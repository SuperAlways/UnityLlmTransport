# Phase 2 · UWR 设计 spec

> **状态**：设计稿，待用户 review。
> **阶段**：Phase 2（UnityWebRequest 主传输 + Hosts.Unity 主线程/生命周期）。
> **输入**：`docs/00-核心需求.md` R1–R10b、`docs/10-架构设计.md` §5/§7/§8.7、`docs/11-开发阶段划分.md` §4、`docs/spike/phase0-summary.md`、`uwr-streaming.md`、`channel-stream.md`、`il2cpp.md`、`proxy-tls-matrix.md`、Phase 1 spec/plan。
> **日期**：2026-11-07

---

## 1. 背景与目标

Phase 0 已实锤（三版本 × Mono/IL2CPP）：

- `UnityWebRequest + DownloadHandlerScript` 真流式：6 次回调、首块 17–50ms、总时长 ~0.6s；
- Abort 生效、残留回调停止；`/sse-error` 500 错误体可读；
- 系统代理生效（错误端口返回 502，不是 `ConnectionError/0`）；UWR 无显式代理 API；
- 自签证书需 `CertificateHandler`，默认校验报 `SSL CA certificate error`；
- **link.xml 必需**：无 link.xml 的 IL2CPP 构建运行卡死（`Curl error 42: Callback aborted`）；
- `Channel<byte[]>` → Stream → SseParser 桥接可编译可消费，但**取消/Abort/无死锁判据未测**（Phase 2 必补）。

Phase 2 目标：在 Unity 游戏/Player 进程内，把 Phase 1 的纯 .NET 契约接上 UWR 主路径，做到：

1. 真流式（首字节即交付，不整包）；
2. 取消即中止底层连接；
3. 首字节/空闲超时由 Phase 1 看门狗负责；
4. 主线程只入队、不解析；
5. 有界缓冲，溢出即 abort；
6. 三版本（2021.3.45f1 / 6000.3.28f1 / 6000.6.8f1）Mono 实机通过，IL2CPP 冒烟通过。

---

## 2. 范围

### 2.1 包含

| 交付 | 内容 |
|---|---|
| `UnityLlmTransport.UnityWebRequest.Core` | 纯 .NET（netstandard2.1、零 Unity）：有界队列、队列→Stream、UWR 结果分类、请求计划（header/redirect/TLS/压缩决策） |
| `unity/UnityLlmTransport.UnityWebRequest` | Unity 适配层：`StreamingDownloadHandler`、`UnityWebRequestTransport`、`UnityWebRequestSession`、`link.xml` |
| `unity/UnityLlmTransport.Hosts.Unity` | `IUnityWebRequestRunner`、`UnityCoroutineHost`（协程 + 主线程 dispatcher + 生命周期 token） |
| 测试 | `tests/UnityLlmTransport.UnityWebRequest.Tests`（net10，纯逻辑）+ Unity Phase2 harness（三版本 × Mono / IL2CPP 冒烟） |

### 2.2 不包含

- HttpClient（Phase 3）、认证/签名（Phase 4）、WebSocket/RawTcp/WebGL（Phase 5）；
- 流式上传（`BodyFactory`）——pi 的请求体均为可重放字节，Phase 2 只支持 `ReadOnlyMemory<byte>`；
- 显式代理/代理认证、客户端证书、自定义根证书、协议版本控制（UWR 无对应 API，显式 `Unsupported`）；
- HTTP/2 能力验证（Phase 3 真实端点再判）；
- UPM/NuGet 发布形态（Phase 6 定；Phase 2 harness 用源码/程序集嵌入形态）。

---

## 3. 包与文件结构

```
src/UnityLlmTransport.UnityWebRequest.Core/          # netstandard2.1，零 UnityEngine，可单测
  UnityLlmTransport.UnityWebRequest.Core.csproj
  ChunkQueue.cs                  # 有界字节块队列（零第三方依赖）
  ChunkQueueReadStream.cs        # 队列→Stream；async-only
  UwrCompletion.cs               # 归一化结果枚举
  UwrResponseSnapshot.cs         # 分类输入（Completion/StatusCode/Headers/Error）
  UwrResponseClassifier.cs       # 归一化结果 → TransportException?
  UwrRequestPlan.cs              # 纯决策产物
  UwrRequestPlanner.cs           # TransportRequest+Options → UwrRequestPlan / Unsupported

tests/UnityLlmTransport.UnityWebRequest.Tests/        # net10.0 xUnit
  ChunkQueueTests.cs
  ChunkQueueReadStreamTests.cs
  UwrResponseClassifierTests.cs
  UwrRequestPlannerTests.cs

unity/UnityLlmTransport.UnityWebRequest/
  Runtime/StreamingDownloadHandler.cs
  Runtime/UnityWebRequestTransport.cs
  Runtime/UnityWebRequestSession.cs
  link.xml

unity/UnityLlmTransport.Hosts.Unity/
  Runtime/IUnityWebRequestRunner.cs
  Runtime/UnityCoroutineHost.cs        # MonoBehaviour：Post/Invoke + 协程 + 生命周期
  Runtime/UnityMainThreadDispatcher.cs # internal queue，由 UnityCoroutineHost.Update 驱动
  Runtime/UnityLifecycleToken.cs

unity/Phase2Harness/
  README.md
  Assets/Scripts/Phase2Runner.cs
  Assets/Editor/Phase2Build.cs
  Assets/link.xml                      # harness 形态：Assembly-CSharp

scripts/assemble-phase2-unity.sh       # 把 src/unity 资产装配进外部 Unity 工程
```

---

## 4. 公共类型面

### 4.1 纯包（Core）

```csharp
public sealed class ChunkQueue
{
    public ChunkQueue(int maxBufferedBytes);

    /// <summary>拷贝入队；false = 溢出（队列随即以 Overflow 错误终止）。</summary>
    public bool TryWrite(byte[] data, int length);

    /// <summary>单消费者读取；返回 false 表示队列已终止（正常或异常，见 CompletionError）。</summary>
    public bool TryRead(out ReadOnlyMemory<byte> chunk);

    /// <summary>等待可读；false = 已终止（无数据）。</summary>
    public ValueTask<bool> WaitToReadAsync(CancellationToken ct);

    public Exception? CompletionError { get; }
    public int BufferedBytes { get; }

    public void Complete(Exception? error = null);
    public void Abort(Exception error);   // Complete(error) + 丢弃未读数据
}

public sealed class ChunkQueueReadStream : Stream
{
    public ChunkQueueReadStream(ChunkQueue queue);

    /// <summary>仅支持 ReadAsync；同步 Read 抛 NotSupportedException。</summary>
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default);
}

public enum UwrCompletion
{
    InProgress,
    Success,
    ConnectionError,
    ProtocolError,
    DataProcessingError,
    Aborted
}

public sealed record UwrResponseSnapshot(
    UwrCompletion Completion,
    int StatusCode,
    TransportHeaders Headers,
    string? Error);

public static class UwrResponseClassifier
{
    /// <summary>返回 null = 可交付会话（含任意非 2xx 状态）；非 null = 抛出该 TransportException。</summary>
    public static TransportException? Classify(UwrResponseSnapshot snapshot, bool callerCancelled);
}

public sealed record UwrRequestPlan(
    TransportHeaders Headers,          // 已过滤 null、已补 Accept-Encoding: identity
    int RedirectLimit,
    bool AcceptAllCertificates);

public static class UwrRequestPlanner
{
    /// <summary>能力不支持 → TransportException(Unsupported)；不静默降级。</summary>
    public static UwrRequestPlan Create(TransportRequest request, TransportOptions options);
}

/// <summary>宿主 runner 判定“首字节已到”的最小契约；由 Unity DownloadHandler 实现。</summary>
public interface IStreamingDownloadHandler
{
    bool FirstDataReceived { get; }
}
```

> 该接口用于解耦 `Hosts.Unity`（判断首字节）与 `UnityWebRequest` 包（具体 handler），避免 Host 包直接依赖适配器实现。

### 4.2 Unity 适配层（不参与 dotnet 单测）

```csharp
public sealed class UnityWebRequestTransport : IStreamingHttpTransport
{
    public UnityWebRequestTransport(
        IUnityWebRequestRunner runner,
        UnityWebRequestTransportOptions? options = null);

    public TransportCapabilities Capabilities { get; }
    public Task<ITransportSession> OpenAsync(
        TransportRequest request,
        TransportOptions options,
        CancellationToken cancellationToken);
}

public sealed record UnityWebRequestTransportOptions
{
    public int MaxBufferedBytes { get; init; } = 4 * 1024 * 1024;
    public int DownloadBufferSize { get; init; } = 16 * 1024;
}

public sealed class StreamingDownloadHandler : DownloadHandlerScript
{
    public StreamingDownloadHandler(ChunkQueue queue, int downloadBufferSize);
    public bool FirstDataReceived { get; }
    public void MarkAborted();
    // ReceiveData 只拷贝入队；CompleteContent 完成队列；溢出/Abort 返回 false
}

public sealed class UnityWebRequestSession : ITransportSession
{
    public int StatusCode { get; }
    public TransportHeaders Headers { get; }
    public Stream Body { get; }
    public void Abort();
    public ValueTask DisposeAsync();
}
```

`TransportCapabilities`（UWR 声明）：

```
StreamingBody | SystemProxy | TlsCertificateHandler
+ Compression（仅当 gzip 解码门禁通过）
```

**不声明**：`ExplicitProxy`、`ProxyAuthentication`、`Http2`、`RequestFinalizer`、`WebSocket`。

> `RequestFinalizer` 语义是“finalizer 能控制最终发送的 headers”。UWR 在 `SendWebRequest` 时由引擎补齐 `Content-Length`/`Host` 等，finalizer 之后再补会破坏签名。因此 UWR **不声明该能力**，SigV4/ADC 必须走 HttpClient（与 `docs/10` §8.7 一致）。Phase 1 终审“真实传输需 OR 该能力”的备注按此修正。

### 4.3 Hosts.Unity

```csharp
public interface IUnityWebRequestRunner
{
    /// <summary>任意线程调用；把 func 编组到 Unity 主线程并等待结果。</summary>
    Task<T> InvokeAsync<T>(Func<T> func, CancellationToken ct);

    /// <summary>任意线程调用；在主线程启动 UWR，完成条件见 §5.3（首字节/提前失败）。</summary>
    Task<UwrResponseSnapshot> StartAsync(UnityWebRequest request, CancellationToken ct);

    /// <summary>任意线程调用；请求真正结束时完成快照（会话返回后继续观察用）。</summary>
    Task<UwrResponseSnapshot> WaitForCompletionAsync(UnityWebRequest request);

    /// <summary>fire-and-forget 主线程投递（Abort/Dispose 用）。</summary>
    void Post(Action action);
}

public sealed class UnityCoroutineHost : MonoBehaviour, IUnityWebRequestRunner
{
    public CancellationToken ShutdownToken { get; }
    public void InvalidateLifecycle();   // 读档/切世界/重载：取消旧 token，换新
}
```

- 无全局静态单例；宿主实例由 mod/游戏创建并注入；
- `Awake` 中 `DontDestroyOnLoad`；`OnDestroy`/`OnApplicationQuit` 取消 `ShutdownToken`；
- `Post/InvokeAsync` 由 `Update` 排空；协程用 `StartCoroutine` 驱动 `SendWebRequest`。

---

## 5. 行为

### 5.1 请求构建（主线程）

`UwrRequestPlanner` 先做纯决策（可在任意线程）：

- `ProxyOptions.Mode`：`System` 通过；`Explicit`/`None` → `Unsupported`（UWR 无法关闭/指定代理，不假装支持）；
- `TlsOptions.ClientCertificate`/`CustomRoots`/`Protocols` → `Unsupported`；`ValidateCertificate=false` → 计划 `AcceptAllCertificates=true`；
- `Redirects`：`Follow` → `redirectLimit=32`；`Error` → `redirectLimit=0`；
- headers：`null` 值仅表示“不设置该头”，引擎默认头无法删除（记录在偏离表）；
- `Decompress=true` 且调用方未给 `Accept-Encoding` → 补 `Accept-Encoding: identity`；
- `BodyFactory != null` → `Unsupported`；`Body.Length > 0` → `UploadHandlerRaw`，否则不挂上传器。

然后在主线程（`InvokeAsync`）：

```
UnityWebRequest(method, url)
  uploadHandler/downloadHandler = ...
  request.timeout = ConnectTimeout > 0 ? ceil(seconds) : 0   // 见 5.2
  request.redirectLimit = plan.RedirectLimit
  request.certificateHandler = plan.AcceptAllCertificates ? AcceptAll : null
  SetRequestHeader(...)  // plan.Headers
```

### 5.2 超时映射

- `ConnectTimeout` → `UnityWebRequest.timeout`（UWR 的整请求上限，单位秒；0=无限）；这是 UWR 能提供的最接近语义；
- `FirstByteTimeout` → **适配器在等待首字节（`StartAsync`）时强制**：从 `StartAsync` 启动计时，超时 → `Abort` + `TransportException(FirstByteTimeout)`（因为 `OpenAsync` 要等首字节才能交付会话，看门狗必须在适配器内）；
- `IdleTimeout` → Phase 1 `TimeoutTransport`（Body 读间隔；适配器不重复实现）；
- 偏差：UWR 没有“仅连接阶段”超时，`ConnectTimeout` 实际约束到响应结束；记录偏离表。

### 5.3 就绪与首字节

`StartAsync` 的完成条件：

1. `StreamingDownloadHandler` 首次 `ReceiveData`（首字节已入队）**且** `request.responseCode != 0`；或
2. 请求在首字节前结束/失败；或
3. 调用方取消、`ConnectTimeout`（UWR `timeout`）触发，或 `FirstByteTimeout` 到期（适配器 Abort）。

`UwrResponseSnapshot` 在就绪时快照 `result`、`responseCode`、`GetResponseHeaders()`、`error`（主线程内），随后 `Classify` 决定：

- 网络/协议失败且 `StatusCode == 0` → `TransportException`；
- 任意 `StatusCode > 0`（含 4xx/5xx/502）→ 返回会话，由上层 `RetryTransport` 按状态分类；
- 错误体：会话 `Body` 仍可读（UWR 在 500 时 `ReceiveData` 已收到错误体）。

> **实现门禁**：三版本实机必须证明“首字节回调时 `responseCode != 0`、`GetResponseHeaders()` 含 `content-type`”。若不成立，Phase 2 暂停并修订设计（不得把整包缓冲伪装成流式）。
>
> **门禁结果（2026-11-07 实机）**：通过——三版本 × Mono/IL2CPP 的 U2 均 `status=200 content_type=True`（证据：`docs/spike/phase2-results.md`）。

### 5.4 流式与背压

- `ReceiveData` 在主线程：仅 `ChunkQueue.TryWrite` 拷贝字节，不做 UTF-8/SSE/JSON；
- 有界容量 `MaxBufferedBytes`（默认 4 MiB）：`TryWrite` 失败 → 队列以 `Overflow` 终止，`ReceiveData` 返回 `false`（引擎 abort），会话读取抛 `TransportException(Overflow)`；
- `CompleteContent` → `queue.Complete()`；失败/取消 → `queue.Abort(error)`；
- 会话返回后由 `WatchCompletionAsync` 观察请求终止（队列已终止则无动作）：
  - `Success` → `queue.Complete()`；
  - 已交付的非 2xx（`ProtocolError` + `StatusCode>0`）：响应完整、错误 body 仍可读 → **按干净收尾 `Complete()`**，不得用伪 `Protocol` 中止把已缓冲的错误体清掉（U6 实机判据）；
  - 其余失败/截断（如 `ConnectionError` 半截流）→ `Abort(classifier 错误)`，读者收到对应 `TransportException` 而非悬挂。
- Abort 后残留回调由 `MarkAborted` 状态位丢弃（不产生二次错误）。

### 5.5 取消

- `OpenAsync` 收到的 `CancellationToken` 注册到会话生命周期：取消 → `runner.Post(request.Abort)`（主线程）；
- 等待首字节期间取消 → 立即 `Abort`，`StartAsync` 以取消完成，分类为 `Cancelled`；
- 会话返回后取消 → 队列以 `Cancelled/Aborted` 终止，`Body.ReadAsync` 抛 `TransportException`，删除注册；
- 会话返回后由 `WatchCompletionAsync` 观察请求终止，语义见 §5.4（`Success` 与已交付非 2xx 干净收尾 `Complete()`，截断/失败按 classifier `Abort()`），防止读者悬挂。
- `DisposeAsync` = Abort + 主线程 `request.Dispose()` + 队列终止；不得泄漏注册。

### 5.6 错误分类（纯函数）

`UwrResponseClassifier` 规则（按顺序）：

| 输入 | 输出 |
|---|---|
| `callerCancelled` 或 `Aborted` 或 error 含 `aborted` | `Cancelled`（`TransportErrorKind.Cancelled`） |
| `ConnectionError`/`ProtocolError` 且 `StatusCode==0`，error 含 `SSL`/`certificate` | `Tls`（IsRetryable=false） |
| 同上，error 含 `proxy`/`Tunnel`/`407` | `Proxy`（IsRetryable=false） |
| 同上，其他 | `Connect`（IsRetryable=true） |
| `DataProcessingError` | `Protocol`（IsRetryable=false） |
| `StatusCode > 0`（任意 Completion） | `null`（返回会话） |
| `Success` 且 `StatusCode==0` | `Protocol`（异常） |

> Phase 0 已知：本地错误端口在系统代理下返回 **502**（`ProtocolError` + code=502），按上表返回会话并由 `RetryTransport` 视作 5xx 可重试，**不要**把它当 `ConnectionError/0`。

### 5.7 压缩

**实测事实（2026-11-07 三版本 × Mono/IL2CPP 实机，证据 `docs/spike/phase2-results.md`）**：

- UWR（2021.3 / 6.3 / 6.6 三版本一致）对 gzip 响应**自动解压**，但响应头**仍保留 `Content-Encoding: gzip`**——不能只看头决定是否需要解压；
- Mono 的 `GZipStream` 只支持同步读（异步 API 内部同步忙等）；IL2CPP 侧同样只保证同步读可用。

**设计（实现落地形态）**：

- 请求固定 `Accept-Encoding: identity`（不鼓励服务端压缩）；
- 响应头含 `Content-Encoding: gzip` 且 `Decompress=true` 时，会话 `Body` 组装为 `AsyncOffloadStream(GzipSniffReadStream(SyncQueueReadStream(queue)))`：
  - `GzipSniffReadStream` 按流头部 `1f 8b` magic 嗅探：UWR 已解压的字节直通（头仍标 gzip 的“假 gzip”）；真 gzip 交给 `GZipStream` 解码；
  - Mono `GZipStream` 只能同步读 → 内层换成 `SyncQueueReadStream`（阻塞式队列视图），再由 `AsyncOffloadStream` 把同步解码 offload 到线程池，Body 对外保持异步只读契约，调用线程永不阻塞；
- gzip 门禁（U8）三版本实机通过 → **`Compression` 能力保留**；若日后某版本确认“UWR 未解压且 `GZipStream` 无法消费”，再置 false 并记录限制；
- `Decompress=false`：不补 identity、不包装解压。

### 5.8 Headers

- 响应 headers 用 `GetResponseHeaders()` 转 `TransportHeaders`（大小写不敏感）；
- 请求 `null` 值不调用 `SetRequestHeader`；引擎默认头若存在无法移除（如 `User-Agent`），记录偏差；
- `content-length` 由 UWR 自动生成，调用方设置无效。

---

## 6. Unity 版本适配

- 版本差异**只在适配层**：Core 与 Hosts.Unity 不出现 `UNITY_*` 宏或 `Application.unityVersion` 判断（Phase 2 初始实现三版本行为一致，无需分支）；
- 能力由运行时声明，不按版本分支：`Http2=false`（2021.3 无、6.3/6.6 未验）、`Compression` 按 §5.7 门禁、其余见 §4.2；
- 若三版本实机出现差异 → 只更新能力矩阵与适配器实现，不改 Core/Abstractions。

| 项 | 2021.3.45f1 | 6.3.28f1 | 6.6.8f1 | 归一化 |
|---|---|---|---|---|
| UWR 流式 | DownloadHandlerScript | 同左 | 同左 | 同一 adapter |
| 首字节时 responseCode/headers | 门禁验证 | 门禁验证 | 门禁验证 | 同一行为 |
| gzip | 门禁验证 | 门禁验证 | 门禁验证 | `Compression` 能力 |
| link.xml | 必需（Phase 0 实锤） | 必需（同一资产） | 必需 | 同一 link.xml |
| IL2CPP | 冒烟 | 冒烟 | 冒烟 | 记录版本结果 |

### link.xml 形态

Phase 2 的 Unity 侧源码按 **Assembly-CSharp** 编译（无 asmdef），`link.xml` 目标 `Assembly-CSharp` + `StreamingDownloadHandler`/`UnityCoroutineHost` `preserve="all"`：

```xml
<linker>
  <assembly fullname="Assembly-CSharp">
    <type fullname="StreamingDownloadHandler" preserve="all" />
    <type fullname="UnityCoroutineHost" preserve="all" />
  </assembly>
</linker>
```

- 消费方若把源码放进自定义 asmdef，必须把 `fullname` 改为对应程序集名（README/文档写死）；
- Phase 6 打包时再评估 UPM/程序集形态；本阶段以 harness 形态验收。

---

## 7. 测试计划

### 7.1 纯逻辑（net10 xUnit，CI 无网）

| 组 | 覆盖 |
|---|---|
| `ChunkQueue` | 跨块拷贝、容量计数、溢出终止、Complete/Abort、丢弃语义、多线程写/单线程读 |
| `ChunkQueueReadStream` | 逐块拼接、部分缓冲、终止返回 0、异常终止透传、取消不挂死、慢消费者无死锁、无伪 EOF |
| Planner | 各类 Unsupported（explicit/none proxy、client cert、custom roots、protocols、BodyFactory）；redirect 0/32；identity 补头；null header 过滤 |
| Classifier | 取消/Tls/Proxy/Connect/Protocol/DataProcessing、502→会话、500→会话、Success→会话 |

### 7.2 Unity Phase2 harness（三版本 × Mono，自动运行）

`Phase2Runner` 复用 Phase 0 模式：启动后跑矩阵、写 `phase2-results.txt`、退出。

| ID | 场景 | 判据 |
|---|---|---|
| U1 | 真实 `UnityWebRequestTransport` + `SseParser`，`/sse` | ≥2 次回调；首块时间 < 总时长；逐条产出 |
| U2 | 首字节快照 | `ReceiveData` 首回时 `responseCode != 0` 且 headers 含 `content-type` |
| U3 | 取消（流中途） | `ct.Cancel()` 后 ≤1 帧 `Abort`；队列以 `Cancelled` 终止；1s 内无新增回调 |
| U4 | 首字节/空闲超时 | `FirstByteTimeout` 由适配器在等待首字节时触发；`IdleTimeout` 经 `TimeoutTransport` 触发；均 Abort |
| U5 | 溢出 | `MaxBufferedBytes` 调小 + `/sse-big` 快速大包 → `Overflow`，无卡死 |
| U6 | 500 | `/sse-error` 返回会话，Body 可读错误体 |
| U7 | 连接/代理/TLS | 错误端口（系统代理）→ 502 会话；自签默认 → `Tls`；`ValidateCertificate=false` → 成功；`Explicit` → `Unsupported` |
| U8 | gzip | `/sse-gzip` 经适配器解析出条目（identity 或解压生效） |
| U9 | 线程纪律 | `ReceiveData` 在主线程；解析/队列读取不在主线程（记录线程 id） |
| U10 | 重定向 | `RedirectPolicy.Error` 对重定向端点报错；`Follow` 成功 |
| U11 | Abort 残留 | Abort 后 1s 窗口观察块计数不增长（证明**读侧干净**：观察块仅随读取经 `TeeStream` 触发，abort 后读通道干净终止、窗口内无迟到观察块；引擎层 ReceiveData 残留回调抑制由 `StreamingDownloadHandler._aborted` 设计 + Phase 0 实机证据支撑，直证推迟到 Phase 3） |

> `SseTestServer` 需新增 `/sse-slow`（600ms 首包，供 U4 首字节超时）、`/sse-idle`（首包立即、后续 400ms，供 U4 空闲超时）、`/sse-big`（快速大块，供 U5）与 `/redirect`（302 → `/sse`，供 U10）；其余复用 Phase 0 服务。

### 7.3 IL2CPP 冒烟

- 三版本各构建一次 IL2CPP Player，跑 U1/U3/U5 三格；
- `link.xml` 缺失对照不重复（Phase 0 已钉死必要性）；若 U1 卡死即构建形态/程序集名错配，按 §6 修正。

### 7.4 实机边界

- Phase 2 的“实机”= 用三个 Unity 版本构建的 Windows Player（与 Phase 0 同法）；
- 塞入太吾游戏本体（官方 mod 加载）放到 Phase 4（PiUnityAgent 接入）联调，不在本阶段承诺。

---

## 8. 偏离表（pi / pi.net / 我们）

| 项 | pi 原版 | pi.net | 我们 | 类别 | 原因/代价 |
|---|---|---|---|---|---|
| HTTP 栈 | Node fetch | HttpClient | UWR 主路径 | A | Unity 运行时约束；HttpClient Phase 3 |
| 请求体 | 任意流/字符串 | 流/字符串 | 仅 `ReadOnlyMemory<byte>` | C | UWR 流式上传复杂；`BodyFactory` 报 Unsupported |
| 取消 | `AbortSignal` | CancellationToken | CancellationToken + 主线程 `Abort` 编组 | A | UWR 仅主线程；延迟 ≤1 帧 |
| 超时 | 单一 `timeoutMs` | handler 超时 | Connect→`UWR.timeout`；首字节/空闲→core 看门狗 | B | UWR 无法只限制连接阶段；Connect 实际是整请求上限 |
| 请求 finalizer | fetch 可改最终请求 | 可改 header | **不声明** `RequestFinalizer` | B | UWR 发送时补 Content-Length/Host，签名会失效；SigV4 走 HttpClient |
| 代理 | 环境/自定义 | handler | 仅系统代理；Explicit/None → Unsupported | B | UWR 无 API；能力诚实声明 |
| 重定向 | 可手动处理 | handler | Follow/Error（`redirectLimit`） | C | UWR 无手动重定向回调 |
| TLS | SDK 自定义 | handler | 默认校验 / 全接受；自定义根/客户端证书不支持 | C | UWR 仅 `CertificateHandler` |
| headers null 抑制 | 支持 | 支持 | 不设置即抑制“我们加的”，引擎默认头删不掉 | C | UWR 无删除引擎默认头 API |
| gzip | fetch 自动 | handler 解压 | 请求 identity；若仍 gzip 则 `GZipStream` 解压 | B | UWR 不保证自动解压（Phase 0 未验证内容） |
| 同步 Body 读 | 同步流 | 同步流 | 仅 `ReadAsync`；同步 Read 抛 NotSupported | B | 主线程同步读会卡帧/死锁（S0-6 已知限制） |
| 状态可见时机 | 即时 | 即时 | 首字节时才可读 status/headers | A | UWR 无更早的响应头回调 |
| Channel 桥 | — | — | 手写 `ChunkQueue`（不用 `System.Threading.Channels`） | B/Unity | 避免第三方 DLL 版本冲突；代价=自维护并发代码（已单测） |

---

## 9. 验收标准

- [ ] `UnityLlmTransport.UnityWebRequest.Core` netstandard2.1 编译 0 错误，零 `UnityEngine`；
- [ ] 纯逻辑测试全绿（Queue/Stream/Planner/Classifier）；
- [ ] 三个 Unity 版本 Mono：U1–U11 全过（结果写入 `docs/spike/phase2-results.md` + 原始 evidence）；
- [ ] 三版本 IL2CPP：U1/U3/U5 冒烟通过，`link.xml` 生效；
- [ ] 主线程零解析；Abort 后读侧干净终止（U11 判据边界见 §7.2——引擎层 ReceiveData 直证推迟到 Phase 3）；溢出无卡死；
- [ ] gzip 门禁结论明确（支持则 `Compression=true`，否则文档记限制）；
- [ ] `link.xml` 随 Unity 资产提交；缺 link.xml 不可发布写进 README/SOP；
- [ ] 不修改 `PiUnityAgent.Ai`/`Abstractions` 的公共形状；仅新增适配器与纯 Core 包；
- [ ] 文档更新：`docs/05` 补登记 `UnityLlmTransport.UnityWebRequest.Core` 与 Unity 资产形态说明。

---

## 10. 后置与已知风险

| 项 | 处理 |
|---|---|
| 流式上传（BodyFactory） | Phase 3+ 评估 HttpClient；UWR 保持 Unsupported |
| 显式/关闭代理、代理认证 | Phase 3 HttpClient |
| HTTP/2 能力 | Phase 3 真实端点验证后设置；本阶段 `Http2=false` |
| 自定义根证书/客户端证书 | Phase 3 HttpClient handler |
| UPM/asmdef 发布形态 | Phase 6 打包；当前 harness 用 Assembly-CSharp + link.xml |
| 游戏内（太吾）联调 | Phase 4 PiUnityAgent 接入 |

**必须由实机门禁回答的风险**：

1. 首字节回调时 `responseCode`/`GetResponseHeaders()` 是否已可用（§5.3）；
2. UWR 对 `Accept-Encoding: identity` 的服从程度与 gzip 实际行为（§5.7）；
3. 系统代理 502 与真实连接错误的区分是否符合 §5.6（Phase 0 只有单点证据）；
4. 判 CancellationToken 注册 → 主线程 Abort 的时序（≤1 帧）；
5. link.xml 在 Assembly-CSharp 形态下的必要性和有效性（Phase 0 已证，但本适配器新增类需复核）。

---

## 11. 参考

- Phase 0：`docs/spike/phase0-summary.md`、`uwr-streaming.md`、`channel-stream.md`、`il2cpp.md`、`proxy-tls-matrix.md`
- 架构：`docs/10-架构设计.md` §5/§7/§8.7
- 阶段：`docs/11-开发阶段划分.md` §4
- Phase 1 契约：`docs/superpowers/specs/2026-11-07-phase1-core-design.md`
- SOP：`docs/SOP/Unity多版本通讯测试-SOP-2026-11-06.md`
