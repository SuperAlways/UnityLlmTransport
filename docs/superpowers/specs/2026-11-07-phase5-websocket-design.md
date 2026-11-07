# Phase 5 · WebSocket 核心（传输侧）设计 spec

> **状态**：设计稿，待用户 review。
> **阶段**：Phase 5（仅本仓库：通用 WebSocket 传输适配器；不含任何消费方集成/回退逻辑）。
> **输入**：`docs/00-核心需求.md` R17、`docs/03-pi通讯需求矩阵.md` T5、`docs/10-架构设计.md` §8.3、`docs/11-开发阶段划分.md` §7、Phase 1 WebSocket 形状、`docs/spike/websocket-matrix.md`（Phase 0 事实）。
> **日期**：2026-11-07

---

## 1. 背景与目标

Phase 0 已实测（`docs/spike/websocket-matrix.md`）：

- Unity 2021.3 / 6.3 / 6.6 的 Mono 与 IL2CPP 都能用 **`System.Net.WebSockets.ClientWebSocket`** 连本地 `ws://`、收 Close/回握手、Abort；
- 远程 `wss://`（Postman echo）在系统代理+系统信任 CA 下通过；
- 非 ws 端点/错误端口 `WebSocketException: Unable to connect` 可分类；
- `HttpListener` 服务端在客户端不完成 close 握手时可能异常退出（Phase 0 已修服务端 per-connection try/catch）——客户端协议必须能正常 Close/Abort。

Phase 1 已定义 WebSocket 形状（`IWebSocketTransport`/`IWebSocketSession`/`WebSocketMessage`/Selector），但**没有实现**。Phase 5 的目标：把该形状实现为**通用传输适配器**，供消费方（PiUnityAgent 或其他）自行接线；不实现 WS→SSE 回退、不实现 provider 语义、不做消费方联调。

---

## 2. 范围

### 2.1 包含

| 交付 | 内容 |
|---|---|
| `UnityLlmTransport.WebSocket` | netstandard2.1、零 UnityEngine：`ClientWebSocketTransport : IWebSocketTransport` + session（基于 `ClientWebSocket`） |
| 行为 | ws/wss 连接、发送 Text/Binary、接收分帧消息（EndOfMessage）、Close、Abort、取消、连接/空闲超时、消息体上限、keepalive |
| 能力 | `WebSocket`、`WebSocketOverTls`、`SystemProxy`、`TlsCertificateHandler` |
| 测试 | dotnet：进程内 ws/wss echo/分帧/Close/Abort/超时/上限；Unity：四版本 Mono + IL2CPP 本地 ws/Close/Abort + 远程 wss |
| 文档 | `docs/05` WebSocket 行、`docs/11` §7 范围治理、README、结果/证据 |

### 2.2 不包含（硬边界）

- **WS→SSE 回退**：属消费方协议层（它同时知道 HTTP/WS 会话）；本组件只提供 `IWebSocketTransport` + Selector 形状。
- **消费方集成/联调/门禁**（PiUnityAgent 或其他）；
- provider 事件 JSON 语义（Codex/OpenAI 等）；
- `WebSocketCached` 会话缓存/复用（本阶段不支持，Selector 选中即 `Unsupported`）；
- 自动重连（消费方策略）；
- **RawTcp 兜底**（本阶段不做；后续按需单独立项）；
- **WebGL**（M2；`ClientWebSocket` 不适用）；
- 显式代理/代理认证（ClientWebSocket 无关闭/显式代理语义；SystemProxy 之外 → `Unsupported`）；
- 自定义根证书/协议版本（`TlsOptions.CustomRoots`/`Protocols` → `Unsupported`）。

---

## 3. 包与文件结构

```
src/UnityLlmTransport.WebSocket/
  UnityLlmTransport.WebSocket.csproj     # netstandard2.1 / LangVersion 9 / Nullable / ImplicitUsings disable
  Internal/IsExternalInit.cs
  Internal/AssemblyInfo.cs               # InternalsVisibleTo("UnityLlmTransport.WebSocket.Tests")
  ClientWebSocketTransport.cs
  ClientWebSocketSession.cs              # internal
  WebSocketErrorClassifier.cs            # internal
  WebSocketBufferBudget.cs               # internal：消息体上限累计
tests/UnityLlmTransport.WebSocket.Tests/
  UnityLlmTransport.WebSocket.Tests.csproj  # net10 xunit + Phase3TestSupport? 独立 WsTestSupport
  WsTestSupport.cs                        # 进程内 Kestrel ws/wss echo/分帧/Close/Abort
  ClientWebSocketTransportTests.cs
  ClientWebSocketSessionTests.cs
  WebSocketTlsTests.cs
unity/Phase5Harness/
  README.md
  Assets/Scripts/Phase5Runner.cs
  Assets/Editor/Phase5Build.cs
scripts/assemble-phase5-unity.sh
```

> 测试服务：dotnet 用进程内 `WsTestSupport`（Kestrel `UseWebSockets` + 自签 wss）；Unity harness 复用现有 `spikes/WsTestServer`（HttpListener，5200）与远程 `wss://ws.postman-echo.com/raw`。不新增外部服务进程。

---

## 4. 公共类型面

```csharp
public sealed class ClientWebSocketTransport : IWebSocketTransport
{
    public ClientWebSocketTransport();

    public TransportCapabilities Capabilities { get; }
    public Task<IWebSocketSession> ConnectAsync(
        WebSocketRequest request,
        WebSocketOptions options,
        CancellationToken cancellationToken);
}
```

- 传输本身**无状态**（不持有连接/不重连）；每次 `ConnectAsync` 一个 `ClientWebSocket` + session。
- `ClientWebSocketSession` 为 internal，实现 Phase 1 的 `IWebSocketSession`。
- 不新增公共 options；所有连接参数复用 `WebSocketRequest` / `WebSocketOptions`。

---

## 5. 行为

### 5.1 连接（`ConnectAsync`）

1. `new Uri(request.Url)` 非法 → `TransportException(Protocol)`；scheme 非 `ws`/`wss` → `Protocol`；
2. `new ClientWebSocket()`，映射 `WebSocketOptions`：
   - `KeepAliveInterval` → BCL 同名属性（null 保留引擎默认）；
   - `SubProtocol` → `ClientWebSocketOptions.AddSubProtocol`（重复/非法抛 → `Protocol`）；
   - `Origin` → `SetRequestHeader("Origin", ...)`；
   - `WebSocketRequest.Headers` → `SetRequestHeader`（null 值跳过；引擎默认头不可删除）；
   - `Proxy`：`null`/`System` → 使用系统代理（BCL 默认）；`Explicit`/`None` → `Unsupported`（ClientWebSocket 无显式/关闭代理 API）；
   - `Tls`：`ValidateCertificate=false` → `RemoteCertificateValidationCallback` 接受；`ClientCertificate` → `ClientCertificates.Add`；`CustomRoots`/`Protocols` → `Unsupported`；
   - `Redirects`：仅 `Error`（默认）；`Follow` → `Unsupported`（握手重定向不可控）；
   - `ConnectTimeout` → 独立 CTS `CancelAfter`（到期 → `Connect`，retryable=true）；
   - `IdleTimeout`/`MaxMessageBytes` 供 session 使用。
3. `ConnectAsync(uri, ct)`；`WebSocketException` 分类见 §5.5；
4. 成功返回 session；session 保留 lifetime token 的取消注册（取消 → Abort(`Cancelled`)）。

### 5.2 接收（`IWebSocketSession.ReceiveAsync`）

- 使用内部接收缓冲（默认 16 KiB；`ClientWebSocketOptions.ReceiveBufferSize` 在 netstandard2.1 不可用，不做选项）；
- **逐帧产出** `WebSocketMessage`：Text/Binary 按 BCL 返回的段产出，`EndOfMessage` 原样传递；`[DONE]` 等协议语义由消费方聚合；
- **Close**：收到 `WebSocketMessageType.Close` 时产出一条 `WebSocketMessageType.Close`（带 `CloseStatus`；description 不在 Phase 1 形状中，记录偏离），随后枚举结束；
- `MaxMessageBytes`：跨段累计到 `EndOfMessage`；超限 → `Abort()` + `TransportException(Overflow)`；
- `IdleTimeout`：每次等待下一帧用 linked CTS `CancelAfter`；到期 → `Abort()` + `TransportException(IdleTimeout)`；
- 调用方取消 → Abort(`Cancelled`) 并抛出 `OperationCanceledException`/`TransportException(Cancelled)`（不得静默继续）；
- 同一 session 不支持并发 Receive（第二个枚举者 → `Protocol`）；
- 对端 Close/连接中断 → 枚举结束（正常 Close）或 `TransportException(Protocol)`（异常断线）。

### 5.3 发送（`IWebSocketSession.SendAsync`）

- 仅支持 Text/Binary；`WebSocketMessageType.Close` → `Protocol`（Close 必须走 `CloseAsync`）；
- 发送串行化（`SemaphoreSlim`，BCL 禁止并发 Send）；取消 token 透传；
- 状态非 Open → `TransportException(Aborted)`（或 Cancelled，取决于取消/Abort 来源）；
- 异常按 §5.5 分类。

### 5.4 Close / Abort / Dispose

- `CloseAsync(status, description, ct)`：Open/CloseReceived 时调用 BCL `CloseAsync`；已 Closed/Aborted → 幂等返回/`Protocol`；
- `Abort()`：`ClientWebSocket.Abort()`；标记 terminal，后续 Send/Receive → `TransportException(Aborted)`；
- `DisposeAsync()`：Abort → 释放 `ClientWebSocket` → 释放 lifetime registration；
- 生命周期 token 取消 → Abort(`Cancelled`)，pending Receive/Send 结束；
- 客户端必须尽量完成 Close 握手（正常关闭），Abort 用于异常/取消；服务端可能因 abrupt close 异常（Phase 0 已记录），本组件不负责修复服务端。

### 5.5 错误分类（`WebSocketErrorClassifier` 内部）

| 场景 | 输出 |
|---|---|
| caller token 已取消 | `Cancelled` |
| ConnectTimeout CTS 到期 | `Connect`，IsRetryable=true，message 含 `timeout` |
| `WebSocketException` 内层 `SocketException`/拒连 | `Connect`，true |
| `WebSocketException` 内层 `AuthenticationException`/文本含 SSL/certificate | `Tls`，false |
| 其他 ConnectAsync `WebSocketException` | `Handshake`，false |
| 非法 URI/scheme/subprotocol | `Protocol`，false |
| Send/Receive 中途 `WebSocketException`/`IOException` | `Protocol`，false（流已开始不重试） |
| Abort 后 Send/Receive | `Aborted`，false |
| 对端正常 Close 消息 | 不是异常（Receive 产出 Close 后结束） |

- 非 2xx/HTTP 错误不适用；握手失败统一 Handshake（不暴露 HTTP 状态）。

### 5.6 能力

```
WebSocket | WebSocketOverTls | SystemProxy | TlsCertificateHandler
```

**不声明**：`StreamingBody`、`ExplicitProxy`、`ProxyAuthentication`、`Http2`、`RequestFinalizer`、`WebSocketCached`（无缓存实现）。

### 5.7 Selector / Fallback

- `DefaultTransportSelector` 对 `TransportKind.WebSocket` 选择注册的 `IWebSocketTransport`，检查 `WebSocket` 能力；
- `TransportKind.WebSocketCached` 本阶段未实现 → 注册缺失时 `Unsupported`；
- `FallbackPolicy` 形状已存在；**WS→SSE 回退由消费方协议层实现**，本组件不内建回退。

---

## 6. Unity 版本适配

- 包是纯 BCL（`System.Net.WebSockets.ClientWebSocket`），不出现 UnityEngine/`UNITY_*`/版本判断；
- 四版本基线：`2022.3.62f3c1`（主基线）、`2021.3.45f1`、`6000.3.28f1`、`6000.6.8f1`；
- 声明 Mono + IL2CPP；WebGL 不支持（M2 另立）；
- 不需要 `link.xml`（无 Unity native→managed handler 重写；BCL WebSocket 已在 Phase 0 IL2CPP 实测可用）；
- 版本差异只允许出现在测试结果/能力矩阵，不改公共形状。

---

## 7. 测试计划

### 7.1 dotnet（net10 xunit，无外网）

进程内 `WsTestSupport`（Kestrel `UseWebSockets`；HTTPS 自签用于 wss）：

| 组 | 覆盖 |
|---|---|
| 连接 | ws/wss echo Text/Binary；非法 URL/scheme；握手失败分类 |
| 帧语义 | 分帧 Text 的 EndOfMessage；UTF-8 中文回环；Close 帧产出后结束 |
| Close/Abort | 正常 Close 握手；Abort 后 Send/Receive 抛 Aborted；服务端 abrupt 后客户端清理 |
| 超时 | ConnectTimeout（慢握手/拒连）；IdleTimeout（服务端延迟/无帧）；取消等待 |
| 上限 | MaxMessageBytes 跨分帧累计超限 → Overflow + Abort |
| 能力/选项 | SubProtocol；Origin/headers 到达服务端；SystemProxy 允许；Explicit/None → Unsupported；Redirect Follow → Unsupported；CustomRoots/Protocols → Unsupported |
| TLS | 自签默认 → Tls；ValidateCertificate=false → 200/echo；ClientCertificate 映射（服务端要求客户端证书可后置） |
| Selector | DefaultTransportSelector 选 WebSocket；WebSocketCached 缺注册 → Unsupported |
| 并发 | 并发 Send 串行化；并发 Receive → Protocol |

### 7.2 Unity Phase5 harness（四版本 Mono + IL2CPP）

服务：`spikes/WsTestServer`（`ws://127.0.0.1:5200/ws/`，echo/Close/Abort）+ 远程 `wss://ws.postman-echo.com/raw`。

| ID | 场景 | 判据 |
|---|---|---|
| W1 | 本地 ws 连接 + Text echo | 收到 `echo:hello`；state Open |
| W2 | 本地 ws UTF-8 中文 echo | `你好，世界` 回环无 mojibake；EndOfMessage 正确 |
| W3 | Close | 收到 NormalClosure；客户端 CloseAsync 后 state Closed |
| W4 | Abort | 连接中止；后续操作抛 Aborted；残留回调无 |
| W5 | 远程 wss（Postman echo） | `hello` 回环成功（系统 CA + 系统代理） |
| W6 | 错误端口/非 ws 端点 | `Connect/Handshake` 分类可读 |

- 四版本 Mono 全跑 W1–W6；IL2CPP 跑 W1/W3/W4（本地 ws），远程 wss 按版本记录（Phase 0 已证，可复用但需 2022.3 重跑）；
- 本地 WsTestServer 只支持单条 Text echo + 主动 Close；二进制/分帧/Idle/MaxMessageBytes 由 dotnet `WsTestSupport` 覆盖，不在 Unity harness 强行扩展服务端。
- 结果写 `docs/spike/phase5-results.md` + evidence；WebGL 不测、不声明。

---

## 8. 偏离表（pi / pi.net / 我们）

| 项 | pi 原版 | pi.net | 我们 | 类别 | 原因/代价 |
|---|---|---|---|---|---|
| WS 实现 | fetch/WS 客户端 | ClientWebSocket | ClientWebSocket 适配器 | B | Unity 内置 BCL，Phase 0 已验证 |
| 消费方接线 | SDK | SDK | `IWebSocketTransport` 注入 | B | 不绑定 provider；消费方负责选择/回退 |
| WS→SSE 回退 | SDK 内 | SDK | **不实现** | C | 协议层需要同时知道两种会话；回退在消费方 |
| 缓存/重连 | SDK 可选 | SDK | 不实现（`WebSocketCached` 未支持） | C | 会话复用/重连策略属消费方/后续阶段 |
| 代理 | fetch | handler | 系统代理 only；显式/关闭 → Unsupported | B | ClientWebSocket 无对应 API；不假装支持 |
| TLS | SDK | handler | 默认校验 / accept-all / 客户端证书；自定义根/协议 → Unsupported | B | ClientWebSocketOptions 能力边界 |
| Close 描述 | SDK | SDK | Phase 1 形状只带 CloseStatus，不带 description | B | 形状已钉，避免改动 Abstractions |
| 消息粒度 | 事件 | 事件 | 帧级 `WebSocketMessage` + EndOfMessage；聚合在消费方 | B | 与 Phase 1 形状一致 |
| 消息上限 | SDK | SDK | `MaxMessageBytes` 适配器累计到 EndOfMessage | B | 防长流内存膨胀 |
| WebGL | — | — | 不支持（M2） | C | ClientWebSocket 不适用 |
| RawTcp | — | — | 本阶段不做 | C | UWR/HttpClient 已覆盖；按需另立 |
| Unity 矩阵 | — | — | 四版本 Mono + IL2CPP；无 link.xml | B | 纯 BCL，无 Unity 回调重写 |

---

## 9. 验收标准

- [ ] `UnityLlmTransport.WebSocket` netstandard2.1 编译 0 错误，零 UnityEngine、零第三方依赖；
- [ ] dotnet 测试全绿（连接/分帧/Close/Abort/超时/上限/能力选项/TLS/Selector/并发）；
- [ ] 四版本 Mono W1–W6 + IL2CPP W1/W3/W4；结果与 evidence 落盘；
- [ ] `Capabilities` 与实测一致（WebSocket/WebSocketOverTls/SystemProxy/TlsCertificateHandler；无 Cached/ExplicitProxy）；
- [ ] Selector 能选中 WebSocket；WebSocketCached 缺注册 → Unsupported；
- [ ] 不包含消费方回退/集成代码或门禁；
- [ ] docs/05 WebSocket 行、docs/11 §7 范围（回退移消费方、RawTcp/WebGL 后置）、README、SOP 更新；
- [ ] 不修改 Abstractions/Sse/UWR/HttpClient/Auth 公共形状。

---

## 10. 后置与风险

| 项 | 处理 |
|---|---|
| WS→SSE 回退 | 消费方协议层；本组件只提供 Selector/Fallback 形状 |
| WebSocketCached | 后续按 provider 需求单独立项（缓存/复用语义） |
| 自动重连 | 消费方策略 |
| RawTcp | 按需单独立项；不作为默认 |
| WebGL | M2；`.jslib` 是另一条实现 |
| 显式代理/自定义根证书 | ClientWebSocket 无 API；不声明 |
| 客户端证书服务端验证 | 映射支持；服务端要求客户端证书的端到端门禁可 Phase 6/按需补 |

**必须由实机门禁回答的风险**：

1. `ClientWebSocket` 四版本 Mono/IL2CPP 的本地 ws、Close、Abort 一致性（Phase 0 已验证 2021/6.3/6.6，需补 2022.3）；
2. 远程 `wss://` 在四版本（系统代理/系统 CA）下的连通性与错误分类；
3. `ClientWebSocketOptions.RemoteCertificateValidationCallback` 在 Unity Mono/IL2CPP 下是否生效（自签 wss；Phase 0 未测 WS 自签）；
4. `IdleTimeout` 使用 linked CTS 取消 `ReceiveAsync` 后连接是否可继续/必须 Abort（按 §5.2 固定为 Abort）；
5. `MaxMessageBytes` 跨分帧累计与 Close 帧不被计入（测试覆盖）。

---

## 11. 参考

- Phase 0：`docs/spike/websocket-matrix.md`、`docs/spike/phase0-summary.md` §2.4/§5
- 架构：`docs/10-架构设计.md` §8.3（WebSocket 形状/选择/回退边界）
- 阶段：`docs/11-开发阶段划分.md` §7（本 spec 将回退移消费方、RawTcp/WebGL 后置）
- Phase 1 形状：`docs/superpowers/specs/2026-11-07-phase1-core-design.md` §4.5
- 现有业务客户端（仅参考结构，**不复用/不依赖**）：`说书人测试/mods/StorytellerPerception/src/Frontend/PerceptionWsClient.cs`
