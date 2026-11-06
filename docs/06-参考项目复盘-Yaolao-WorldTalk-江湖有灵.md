# 06 · 参考项目复盘：Yaolao / WorldTalk / 江湖有灵

> **目的**：把三家在 Unity 里做 LLM 通讯的真实做法、踩过的坑、代价拆开，作为 UnityLlmTransport 的设计证据。
> **口径**：只引用本地反编译/参考源码；路径相对 `D:\shuoshu\taiwuask\`。
> **许可证边界**：WorldTalk / 江湖有灵 / Yaolao 均为反编译或学习参考，未见开源 LICENSE；**只能参考模式，不能复制代码**。
> **日期**：2026-11-06

---

## 1. TaiWuYaoLao（说书人早期 / yaolao）

### 1.1 做法

- 接口：`Core/Agent/ILlmClient.cs` 定义 `StreamChatAsync` / `ChatAsync`；
- 实现：`Frontend/Networking/LlmTransportHost.cs`，用 `UnityWebRequest`；
- 流式：`DownloadHandlerBuffer` + 等整包 `rawText`，再 `Split('\n')` 解析 SSE。

关键代码：

```csharp
request.downloadHandler = new DownloadHandlerBuffer();
request.timeout = 120;
...
var rawText = await tcs.Task;
var lines = rawText.Split('\n');
```

### 1.2 踩过的坑（源码证据）

| 坑 | 证据 | 后果 |
|---|---|---|
| **假流式** | `LlmTransportHost.cs:85` `DownloadHandlerBuffer`；`:44-75` 等 `rawText` 后 Split | 生成期间 UI 无输出；长响应占内存 |
| **整请求 timeout** | `:86` `request.timeout = 120` | 长回答可能被 120s 截断；流空闲无法区分 |
| **连接错误误判** | `:112` “Bug 4: ConnectionError → responseCode=0, ClassifyStatus(0)=Success (wrong)” | 网络失败被当成功 |
| **重试只覆盖连接阶段** | `:79` 注释 “Retry on initial connect for transient errors (not during streaming)” | 流中途失败不可恢复 |
| **状态码分类不全** | `:127` `ClassifyStatus((int)request.responseCode)` | 只看状态码，忽略 `result`/`error` |
| **取消粒度粗** | `:93` `request.Abort()`；协程等待中检查 token | 取消后残留回调/状态需额外处理 |
| **测试/验证接口用 15s** | `:257`/`:306` `request.timeout = 15` | 与主流程 120s 不一致，行为分叉 |

### 1.3 教训

1. **`DownloadHandlerBuffer` 不能做流式**；必须 `DownloadHandlerScript`。
2. **超时不能只有一个整请求值**；SSE 要首字节/空闲两个看门狗。
3. **先看 `UnityWebRequest.result`，再看状态码**；`responseCode == 0` 是连接错误。
4. **重试只在首字节前**；流开始后不重试，避免重复内容/重复计费。
5. **接口层（Core）与传输层（Frontend）分离是对的**；但传输实现选错了。

---

## 2. WorldTalk

### 2.1 做法

- 通用 LLM 客户端：`source/frontend/TaiwuAI_Beta.AI.Common/AiApiClient.cs`；
- 流式：`StreamingDownloadHandler : DownloadHandlerScript`，在 `ReceiveData` 里增量解码；
- 请求：`new UnityWebRequest(endpoint, "POST")` + `Accept: text/event-stream`；
- 取消：`Cancel()` → `_currentRequest.Abort()`；
- UI：节流通知（`realtimeSinceStartup`，约 0.1s）。

关键代码：

```csharp
private sealed class StreamingDownloadHandler : DownloadHandlerScript
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _lineBuffer = new StringBuilder();
    protected override bool ReceiveData(byte[] data, int dataLength)
    {
        // 增量 UTF-8 解码；_lineBuffer 累积；DrainCompleteLines()
        // content / reasoning / tool_calls / usage 聚合
        // 节流通知 UI
    }
}
```

### 2.2 踩过的坑 / 代价

| 点 | 证据 | 说明 |
|---|---|---|
| **主线程回调** | `AiApiClient.cs:76` `ReceiveData` | UTF-8 解码 + SSE 行解析 + 聚合都在主线程；重解析会拉长帧时间 |
| **UI 节流** | `:324-332` `_lastContentNotification` 0.1s | 不节流会每个 chunk 刷 UI |
| **整请求 timeout** | `:436` `request.timeout = ConfigManager.Timeout` | 仍是整请求语义；空闲检测需另做 |
| **SSE 解析异常只记日志** | `:237-241` `Debug.LogWarning` | 解析失败可能静默丢内容 |
| **最终仍依赖整包结果** | `:440-470` 等 `SendWebRequest` 完成后构建结果 | 流式回调与最终结果双轨，需保证一致 |
| **代理/TLS 未接管** | 未见显式代理/certificateHandler | 依赖 Unity 后端；企业代理/自签场景可能失败 |

### 2.3 可借鉴

- **UWR + DownloadHandlerScript 在 Unity 里确实可行**；
- **增量 UTF-8 解码 + 行缓冲**是正确姿势；
- **content/reasoning/tool_calls/usage 聚合**的形状可参考；
- **节流通知 UI** 必须做；
- **Abort 取消**可用。

### 2.4 不能直接照搬

- 解析放在 `ReceiveData` 主线程；我们应该“入队 + 后台解析”；
- timeout 仍是整请求；我们要分层看门狗；
- 代理/TLS/错误分类不完整。

---

## 3. 江湖有灵

### 3.1 做法

- Core 目标 `net48`；被前端引用；
- **非流式**：`HttpClient` + `HttpClientHandler`（`CreateHttp`，TLS 1.2、连接数、300s timeout）；
- **流式**：`RawSseStreamAsync`：`TcpClient` → `SslStream` → 手写 HTTP/1.1 请求 → 自解响应头 → `SseBodyReader` 自解 chunked → `SseEventReader` 解析 SSE；
- 超时：连接/握手超时 + 流空闲超时（约 60s 无数据）；
- 取消：`ct.Register(() => tcp.Close())`；
- TLS：`SslStream` + 自定义证书回调；`ServicePointManager.SecurityProtocol |= Tls12`；
- 限制：响应体/行长度上限、回环 endpoint 校验。

关键代码：

```csharp
tcp = new TcpClient();
await tcp.ConnectAsync(connectHost, port);
var stream2 = tcp.GetStream();
ssl = new SslStream(stream2, false, (sender, cert, chain, errors) => ...);
await ssl.AuthenticateAsClientAsync(connectHost);
...
sse = new SseBodyReader(responseHead.Chunked, events.FeedLine);
```

### 3.2 踩过的坑 / 代价

| 点 | 证据 | 说明 |
|---|---|---|
| **放弃 HttpClient 流式** | `OpenAiCompatibleClient.cs:934` `RawSseStreamAsync` | 说明其在 Unity/Mono 下不信任 HttpClient 流式行为 |
| **手写 HTTP/1.1** | `:970-1035` 手拼请求头、解析响应头 | 维护成本高；只支持 HTTP/1.1 |
| **自解 chunked** | `SseBodyReader.cs:8` 状态机 | 正确但复杂；协议错误处理需大量测试 |
| **无显式代理** | 裸 TCP 直连 | 企业代理/VPN 场景不可用 |
| **无 HTTP/2/重定向/连接池** | 裸 TCP | 功能缺失 |
| **TLS 自维护** | `SslStream` + 证书回调 | 证书策略/平台差异需自己处理 |
| **空闲超时自实现** | `:1053` 连接/握手超时；`:1092` 空闲超时 | 正确的方向，但应由公共组件提供 |

### 3.3 可借鉴

- **分层超时**：连接/握手 + 流空闲，两个看门狗；
- **取消必须关闭底层连接**（`tcp.Close()`）；
- **SSE 解析与传输分离**（`SseBodyReader` / `SseEventReader`）；
- **响应体/行长度上限**；
- **错误分类与重试**（`IsTransientConnError`、retry class）。

### 3.4 不能默认照搬

- 裸 TCP 作为**首选**：丢失代理/HTTP2/重定向/连接池，维护成本极高；
- 手写 HTTP 客户端：除非 UWR/HttpClient 都验证失败，否则不默认。

---

## 4. 三家对比

| | Yaolao | WorldTalk | 江湖有灵 |
|---|---|---|---|
| 流式传输 | UWR + Buffer | **UWR + DownloadHandlerScript** | **Raw TCP + SslStream + 手写 HTTP** |
| 真流式 | ❌ | ✅ | ✅ |
| SSE 解析 | 事后 Split | 增量行缓冲 | 自研 SseBodyReader/EventReader |
| chunked | UWR | UWR | **自解** |
| 取消 | `Abort()` | `Abort()` | `tcp.Close()` |
| 分层超时 | ❌ | ❌（整请求） | ✅ 连接/空闲 |
| 代理 | 系统 | 系统 | ❌ |
| TLS | UWR | UWR | 自维护 |
| HTTP/2 | ❌ | ❌ | ❌ |
| 主线程 | 回调 | 回调内解析 | 后台线程 |
| 代码复杂度 | 低 | 中 | 高 |
| 结论 | 选错 handler | 最实用的 Unity 路线 | 兜底/极端可控 |

---

## 5. 对我们设计的直接结论

1. **Unity 主路径**：`UWR + DownloadHandlerScript`，`ReceiveData` 只入队，解析在后台；
2. **超时**：首字节 / 空闲两个看门狗；不用 UWR timeout；
3. **取消**：`CancellationToken` → `Abort()`；残留回调丢弃；
4. **错误分类**：`result` + `responseCode` + `error` + 错误体；
5. **UI**：节流；主线程 dispatch；
6. **代理/自签/HTTP2**：需要时走 HttpClient 传输；UWR 只承诺系统代理；
7. **RawTcp**：只做兜底，不默认；
8. **测试**：mock transport + SSE fixture；不要把解析耦合进 UWR handler；
9. **许可证**：mikito 无 LICENSE，只能读思路；LaunchDarkly/ com.openai.unity 可参考但不要绑死依赖。
