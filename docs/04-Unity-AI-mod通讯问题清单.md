# 04 · Unity / AI mod 通讯问题清单

> **目的**：把 Unity（尤其 AI mod）里做 LLM 流式通讯的约束、坑与实证集中列出，作为设计输入。
> **口径**：Unity 2021.2+，netstandard2.1，Mono 优先 / IL2CPP 兼容，桌面 Windows 优先，WebGL 非 M1。
> **日期**：2026-11-06

---

## 1. 环境约束

| 维度 | 约束 | 影响 |
|---|---|---|
| 运行时 | Unity Mono / IL2CPP | `HttpClient`/TLS/线程行为随平台变化；AOT 裁剪 |
| API | `netstandard2.1` | 无 .NET 6+ API（`Task.WaitAsync`、`Stopwatch.GetElapsedTime`、`Lock`） |
| 引擎版本 | 由游戏锁定，不能升级 | 2021.3 无 HTTP/2；WebGL 限制固定 |
| 线程 | Unity API 仅主线程 | 网络回调/解析必须 marshaling；不能阻塞主线程 |
| 生命周期 | 暂停/切场景/退出/域重载 | 长连接、后台线程、静态状态需清理 |
| 多 mod | 多个 mod 共存 | 全局静态/监听器/连接名冲突 |
| 网络环境 | 代理/VPN/企业网/自签 | UWR 只有系统代理；显式代理需 HttpClient |
| 测试 | CI 无网、无 key | 传输必须可 mock；否则只能实机 |

---

## 2. 传输方案对比

| 能力 | UWR + `DownloadHandlerBuffer` | UWR + `DownloadHandlerScript` | HttpClient（Unity） | RawTcp/SslStream | .NET 8 后端 |
|---|---|---|---|---|---|
| 真流式 | ❌ 整包 | ✅ 逐块 | ⚠️ 平台差异 | ✅ | ✅ |
| SSE | 事后解析 | 增量 | 增量 | 增量 | 增量 |
| chunked | 引擎处理 | 引擎处理 | 引擎处理 | 自解 | 引擎处理 |
| WebSocket | ❌ | ❌ | ⚠️ `ClientWebSocket` | 自实现 | ✅ |
| HTTP/2 | ❌（2021） | ❌（2021） | ⚠️ | ❌ | ✅ |
| 显式代理 | ❌ 仅系统 | ❌ 仅系统 | ✅ handler | ❌ | ✅ |
| TLS 证书 | `certificateHandler` | 同左 | handler | 自实现 | ✅ |
| 取消 | `Abort()` | `Abort()` | token（可能不彻底） | `Close()` | ✅ |
| 超时分层 | 整请求 | 整请求 | 可做 | 自实现 | ✅ |
| 主线程 | 回调主线程 | 回调主线程 | 后台线程 | 后台线程 | 与 Unity 无关 |
| IL2CPP | ✅ | ✅（需 `link.xml`） | ⚠️ | ✅ | N/A |
| WebGL | ✅ | ✅ | ❌ | ❌ | N/A |
| 测试 | 需实机 | 需实机 | 可 mock handler | 可 mock | 可 mock |

**结论**：Unity 前端首选 **UWR + DownloadHandlerScript**；显式代理/自签/HTTP2 需求走 **HttpClient**；RawTcp 只做兜底；后端不在本组件目标内。

> **口径订正**：
> - `com.utilities.rest` 的 SSE 通道不是 `DownloadHandlerScript`，而是逐帧重读 `downloadHandler.text` 全量串 + 字符索引增量解析（长流 O(n²)）；只参考其请求/释放封装，不作为 SSE 流式实现。
> - `mikito` 的 `stream` 分支实为 `HttpClient + ReadLineAsync`，不是 UWR；UWR 版在 master/coroutine 分支，不能拿它当 UWR 证据。

---

## 3. 平台事实与坑（带来源）

### 3.1 `DownloadHandlerScript.ReceiveData` 在主线程回调

- 数据先进入临时 ring buffer；每帧最多回调一次，一帧内可多次；多个 datagram 会合并。
- 来源：https://docs.unity.com/en-us/engine/6000.5/script-reference/unityengine/networking/downloadhandler/receivedata
- **设计含义**：`ReceiveData` 只做 `byte[]` 入队；UTF-8/SSE 解析放后台线程，避免卡主线程。

### 3.2 `UnityWebRequest.timeout` 语义不可靠

- 文档写 “if no response is received”，示例却按整请求 60 秒演示。
- 来源：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest-timeout.html
- **设计含义**：SSE 场景设 `timeout = 0`，自己实现首字节/空闲两个看门狗。

### 3.3 代理：2021.3 只有系统代理

- 文档：UnityWebRequest 在请求发出后应用设备代理设置；没有显式 proxy API。
- 来源：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest.html
- **设计含义**：企业代理/指定 proxy 只能走 HttpClient，或本地转发。

### 3.4 TLS 证书可接管

- `certificateHandler` 可接受自签/企业根证书；默认校验系统根。
- 来源：https://docs.unity3d.com/2021.3/Documentation/ScriptReference/Networking.UnityWebRequest-certificateHandler.html

### 3.5 HTTP/2：Unity 2021 没有

- Unity 6.3 才把 UWR 默认切到 HTTP/2。
- 来源：https://docs.unity.com/en-us/engine/6000.3/manual/whats-new/unity63
- **设计含义**：2021.3 只能 HTTP/1.1 chunked；HTTP/2 不在 M1 承诺。

### 3.6 IL2CPP 会剥离 `DownloadHandlerScript` 的虚方法

- Unity 修复记录：“Prevent virtual method in DownloadHandlerScript from being stripped.”
- 来源：https://unityreleases.com/issues?area=Networking&sort=days-desc&status=fixed
- **设计含义**：需要 `link.xml` 或 `[Preserve]` 保住 `ReceiveData` / `CompleteContent` 重写。

### 3.7 WebGL 是另一套世界

- `HttpClient` / `WebClient` / `HttpWebRequest` 未实现且可能产生不可恢复的浏览器挂起；`SslStream`、原始 socket 不支持；`Task.Run` / `Task.Delay` / `Timer` 不工作。
- 来源：https://docs.unity.com/en-us/engine/6000.3/manual/platform-specific/webgl/develop/web-dotnet-api-support
- **设计含义**：M1 不承诺 WebGL；M2 用 `.jslib` + fetch。

### 3.8 连接失败时 `responseCode == 0`

- 不能只看状态码分类；必须结合 `UnityWebRequest.result`（`ConnectionError` / `ProtocolError` / `DataProcessingError`）与 `error` 字符串。
- 实证：Yaolao 的 `LlmTransportHost.cs` 注释 “Bug 4: ConnectionError → responseCode=0, ClassifyStatus(0)=Success (wrong)”。

### 3.9 gzip 待实机确认

- Unity 不建议手动设置 `Accept-Encoding`；`DownloadHandlerScript.ReceiveData` 收到的数据是否已由 libcurl 解压，需在目标版本抓包验证。
- **设计含义**：SSE 通常 identity；错误体可能 gzip；先按“已解压”设计，实机校验。

### 3.10 WebSocket 的本地实证

- `说书人测试/mods/StorytellerPerception/src/Frontend/PerceptionWsClient.cs` 在 netstandard2.1 Unity 前端使用 `System.Net.WebSockets.ClientWebSocket` 连接本地 `ws://127.0.0.1:8000/ws`；
- 包含身份帧、有界发送队列、重连锁存/超时恢复、收信循环、Close 处理；
- **结论**：桌面 Unity Mono + netstandard2.1 下本地 `ws://` 可用；但 `wss://`（TLS/证书）、代理、IL2CPP、WebGL 均未验证，不能直接宣称“WebSocket 全平台支持”。

---

## 4. AI mod 特有问题

### 4.1 主线程

- Unity API 只能在主线程；`ReceiveData` 本身在主线程。
- UI 更新、GameObject 操作、日志输出都需主线程。
- 解析/JSON 聚合若放在 `ReceiveData` 里，会拉长主线程帧时间。

### 4.2 生命周期

- 游戏暂停、切场景、退出、域重载：长连接必须取消/关闭；后台线程/静态队列要清理。
- 多个 mod 共存：避免全局静态单例、固定端口、全局事件总线冲突。

### 4.3 取消

- `UnityWebRequest.Abort()` 是可靠的中止手段；`CancellationToken` 需要桥接到 `Abort()`。
- 取消后仍可能收到残留 `ReceiveData` 回调，需要状态位丢弃。
- 只取消“读取”而不 Abort，会让请求继续跑、继续计费。

### 4.4 超时

- UWR 的 `timeout` 不能区分连接/首字节/空闲；SSE 需要两个看门狗：
  - 首字节超时：从 Send 到第一块字节；
  - 空闲超时：两块字节之间的最大间隔。
- 超时后必须 `Abort()`，否则请求可能悬挂。

### 4.5 错误分类

- 连接错误 `responseCode=0`；HTTP 错误有状态码；协议错误可能是 `ProtocolError`/`DataProcessingError`；流内错误可能是 200 + error payload。
- 需要统一 `TransportError`：Connect / HttpStatus / Protocol / Parse / IdleTimeout / Cancelled / Aborted。

### 4.6 测试

- Unity 实机测试成本高；CI 无网、无 key。
- 传输必须可注入 mock；SSE/解析必须能在纯 .NET 单测里跑。
- 需要录制的 SSE fixture（跨 chunk 边界）用于回归。

---

## 5. 对 UnityLlmTransport 的硬性要求

1. **UWR 流式实现**：`DownloadHandlerScript`，`ReceiveData` 只入队；
2. **主线程分发**：后台解析 → 主线程事件；
3. **取消**：`CancellationToken` → `Abort()`；残留回调丢弃；
4. **分层超时**：首字节 / 空闲，独立看门狗；不依赖 UWR timeout；
5. **错误分类**：结合 `result`、`responseCode`、`error`、错误体；
6. **可替换**：Abstractions 不引用 UnityEngine；UWR/HttpClient/RawTcp 可切换；
7. **可测试**：mock transport + SSE fixture；
8. **生命周期**：取消令牌绑定游戏/域；无泄漏；
9. **IL2CPP**：`link.xml` / `[Preserve]` 保住 handler 重写；
10. **能力声明**：代理/TLS/HTTP2/WebSocket 是否支持，显式告诉上层。

---

## 6. 最小验证清单

- [ ] `ReceiveData` 是否逐帧增量到达（而非结束后一次性）；
- [ ] `ReceiveData` 收到的数据是否已解 gzip；
- [ ] `Abort()` 后连接是否在一帧内停止、是否还有残留回调；
- [ ] `responseCode == 0` 时 `result` / `error` 的实际取值；
- [ ] IL2CPP 构建 + `link.xml` 验证 `DownloadHandlerScript` 重写未被剥离；
- [ ] 首字节/空闲超时在真实弱网/代理下的行为；
- [ ] 主线程解析 vs 后台解析的帧时间对比。
