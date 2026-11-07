# Phase 1 · Core 设计 spec

> **状态**：设计稿，待用户 review。
> **阶段**：Phase 1（Abstractions + SSE + 超时/重试/观测/选择器 + Mock）。
> **输入**：`docs/00-核心需求.md`、`03-pi通讯需求矩阵.md`、`04-Unity-AI-mod通讯问题清单.md`、`05-版本与兼容基线.md`、`09-参考项目评估矩阵.md`、`10-架构设计.md`、`11-开发阶段划分.md`、`docs/spike/phase0-summary.md`。
> **上游**：pi `v1.5.0` / `f9a263655`；pi.net `main` / `2d6757b`（仅 C# 映射参考）。
> **日期**：2026-11-07

---

## 1. 背景与目标

Phase 0 已证明：

- UWR 真流式成立，三版本 Mono/IL2CPP 通过；
- `System.Net.ServerSentEvents` 可 vendor 到 netstandard2.1；
- `link.xml` 是 IL2CPP 必需件；
- WebSocket 本地/远程/Close/Abort 可用；
- UWR 仅系统代理，显式代理走 HttpClient。

Phase 1 的目标：**建立纯 .NET 的稳定契约与核心件**，让后续 UWR/HttpClient/WebSocket 适配器、以及 PiUnityAgent 的 Host 适配器依赖这些契约。B 方案（2026-11-07）下，`PiUnityAgent.Ai` 只依赖自有 Fetch 端口，不直接依赖 `Abstractions`。

---

## 2. 范围

### 2.1 包含

| 组件 | 内容 |
|---|---|
| `UnityLlmTransport.Abstractions` | `IStreamingHttpTransport`、`ITransportSession`、`TransportRequest/Headers/Options`、`TransportCapabilities`、`TransportException`、超时/中间件/重试/观测/最终化/选择器装饰器、MockTransport |
| `UnityLlmTransport.Sse` | vendor `System.Net.ServerSentEvents` 的可编译版本 + 适配 |
| WebSocket 形状 | `IWebSocketTransport` / `IWebSocketSession` / `WebSocketMessage`（仅接口，行为 Phase 5） |
| 测试 | `net10.0` xUnit：Header、超时、取消、重试、观测、最终化、选择器、SSE、Mock |

### 2.2 不包含

- UWR/HttpClient 具体实现（Phase 2/3）；
- WebSocket 实现（Phase 5）；
- 真实认证（OAuth/登录/凭据存储）——Phase 4；
- provider JSON/SSE 语义（PiUnityAgent.Ai）；
- HTTP/SSE 解析器以外的网络栈；
- Unity 相关代码。

---

## 3. 包结构

```
src/
  UnityLlmTransport.Abstractions/   # netstandard2.1，零 Unity
  UnityLlmTransport.Sse/            # vendor parser + adapter
tests/
  UnityLlmTransport.Tests/          # net10.0 xUnit
```

> 包名定案 `UnityLlmTransport.*`（对齐 `docs/05` §包清单；`Sse` 为本阶段新增包，Phase 2 前需在该清单补登记）。
> **C# 9 语言天花板（硬规则）**：两个库工程显式 `<LangVersion>9.0</LangVersion>`（Unity 2021.3 上限）；禁 C# 10+ 语法——分号类体、file-scoped namespace、`required` 成员、集合表达式一律不用。编译器替我们把门，不靠自觉。

---

## 4. 公共类型面

### 4.1 传输接口

```csharp
public interface IStreamingHttpTransport
{
    TransportCapabilities Capabilities { get; }

    Task<ITransportSession> OpenAsync(
        TransportRequest request,
        TransportOptions options,
        CancellationToken cancellationToken);
}

public sealed record TransportRequest(
    string Method,
    string Url,
    TransportHeaders Headers,
    ReadOnlyMemory<byte> Body)
{
    /// <summary>P2：大请求用工厂重建流；必须是可重放的。</summary>
    public Func<CancellationToken, Task<Stream>>? BodyFactory { get; init; }
}

public sealed record TransportOptions
{
    public TimeSpan? ConnectTimeout { get; init; }
    public TimeSpan? FirstByteTimeout { get; init; }
    public TimeSpan? IdleTimeout { get; init; }
    public int? MaxResponseBytes { get; init; }
    public ProxyOptions? Proxy { get; init; }
    public TlsOptions? Tls { get; init; }

    public TransportKind Kind { get; init; } = TransportKind.Auto;
    public TimeSpan? WebSocketConnectTimeout { get; init; }
    // IdentityOptions 后置 Phase 4（认证），Phase 1 不含；见 §8 偏离表
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
    public TransportCacheRetention? CacheRetention { get; init; }
    public string? SessionId { get; init; }
    public RedirectPolicy Redirects { get; init; } = RedirectPolicy.Follow;
    public bool Decompress { get; init; } = true;
}

public interface ITransportSession : IAsyncDisposable
{
    int StatusCode { get; }
    TransportHeaders Headers { get; }
    Stream Body { get; }
    void Abort();
}
```

### 4.2 Headers / Proxy / TLS

```csharp
public sealed class TransportHeaders : Dictionary<string, string?>
{
    public TransportHeaders() : base(StringComparer.OrdinalIgnoreCase) { }
    public TransportHeaders(IDictionary<string, string?> values)
        : base(values, StringComparer.OrdinalIgnoreCase) { }
}

public enum ProxyMode { None, System, Explicit }

public sealed record ProxyOptions
{
    public ProxyMode Mode { get; init; } = ProxyMode.System;
    public string? Url { get; init; }
    public NetworkCredential? Credentials { get; init; }
}

public sealed record TlsOptions
{
    public bool ValidateCertificate { get; init; } = true;
    public X509Certificate2? ClientCertificate { get; init; }
    public IReadOnlyList<X509Certificate2>? CustomRoots { get; init; }
    public SslProtocols? Protocols { get; init; }
}
```

- 请求 headers 中 `null` = 抑制默认头；
- 名称大小写不敏感。

### 4.3 能力与错误

```csharp
[Flags]
public enum TransportCapabilities
{
    None = 0,
    StreamingBody = 1 << 0,
    WebSocket = 1 << 1,
    ExplicitProxy = 1 << 2,
    Http2 = 1 << 3,
    TlsCertificateHandler = 1 << 4,
    Compression = 1 << 5,
    RequestFinalizer = 1 << 6,
    SystemProxy = 1 << 7,
    ProxyAuthentication = 1 << 8,
    WebSocketOverTls = 1 << 9,
}

public enum TransportErrorKind
{
    Connect, Tls, Proxy, HttpStatus, Handshake, Protocol,
    FirstByteTimeout, IdleTimeout, Cancelled, Aborted, Overflow, Unsupported,
}

public sealed class TransportException : Exception
{
    public TransportErrorKind Kind { get; }
    public int? StatusCode { get; }
    public TransportHeaders? Headers { get; }
    public string? ErrorBody { get; }
    public bool IsRetryable { get; }
}
```

### 4.4 超时 / 中间件 / 重试 / 观测 / 最终化 / 选择器

```csharp
public sealed class TimeoutTransport : IStreamingHttpTransport { }
public sealed class MiddlewareTransport : IStreamingHttpTransport { }
public interface IRequestMiddleware
{
    ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct);
}

public sealed record RetryOptions
{
    public int MaxRetries { get; init; } = 2;
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
}
public interface IRetryPolicy
{
    bool ShouldRetry(TransportException error, int attempt);
    TimeSpan GetDelay(TransportException error, int attempt);
}
public interface IAuthRefreshHandler { Task<bool> RefreshAsync(CancellationToken ct); }
public sealed class RetryTransport : IStreamingHttpTransport { }

public sealed record TransportResponseInfo(int StatusCode, TransportHeaders Headers);
public interface ITransportObserver
{
    ValueTask OnResponseAsync(TransportResponseInfo response, CancellationToken ct);
    ValueTask OnBodyChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct);
}
public sealed class ObserverTransport : IStreamingHttpTransport { }

public interface ITransportRequestFinalizer
{
    ValueTask<TransportRequest> FinalizeAsync(TransportRequest request, CancellationToken ct);
}

/// <summary>管线最内层：紧贴具体 transport，按序执行 finalizer 列表。</summary>
public sealed class FinalizerTransport : IStreamingHttpTransport { }

public enum TransportKind { Auto, Sse, WebSocket, WebSocketCached }
public enum RedirectPolicy { Follow, Error }
public enum TransportCacheRetention { PerRequest, SessionScoped }
public sealed record TransportSelectionContext(string Api, string Provider, string BaseUrl, TransportKind Kind);
public sealed record TransportSelection(IStreamingHttpTransport? Http, IWebSocketTransport? WebSocket);
public interface ITransportSelector { TransportSelection Select(TransportSelectionContext context); }
public sealed record TransportFallbackPolicy(IReadOnlyList<TransportKind> Order, bool FallbackOnlyBeforeFirstMessage = true);
```

### 4.5 WebSocket 形状（仅接口）

```csharp
public interface IWebSocketTransport
{
    TransportCapabilities Capabilities { get; }
    Task<IWebSocketSession> ConnectAsync(WebSocketRequest request, WebSocketOptions options, CancellationToken ct);
}

public sealed record WebSocketRequest(string Url, TransportHeaders Headers);

public sealed record WebSocketOptions
{
    public TimeSpan? ConnectTimeout { get; init; }
    public TimeSpan? IdleTimeout { get; init; }
    public TimeSpan? KeepAliveInterval { get; init; }
    public int? MaxMessageBytes { get; init; }
    public ProxyOptions? Proxy { get; init; }
    public TlsOptions? Tls { get; init; }
    public string? SubProtocol { get; init; }
    public string? Origin { get; init; }
    public RedirectPolicy Redirects { get; init; } = RedirectPolicy.Error;
}

public enum WebSocketMessageType { Text, Binary, Close }
public sealed record WebSocketMessage(
    WebSocketMessageType Type,
    ReadOnlyMemory<byte> Payload,
    bool EndOfMessage,
    WebSocketCloseStatus? CloseStatus = null);

public interface IWebSocketSession : IAsyncDisposable
{
    WebSocketState State { get; }
    Task SendAsync(WebSocketMessage message, CancellationToken ct);
    IAsyncEnumerable<WebSocketMessage> ReceiveAsync(CancellationToken ct);
    Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct);
    void Abort();
}
```

> `WebSocketState` / `WebSocketCloseStatus` **复用 `System.Net.WebSockets`** 同名枚举（netstandard2.1 自带，三版本 Unity 均可），不自定义——避免白造一层映射。IL2CPP 裁剪由 link.xml 保障。

---

## 5. 行为

### 5.1 TimeoutTransport

- `ConnectTimeout` 透传给具体 transport；
- `FirstByteTimeout`：从 `OpenAsync` 返回 session 起，到 `ITransportSession.Body` 读出第 1 个字节的最大等待；
- `IdleTimeout`：两次读到 body 字节之间的最大间隔；
- `MaxResponseBytes`：累计响应字节超限 → `session.Abort()` → `TransportException(Overflow)`；
- 超时 → `session.Abort()` → `TransportException(FirstByteTimeout/IdleTimeout)`；
- 只包装 `Body`，不改状态码/headers。

### 5.2 MiddlewareTransport

- 按顺序执行 `IRequestMiddleware`；
- 每个 middleware 返回新的 `TransportRequest`；
- 用于 API Key/OAuth token/日志；
- 不存储/刷新凭据。

### 5.3 RetryTransport

- **策略二选一**：默认由 `RetryOptions` 驱动内置策略；调用方注入 `IRetryPolicy` 时整体替换内置逻辑，两者不叠加；
- 只在**首字节之前**重试；
- 可重试：网络、408/409/429/5xx、`x-should-retry: true`；
- 401 → `IAuthRefreshHandler.RefreshAsync` 后最多重试一次；
- 尊重 `Retry-After`/`retry-after-ms`，超过 `MaxRetryDelay` 失败；
- 指数退避 + 抖动；可取消。

### 5.4 ObserverTransport

- `OnResponseAsync` 在 Open 成功后、消费 Body 前；
- `OnBodyChunkAsync` 通过 **internal** `TeeStream` 观察原始字节（不进公共面）；
- 不改变 Body 字节内容。

### 5.5 Finalizer 时序

```
transport 构造最终请求（Host/Content-Length/默认头）
  → finalizer 签名
  → 发送
  → 发送后不得再改 header
```

- `ITransportRequestFinalizer` 在传输层发送前调用；
- 不支持最终化的传输不得声明 `RequestFinalizer`。

### 5.6 Selector

- `Select` 返回 `TransportSelection { Http?, WebSocket? }`；
- `Auto` 由上层 fallback policy 驱动；
- 能力不支持 → `Unsupported`，不静默降级；
- Selector 不直接发起请求。

### 5.7 MockTransport

- 可脚本化：字节块、延迟、错误、取消、超时、非 2xx；
- 用于 Core 单测；
- 不依赖 Unity/网络。

类型面：

```csharp
public sealed class MockTransport : IStreamingHttpTransport
{
    public MockTransport(Func<TransportRequest, MockResponse> handler);
    public IReadOnlyList<TransportRequest> Requests { get; }   // 供断言
}

public sealed record MockResponse
{
    public int StatusCode { get; init; } = 200;
    public TransportHeaders? Headers { get; init; }
    public IReadOnlyList<ReadOnlyMemory<byte>>? Chunks { get; init; }
    public TimeSpan ChunkDelay { get; init; }
    public Exception? Error { get; init; }                      // 非空则 OpenAsync 抛
}

public sealed record MockWebSocketTransport : IWebSocketTransport { }  // Phase 1 仅形状
```

### 5.8 解压与 Decompress 选项

- UWR 路径：Unity 自动解 gzip（Phase 0 实锤），`Decompress` 只声明意图，适配器不重复解压；
- HttpClient 路径：`Decompress=true` 时加 `Accept-Encoding` 并解压，`false` 时原样返回；
- SSE 解析器只消费已解压字节。

---

## 6. SSE 包

- vendor `spikes/SseCompile` 的 parser（MIT，含 `ArrayBuffer`/`SR`/`ArrayMaxLength`/`IsExternalInit`）；
- 公开 API 保持 `SseParser.Create(Stream)` / `EnumerateAsync`；
- 只做字节→SSE 事件，不做 provider JSON；
- 不引入 `System.Memory`/`Microsoft.Bcl.AsyncInterfaces` NuGet（netstandard2.1 自带）；
- 测试：多行/注释/字段、LF+`[DONE]`、CR-only、EOF 半截。

---

## 7. 测试计划

| 组 | 覆盖 |
|---|---|
| Headers | 大小写不敏感；请求 null 抑制默认头 |
| Timeout | 首字节超时、空闲超时、Abort 被调用 |
| Middleware | 顺序、请求替换、异常传播 |
| Retry | 首字节前重试、流开始后不重试、401 refresh、Retry-After、取消 |
| Observer | onResponse 先于 Body；原始 chunk 可见 |
| Finalizer | 发送前调用；header 不被传输层改写 |
| Selector | context 选择、能力不支持、fallback policy 形状 |
| SSE | 4 个边界测试（vendor） |
| Mock | 字节/延迟/错误/取消/超时 |
| 契约 | 同一组字节经 Mock/装饰器产出一致 |

---

## 8. 偏离表

| 项 | pi 原版 | pi.net | 我们 | 类别 | 原因/代价 |
|---|---|---|---|---|---|
| Api 类型 | 泛型 `Model<TApi>` | string | string | A | C# 泛型约束 |
| 选项 | 接口/泛型 | 接口 | record | B | C# 形状 |
| 取消 | AbortSignal | options/Token | CancellationToken | A | C# 惯用法 |
| 可选能力方法 | 可选成员 | 默认接口方法 | 独立能力接口 | A/Unity | Mono DIM 不稳 |
| partial/流 | 同步流 | 同步流 | 同步流 + Stream | B | 对齐 |
| HTTP 注入 | fetch | HttpClient | IStreamingHttpTransport | B | 可替换 |
| 身份选项 | 无统一字段 | 有 | Phase 1 不含 `IdentityOptions`，Phase 4 随认证补 | C | 认证属 Phase 4，先留孤儿字段反招歧义 |
| WebSocket 状态枚举 | 自定 | 自定 | 复用 `System.Net.WebSockets` | B | 不白造映射 |
| gzip 解压 | fetch 自动 | HttpClient 手动 | UWR 自动 / HttpClient 按 `Decompress` | B | 两栈能力差异 |
| 本阶段实现范围 | 全实现 | 全实现 | Core 形状 + Mock，不做真实传输 | C | Phase 2/3 后置 |

---

## 9. 验收标准

- [ ] `netstandard2.1` 编译 0 错误；
- [ ] 两个库工程 `LangVersion=9.0`，无 C# 10+ 语法（分号类体/file-scoped namespace/required/集合表达式）；
- [ ] `Abstractions`/`Sse` 零 `UnityEngine`；
- [ ] Headers 大小写/null 抑制；
- [ ] Timeout 首字节/空闲/Abort + `MaxResponseBytes` → `Overflow`；
- [ ] Retry 仅首字节前 + 401 refresh + `RetryOptions`/`IRetryPolicy` 二选一；
- [ ] Observer/Finalizer/Selector 形状与单测；
- [ ] WebSocket 枚举复用 `System.Net.WebSockets`；
- [ ] SseParser 4/4；
- [ ] MockTransport 类型面可脚本化并记录请求；
- [ ] 无 .NET 9+ API；
- [ ] 测试全绿。

---

## 10. 参考

- Phase 0：`docs/spike/phase0-summary.md`
- 架构：`docs/10-架构设计.md`
- 上游：pi `packages/ai/src/types.ts`、`utils/event-stream.ts`；pi.net `Stream`/`Utils`
- 参考实现：LaunchDarkly 超时/退避、System.Net.ServerSentEvents 解析（仅供模式）
