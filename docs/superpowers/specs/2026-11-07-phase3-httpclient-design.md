# Phase 3 · HttpClient 设计 spec

> **状态**：设计稿，待用户 review。
> **阶段**：Phase 3（HttpClient 传输 / 桌面与 .NET 路径 / 显式代理与 TLS）。
> **输入**：`docs/00-核心需求.md` R11–R13、`docs/03-pi通讯需求矩阵.md` T11–T14、`docs/10-架构设计.md` §6/§8.6/§8.7、`docs/11-开发阶段划分.md` §5、`docs/spike/proxy-tls-matrix.md`、Phase 1/2 spec 与最终代码。
> **参考**：pi `packages/ai/src/utils/provider-retry.ts`、`error-body.ts`（行为已在 Phase 1 的 Retry/错误契约里）；pi.net `Api/ApiRegistry.cs` + `Api/OpenAiCompletions.cs` 的 `HttpClient?` 注入与 `ResponseHeadersRead` 用法（仅 C# 映射参考）。
> **日期**：2026-11-07

---

## 1. 背景与目标

Phase 0 已实测（Windows .NET 10 探针）**HttpClient**：

- 系统代理自动吃 WinINET 设置（含 `127.*` bypass），显式 `WebProxy` 生效且不尊重 bypass；
- 正常 CA / 系统代理 / 显式代理均可达；
- 自签默认在握手层拒绝（`AuthenticationException: UntrustedRoot`），`ServerCertificateCustomValidationCallback` 可绕过；
- 反证格（`google.com` 直连挂/走代理通）证明代理真实生效。

UWR 的边界（Phase 2 已钉）：无显式代理/关闭代理、无客户端证书/自定义根、`RequestFinalizer` 不声明、首字节前超时需适配器自建。HttpClient 正是补齐这些能力的第二实现，服务：

1. 桌面 Unity / .NET 后端/编辑器（显式代理、TLS 例外、HTTP 头完整可控）；
2. 需要 `RequestFinalizer` 的签名路径（SigV4/ADC/Azure AD，Phase 4）；
3. 与 UWR 的字节级契约对照（同一 SSE 字节 → 同一 `SseItem` 序列）。

目标：在 **netstandard2.1 纯 .NET、零 UnityEngine** 的包里实现 `HttpClientTransport`，复用 Phase 1 全部装饰器/错误契约，并在三版本 Unity Mono desktop 实机冒烟。

---

## 2. 范围

### 2.1 包含

| 交付 | 内容 |
|---|---|
| `UnityLlmTransport.HttpClient` | netstandard2.1、零 UnityEngine：`HttpClientTransport`、默认 handler 工厂（proxy/TLS/redirect/decompress）、session/流包装、错误分类 |
| `tests/UnityLlmTransport.HttpClient.Tests` | net10 xUnit：进程内 HTTP/HTTPS/代理测试服务 + 全行为矩阵 |
| `spikes/Phase3TestServer` | net10 Kestrel 测试服务（HTTP/HTTPS 自签/显式代理），供 Unity harness 使用 |
| `unity/Phase3Harness` | `Phase3Runner` + `Phase3Build` + `assemble-phase3-unity.sh`；三版本 Mono desktop 冒烟 |
| 结果文档 | `docs/spike/phase3-results.md` + `docs/spike/evidence/phase3/` |

### 2.2 不包含

- HTTP/2 **声明**（netstandard2.1/Mono 下未验证，`Http2=false`；只记录响应版本观察）；
- IL2CPP 支持**声明**（HttpClient 在 IL2CPP 下未承诺；本阶段只跑 Mono）；
- 自定义根证书/协议版本控制（`CustomRoots`/`Protocols` → `Unsupported`）；
- 流式上传（`BodyFactory` → `Unsupported`，与 Phase 2 一致）；
- WebSocket/WS→SSE 回退（Phase 5）；认证/签名（Phase 4）；录制回放/打包（Phase 6）；
- 修改 Abstractions/Sse/UWR 公共形状。

---

## 3. 包与文件结构

```
src/UnityLlmTransport.HttpClient/
  UnityLlmTransport.HttpClient.csproj       # netstandard2.1 / LangVersion 9 / Nullable / ImplicitUsings disable
  Internal/IsExternalInit.cs                # record/init shim；装配 Unity 时删除（保留 Abstractions 的唯一份）
  HttpClientTransport.cs                    # IStreamingHttpTransport；默认每请求 handler 或注入 client
  HttpClientTransportOptions.cs
  HttpClientSession.cs                      # internal：status/headers/Body/Abort/Dispose
  HttpResponseReadStream.cs                 # internal：读错误→TransportException，Abort 后读→Aborted
  HttpClientRequestBuilder.cs               # internal：URI/method/headers/content/redirect 规则
  HttpClientErrorClassifier.cs              # internal：异常→TransportException 映射
  DefaultHttpClientHandlerFactory.cs        # internal：Proxy/TLS/Redirect/Decompress → HttpClientHandler

tests/UnityLlmTransport.HttpClient.Tests/
  UnityLlmTransport.HttpClient.Tests.csproj  # net10 xunit + FrameworkReference Microsoft.AspNetCore.App（进程内 Kestrel，测试专用）
  HttpClientTransportTests.cs
  HttpClientTimeoutTests.cs
  HttpClientProxyTlsTests.cs
  HttpClientContractTests.cs

spikes/Phase3TestServer/
  Phase3TestServer.csproj                    # net10 Kestrel console
  Program.cs

unity/Phase3Harness/
  README.md
  Assets/Scripts/Phase3Runner.cs
  Assets/Editor/Phase3Build.cs
scripts/assemble-phase3-unity.sh
```

---

## 4. 公共类型面

### 4.1 传输

```csharp
public sealed class HttpClientTransport : IStreamingHttpTransport
{
    public HttpClientTransport();
    public HttpClientTransport(HttpClientTransportOptions options);

    public TransportCapabilities Capabilities { get; }
    public Task<ITransportSession> OpenAsync(
        TransportRequest request,
        TransportOptions options,
        CancellationToken cancellationToken);
}

public sealed record HttpClientTransportOptions
{
    /// <summary>注入共享 HttpClient（复用连接池）。非 null 时 per-request Proxy/Tls/Redirects/Decompress 只允许默认值，否则 Unsupported。</summary>
    public HttpClient? Client { get; init; }

    /// <summary>非 null 时覆盖传输默认能力声明（仅对注入 Client 的高级用法）。</summary>
    public TransportCapabilities? CapabilitiesOverride { get; init; }
}
```

- 默认（`Client == null`）：**每个 `OpenAsync` 按 `TransportOptions` 创建一套 handler + HttpClient**，会话 `Dispose` 时释放；不保跨请求连接池。
- 注入 `Client`：不新建/不释放 client；per-request 的 `Proxy/Tls/Redirects/Decompress` 只接受默认值（见 §5.8）；`ConnectTimeout/FirstByteTimeout/IdleTimeout` 仍生效。
- 不实现 `IDisposable`：注入 client 的生命周期归调用方；默认路径每会话自管。

### 4.2 能力声明

```
StreamingBody | SystemProxy | ExplicitProxy | ProxyAuthentication
| TlsCertificateHandler | Compression | RequestFinalizer
```

**不声明**：`WebSocket`、`Http2`。

- `RequestFinalizer = true`：finalizer 在 `SendAsync` 之前运行；HttpClient 的最终 header 可由 finalizer 控制（`docs/10` §8.7 允许 HttpClient/RawTcp 做签名）。
- `Http2 = false`：本阶段不验证、不承诺；响应版本只记录，不暴露为能力。
- `Compression`：默认 handler 在 `Decompress=true` 时 `GZip|Deflate` 自动解压；注入 Client 时由 `CapabilitiesOverride` 纠正。
- **注入 Client 的能力诚实**：若注入的 client/handler 未按默认映射配置（例如没开自动解压、没允许显式代理），调用方**必须**设置 `CapabilitiesOverride`；否则 `Capabilities` 与实现可能不一致，责任在调用方（文档写死）。

---

## 5. 行为

### 5.1 请求构建

- `new HttpMethod(request.Method)` / `new Uri(request.Url)` 失败 → `TransportException(Protocol)`；
- `BodyFactory != null` → `Unsupported`；
- `Body.Length > 0` → `ByteArrayContent`；否则不挂 content；
- headers：
  - 值为 `null` → 不添加（抑制我们加的默认头；引擎/客户端默认头见偏离表）；
  - 内容头（`Content-Type`/`Content-Length`/`Content-Encoding`/`Content-Language`/`Content-Location`）→ content headers（有 content 时）；
  - 其余 → `request.Headers.TryAddWithoutValidation`（`Host` 等受控头允许）；
  - `Content-Length` 由实际 body 决定，调用方给的值不覆盖实际长度（记录偏离）。
- 不设置 `HttpRequestMessage.Version`；不声明 HTTP/2。

### 5.2 默认 handler 映射（`Client == null`）

| `TransportOptions` | 映射 |
|---|---|
| `Proxy == null` 或 `Mode == System` | `UseProxy = true`，`Proxy = null`（系统代理，尊重系统 bypass） |
| `Mode == None` | `UseProxy = false` |
| `Mode == Explicit` | `Proxy = new WebProxy(Url)`；`Credentials` → `WebProxy.Credentials`（显式代理**不尊重 bypass**，Phase 0 观察） |
| `Tls == null` 或 `ValidateCertificate == true` | 默认校验 |
| `ValidateCertificate == false` | `ServerCertificateCustomValidationCallback = (_,_,_,_) => true` |
| `ClientCertificate != null` | `handler.ClientCertificates.Add(cert)` |
| `CustomRoots != null` 或 `Protocols != null` | `Unsupported` |
| `Redirects == Follow` | `AllowAutoRedirect = true` |
| `Redirects == Error` | `AllowAutoRedirect = false`；传输对 3xx 抛 `Protocol`（与 Phase 2 一致） |
| `Decompress == true` | `AutomaticDecompression = GZip | Deflate` |
| `Decompress == false` | `AutomaticDecompression = None` |

- 新建的 `HttpClient.Timeout = Timeout.InfiniteTimeSpan`（超时统一由 `ConnectTimeout` CTS + Phase 1 看门狗管理，避免引擎默认 100s 杀长流）。

### 5.3 超时

- **`ConnectTimeout`**：适配器在 `SendAsync`（到响应头）期间用 linked CTS `CancelAfter`；到期且调用方 token 未取消 → `TransportException(Connect, "connect/headers timeout", IsRetryable=true)`；
- **`FirstByteTimeout`/`IdleTimeout`**：由 Phase 1 `TimeoutTransport` 负责。HttpClient 的 `OpenAsync` 在**响应头**返回（body 未读），因此首字节计时语义正确（比 UWR 好在适配器等首字节）；
- `MaxResponseBytes`：Phase 1 `TimeoutTransport` 负责。

### 5.4 OpenAsync

1. 构建 `HttpRequestMessage`；
2. 默认路径创建 handler/client；注入路径直接用 client；
3. `SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token)`；
4. 成功后快照 `StatusCode`、合并 `response.Headers` + `response.Content.Headers` 为 `TransportHeaders`；
5. **任意状态码都返回会话**（含 4xx/5xx；错误体由 `Body` 读，`RetryTransport` 在外层按状态分类）；
6. 异常 → `HttpClientErrorClassifier` 映射为 `TransportException`；
7. 返回 `HttpClientSession`（body = `ReadAsStreamAsync()` 包装）。

### 5.5 会话与 Abort

- `StatusCode`/`Headers` 固定快照；`Body` 为 `HttpResponseReadStream`；
- `Abort()`：在主线程/任意线程都允许（BCL 无 Unity 限制）→ 保护性 `response.Dispose()` + stream 标记 aborted；
- `DisposeAsync()`：Abort → 释放 response/stream → 默认路径释放 client/handler → dispose lifetime registration；
- 生命周期 token 注册（`OpenAsync` 的 token）：取消 → `Aborted/Cancelled` 终止 Body 读并释放 response；
- `HttpResponseReadStream`：
  - 异步/同步读均支持（HttpClient 流原生支持；**主线程勿同步读**写入文档）；
  - 读时 `IOException`/`SocketException` → `TransportException(Protocol, IsRetryable=false)`；
  - `OperationCanceledException` 原样传播；
  - Abort 后读 → `TransportException(Aborted)`；
  - `Dispose` 释放底层 response stream（不重复释放 response）。

### 5.6 错误分类（`HttpClientErrorClassifier` 内部）

| 输入 | 输出 |
|---|---|
| caller token canceled | `Cancelled` |
| ConnectTimeout CTS 触发（caller 未取消） | `Connect`，IsRetryable=true，message 含 `timeout` |
| `AuthenticationException` / SSL 文本 | `Tls`，false |
| `HttpRequestException`/`WebException` 内层 `SocketException` 且文本含 proxy/Tunnel/407 | `Proxy`，false |
| 同上层，其他网络错误 | `Connect`，true |
| `Uri`/`HttpMethod`/header 构建错误 | `Protocol`，false |
| 其他 `HttpRequestException` | `Connect`，true |
| Body 读失败（会话已开始） | `Protocol`，false（不重试） |

- 非 2xx **不是异常**：会话交付，错误体可读；
- `RetryTransport` 在外层统一处理 408/409/429/5xx/`x-should-retry`/`Retry-After`（Phase 1 已实现）。

### 5.7 gzip

- `Decompress=true`：默认 handler 自动解压（`GZip|Deflate`）；SSE/harness 直接消费解压后字节；
- `Decompress=false`：`AutomaticDecompression=None`，调用方看到压缩字节与 `Content-Encoding`；
- **不做** UWR 式魔数嗅探（HttpClient handler 语义明确）；测试需钉住“解压后 `Content-Encoding` 头的呈现”（记录到结果文档，不改变实现）。

### 5.8 注入 Client 的约束

`Client != null` 时：

| per-request 设置 | 行为 |
|---|---|
| `Proxy == null` / `Mode == System` | 允许（由 client 配置决定） |
| `Mode != System` | `Unsupported`（client 的 proxy 已固定） |
| `Tls == null` / `ValidateCertificate == true` 且无可选字段 | 允许 |
| `ValidateCertificate == false` / `ClientCertificate` / `CustomRoots` / `Protocols` | `Unsupported`（请配置 client handler） |
| `Redirects == Follow` | 允许 |
| `Redirects == Error` | `Unsupported` |
| `Decompress == true` | 允许 |
| `Decompress == false` | `Unsupported` |
| `ConnectTimeout/FirstByteTimeout/IdleTimeout` | 正常生效（`ConnectTimeout` 用独立 CTS，不依赖 client.Timeout） |

- 注入的 `HttpClient` 若自身设置了 `Timeout`，可能与 `ConnectTimeout`/长流叠加；调用方应把注入 client 的 `Timeout` 设为 `Timeout.InfiniteTimeSpan`（文档写死）。

---

## 6. Unity 版本适配

- 包是**纯 BCL**：不出现 `UnityEngine`、`UNITY_*`、`Application.unityVersion`；Mono/IL2CPP 差异由 BCL 行为决定；
- 版本差异只允许出现在测试结果/能力声明；不改公共形状；
- 支持定义（docs/05 §6）：`netstandard2.1` 编译 + 三版本 **Mono** 实机冒烟；IL2CPP 本阶段**不声明**（如需承诺另立阶段验证）；
- `Http2=false`：三版本都可能是 HTTP/1.1；结果文档只记录 `HttpResponseMessage.Version` 观察值，不进入能力。

---

## 7. 测试计划

### 7.1 dotnet 自动化（无外网、无 key）

测试工程内起 `HttpListener`/Kestrel 进程内服务（测试专用 `FrameworkReference Microsoft.AspNetCore.App`）：

| 组 | 场景 |
|---|---|
| 流式 | `/sse` 5 段 100ms：首 item 早于结束；`open` 在响应头即返回；与 fixture 一致 |
| 状态/头 | 2xx/500/404 都返回会话；status/headers（含 content-type、大小写不敏感）；错误体可读 |
| 取消 | 中途 cancel → Body 读 `Cancelled/Aborted`；无悬挂 |
| 超时 | 慢响应头 → `ConnectTimeout`=`Connect`；慢首字节 / 慢块 → `FirstByteTimeout`/`IdleTimeout`（经 `TimeoutTransport`） |
| 溢出 | `/big` + `MaxResponseBytes` → `Overflow`（经 `TimeoutTransport`） |
| gzip | `Decompress=true` 得明文；`false` 得压缩字节（记录头呈现） |
| 重定向 | Follow 自动跟随；Error → `Protocol`；`Location` 用例 |
| headers | null 抑制；内容头路由；`Content-Length` 由 body 决定；重复/非法头 |
| 错误 | `BodyFactory`/非法 URI/method → `Unsupported`/`Protocol` |
| 注入 client | 默认 options 允许；非默认 Proxy/Tls/Redirect/Decompress → `Unsupported` |
| 代理 | 本地显式代理服务返回标记 → `WebProxy` 生效；target 不可直达也可通过 |
| TLS | 进程内自签 Kestrel：默认拒绝 → `Tls`；`ValidateCertificate=false` → 200；客户端证书可选 |
| 契约 | 同一 SSE fixture：`HttpClientTransport` 与 Phase 1 `MockTransport` 产出相同 `SseItem`；与 Phase 2 UWR 记录条目一致 |

### 7.2 Unity Phase3 harness（三版本 × Mono）

`Phase3TestServer` 提供 HTTP/HTTPS 自签/显式代理端口；`Phase3Runner` 自动跑：

| ID | 场景 | 判据 |
|---|---|---|
| H1 | `/sse` 流式 | ≥2 次回调；首 item < 总时长；items 与 fixture 一致 |
| H2 | 取消 | `Cancelled/Aborted`；无悬挂 |
| H3 | 首字节/空闲超时 | 经 `TimeoutTransport` → 对应 Kind |
| H4 | 500 错误体 | status=500；body 含 boom |
| H5 | gzip | items 与 `/sse` 相同 |
| H6 | 重定向 | Follow 成功；Error → `Protocol` |
| H7 | TLS | 自签默认 → `Tls`；`ValidateCertificate=false` → 200 |
| H8 | 显式代理 | 本地代理标记可达（系统代理对照记录） |
| H9 | headers/线程 | 头大小写；body 消费在非主线程（记录线程 id） |

- 三版本（2021.3.45f1 / 6000.3.28f1 / 6000.6.8f1）各跑一次 Mono；结果写 `docs/spike/phase3-results.md` + 原始 evidence；
- IL2CPP 不跑（不声明）。

### 7.3 门禁

- `dotnet build UnityLlmTransport.sln -c Release` 0 错误；
- `dotnet test tests/UnityLlmTransport.HttpClient.Tests/...` 全绿；
- 三版本 Mono H1–H9 全过（结果文档 + evidence）；
- 同一 SSE fixture 与 UWR 记录条目一致（契约）；
- 不修改 UWR/Abstractions/Sse 公共形状。

---

## 8. 偏离表（pi / pi.net / 我们）

| 项 | pi 原版 | pi.net | 我们 | 类别 | 原因/代价 |
|---|---|---|---|---|---|
| HTTP 注入 | fetch | `HttpClient?` 注入 | 注入 `HttpClient` 或默认每请求 handler | B | 连接池可由注入 client 复用；默认路径不保池（代价） |
| per-request 代理/TLS | fetch 实现 | handler 配置 | 默认路径按 `TransportOptions` 建 handler；注入 client 时只允许默认值 | B | 统一选项与 handler 配置的二选一，冲突显式 Unsupported |
| 超时 | `timeoutMs` | 调用方 CTS | Connect→headers CTS；首字节/空闲→core 看门狗 | B | 比 UWR 更接近 pi 语义（Open 在响应头返回） |
| 流式上传 | fetch body 流 | 可注入 StreamContent | `BodyFactory` → Unsupported | C | pi 请求体均为可重放 JSON；后可补 |
| 重定向 | 手动/跟随 | handler | Follow/Error；Error 抛 `Protocol` | B | 与 Phase 2 一致 |
| gzip | fetch 自动 | handler | handler 自动（默认）；无嗅探 | B | UWR 行为不同，已分别订正 |
| HTTP/2 | fetch 协商 | .NET 默认 | 不声明（`Http2=false`） | C | netstandard2.1/Mono 未验证 |
| IL2CPP | — | — | 不声明 | C | 本阶段只承诺 Mono |
| 同步 Body 读 | 同步流 | 同步流 | 支持但文档禁止主线程同步读 | B | HttpClient 原生支持，不强改 async-only |
| 自定义根证书/协议 | SDK 可配 | handler | Unsupported | C | netstandard2.1/Mono 稳定实现风险；Phase 6 再议 |
| `RequestFinalizer` | — | — | 声明 true | B | finalizer 在 SendAsync 前；Phase 4 SigV4 依赖 |

---

## 9. 验收标准

- [ ] 包 `netstandard2.1` 编译 0 错误，零 `UnityEngine`；
- [ ] dotnet 测试全绿（流式/取消/超时/溢出/gzip/重定向/headers/错误/代理/TLS/注入约束/契约）；
- [ ] 三版本 Unity Mono H1–H9 全过，结果与 evidence 落盘；
- [ ] 与 UWR 的同一 SSE fixture 条目一致；
- [ ] `Capabilities` 与实测一致（`Http2=false`、`RequestFinalizer=true`、`Compression` 按 Decompress）；
- [ ] `docs/05` 包清单/SOP/README 更新；
- [ ] 不修改 `PiUnityAgent.Ai`/Abstractions/Sse/UWR 公共形状。

---

## 10. 后置与风险

| 项 | 处理 |
|---|---|
| HTTP/2 | Phase 6 真实端点验证后再考虑能力声明 |
| IL2CPP | 若后续要承诺，单独立项验证（TLS/代理/线程） |
| 自定义根证书/协议 | Phase 6 或用户需求驱动 |
| 流式上传 | 需求出现再补 `BodyFactory` |
| 注入 client 的连接池/能力一致性 | 文档写死“调用方负责 handler 与能力声明” |
| gzip 头呈现差异 | 测试记录实际行为，不改变实现 |
| 代理判断（系统/显式是否真走代理） | 用 Phase 0 的“不可直达目标”试金石法，避免只看 200 |

**必须由实机门禁回答的风险**：

1. Unity Mono 下 HttpClient 的 SSE 逐块流式与取消（BCL 版本差异）；
2. 三版本 Mono 的自签默认拒绝/accept-all 行为是否一致；
3. 显式 `WebProxy` 在 Unity Mono 下是否生效（Phase 0 只测 .NET 10）；
4. `ResponseHeadersRead` 在 Unity Mono 下是否真的在响应头返回（而不是整包缓冲）；
5. gzip 自动解压后 `Content-Encoding` 是否保留（记录，不承诺）。

---

## 11. 参考

- Phase 0：`docs/spike/proxy-tls-matrix.md`（HttpClient 系统/显式代理、自签 callback、判别格）
- 架构：`docs/10-架构设计.md` §6/§8.6/§8.7
- 阶段：`docs/11-开发阶段划分.md` §5
- pi：`pi-study/pi/packages/ai/src/utils/provider-retry.ts`、`error-body.ts`
- pi.net：`pi-study/pi.net/src/Pi.Ai/Api/ApiRegistry.cs`（`HttpClient?` 注入形状）、`Api/OpenAiCompletions.cs`（`ResponseHeadersRead`）
- Phase 1/2 契约：`docs/superpowers/specs/2026-11-07-phase1-core-design.md`、`2026-11-07-phase2-uwr-design.md`
