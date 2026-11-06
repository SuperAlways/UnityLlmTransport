# Phase 0 Spike Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用最小实验确认 Unity 2021/2.1 的通讯事实（SSE 解析、UWR 流式/Abort/gzip、代理/TLS、WebSocket、IL2CPP），产出 go/no-go。

**Architecture:** 所有实验放在 `spikes/`，一次性、不合并生产；结论写入 `docs/spike/`。agent 可自动执行纯 .NET 任务；Unity 任务由 agent 准备资产，人类在 Unity 2021.3 中执行并回填结果。

**Tech Stack:** `netstandard2.1` / `net10.0`、Unity 2021.3、vendored `System.Net.ServerSentEvents`、UnityWebRequest、HttpClient、ClientWebSocket。

**Spec protocol:** 按 `docs/12-开发执行模型-spec-plan-subagent.md` §2.1：agent 基于需求/参考/架构直接写 spec，不做过度 brainstorm；用户只 review 最终 spec 与不可逆分叉。

## Global Constraints

- 不改 `src/`、不写生产代码；`spikes/` 与 `docs/spike/` 可丢弃。
- 不复制无 LICENSE 代码；只参考模式。
- 每个实验记录环境、输入、结果、错误、证据路径。
- 所有结论以实测为准；未验证项必须写“未验证”。
- 不使用 .NET 6+ 专有 API 到 `netstandard2.1` 生产代码；spike 测试工程可用 `net10.0`。
- 每个任务结束提交；提交信息按任务给定。

---

### Task 0: Spike 工作区

**Files:**
- Create: `.superpowers/sdd/progress.md`
- Create: `spikes/`、`docs/spike/`

**Interfaces:**
- Consumes: 当前仓库 `UnityLlmTransport`。
- Produces: 分支 `phase0-spike`、spike 目录、SDD 台账。

- [ ] **Step 0.1: 建分支与目录**

```bash
git checkout -b phase0-spike
mkdir -p spikes docs/spike .superpowers/sdd
printf '# Phase 0 SDD progress\n\n- Task 0: in progress\n' > .superpowers/sdd/progress.md
```

- [ ] **Step 0.2: 提交**

```bash
git add .superpowers/sdd/progress.md
git commit -m "chore: start phase0 spike"
```

---

### Task 1: SseParser 在 netstandard2.1 编译

**Files:**
- Create: `spikes/SseCompile/SseCompile.csproj`
- Create: `spikes/SseCompile/IsExternalInit.cs`
- Create: `spikes/SseCompile/System/Net/ServerSentEvents/*.cs`（从 refs 拷贝）
- Create: `spikes/SseCompile/System/Net/ArrayBuffer.cs`（从 refs Common 拷贝）
- Create: `spikes/SseCompile.Tests/SseCompile.Tests.csproj`
- Create: `spikes/SseCompile.Tests/SseParserSmokeTests.cs`

**Interfaces:**
- Consumes: `refs/01-.../src/libraries/System.Net.ServerSentEvents`（MIT）。
- Produces: 可编译的 `SseParser` + 关键边界测试结果。

- [ ] **Step 1.1: 补齐 sparse checkout 并拷贝源码**

```bash
git -C refs/01-dotnet-runtime-system-net-serversentevents sparse-checkout add src/libraries/Common/src/System/Net
mkdir -p spikes/SseCompile/System/Net/ServerSentEvents
cp refs/01-dotnet-runtime-system-net-serversentevents/src/libraries/System.Net.ServerSentEvents/src/System/Net/ServerSentEvents/*.cs spikes/SseCompile/System/Net/ServerSentEvents/
cp refs/01-dotnet-runtime-system-net-serversentevents/src/libraries/Common/src/System/Net/ArrayBuffer.cs spikes/SseCompile/System/Net/ArrayBuffer.cs
```

- [ ] **Step 1.2: 建 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <Nullable>enable</Nullable>
    <LangVersion>9.0</LangVersion>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
  </PropertyGroup>
</Project>
```

- [ ] **Step 1.3: 补 IsExternalInit**

```csharp
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
```

- [ ] **Step 1.4: 降级 C# 11/12 语法**

把源码中的 `"..."u8` 改为 `Encoding.UTF8.GetBytes("...")`；集合表达式改为 `new[] { ... }`。用 `dotnet build` 报错逐条修。

- [ ] **Step 1.5: 建测试工程**

```bash
dotnet new xunit -o spikes/SseCompile.Tests -f net10.0
dotnet add spikes/SseCompile.Tests reference spikes/SseCompile
```

- [ ] **Step 1.6: 写边界测试**

```csharp
using System.Net.ServerSentEvents;
using System.Text;
using Xunit;

public class SseParserSmokeTests
{
    private static async Task<List<SseItem<string>>> ParseAsync(string raw)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var items = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            items.Add(item);
        return items;
    }

    [Fact]
    public async Task MultiLine_Comment_Fields()
    {
        var items = await ParseAsync(": hi\r\nid: 1\r\nevent: delta\r\ndata: hello\r\ndata: world\r\n\r\n");
        Assert.Single(items);
        Assert.Equal("hello\nworld", items[0].Data);
        Assert.Equal("delta", items[0].EventType);
        Assert.Equal("1", items[0].EventId);
    }

    [Fact]
    public async Task Lf_And_Done()
    {
        var items = await ParseAsync("data: a\n\ndata: [DONE]\n\n");
        Assert.Equal(2, items.Count);
        Assert.Equal("[DONE]", items[1].Data);
    }

    [Fact]
    public async Task Cr_Only()
    {
        var items = await ParseAsync("data: a\r\rdata: b\r\r");
        Assert.Equal(2, items.Count);
        Assert.Equal("a", items[0].Data);
        Assert.Equal("b", items[1].Data);
    }

    [Fact]
    public async Task Eof_Half_Event_Is_Dropped()
    {
        var items = await ParseAsync("data: a\n");
        Assert.Empty(items);
    }
}
```

- [ ] **Step 1.7: 跑测试**

```bash
dotnet test spikes/SseCompile.Tests/SseCompile.Tests.csproj -c Release --nologo
```

Expected: 4/4 passed；若编译失败，按错误修语法/依赖。

- [ ] **Step 1.8: 提交**

```bash
git add spikes/SseCompile spikes/SseCompile.Tests
git commit -m "spike: vendor System.Net.ServerSentEvents on netstandard2.1"
```

---

### Task 2: 本地 SSE 测试服务器

**Files:**
- Create: `spikes/SseTestServer/SseTestServer.csproj`
- Create: `spikes/SseTestServer/Program.cs`

**Interfaces:**
- Consumes: 无。
- Produces: `http://127.0.0.1:5199/sse` 普通 SSE；`/sse-gzip`、`/sse-error` 可选端点。

- [ ] **Step 2.1: 建 csproj**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
```

- [ ] **Step 2.2: 写服务器**

```csharp
using System.Net;
using System.Text;

var listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:5199/");
listener.Start();
Console.WriteLine("SSE server: http://127.0.0.1:5199/sse");

while (true)
{
    var ctx = await listener.GetContextAsync();
    var path = ctx.Request.Url!.AbsolutePath;
    if (path is not ("/sse" or "/sse-error"))
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
        continue;
    }

    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.SendChunked = true;
    var output = ctx.Response.OutputStream;

    if (path == "/sse-error")
    {
        ctx.Response.StatusCode = 500;
        await output.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":\"boom\"}"));
        ctx.Response.Close();
        continue;
    }

    foreach (var part in new[] { "你", "好", "，", "世", "界" })
    {
        var payload = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{part}\"}}}}]}}\n\n";
        await output.WriteAsync(Encoding.UTF8.GetBytes(payload));
        await output.FlushAsync();
        await Task.Delay(100);
    }

    await output.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
    ctx.Response.Close();
}
```

- [ ] **Step 2.3: 启动并验证**

```bash
dotnet run --project spikes/SseTestServer
curl -N http://127.0.0.1:5199/sse
curl -i http://127.0.0.1:5199/sse-error
```

Expected: 普通端点逐段输出；错误端点 500 + error body。

- [ ] **Step 2.4: 提交**

```bash
git add spikes/SseTestServer
git commit -m "spike: local SSE test server"
```

---

### Task 3: Unity UWR 流式 Spike

**Files:**
- Create: `spikes/UnityUwrSpike/Assets/Scripts/StreamingDownloadHandler.cs`
- Create: `spikes/UnityUwrSpike/Assets/Scripts/UwrSpike.cs`
- Create: `spikes/UnityUwrSpike/README.md`

**Interfaces:**
- Consumes: Task 2 的本地 SSE 服务。
- Produces: 逐块/首字节/Abort/错误分类的实测数据。

- [ ] **Step 3.1: 写 handler**

```csharp
using System.Collections.Generic;
using UnityEngine.Networking;

public sealed class StreamingDownloadHandler : DownloadHandlerScript
{
    public readonly List<(double t, int bytes)> Chunks = new();
    public int CallbackCount;
    private readonly double _start = UnityEngine.Time.realtimeSinceStartupAsDouble;

    public StreamingDownloadHandler() : base(new byte[16384]) { }

    protected override bool ReceiveData(byte[] data, int dataLength)
    {
        CallbackCount++;
        Chunks.Add((UnityEngine.Time.realtimeSinceStartupAsDouble - _start, dataLength));
        return true;
    }
}
```

- [ ] **Step 3.2: 写 runner**

```csharp
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

public sealed class UwrSpike : MonoBehaviour
{
    [SerializeField] private string url = "http://127.0.0.1:5199/sse";
    private UnityWebRequest _request;

    private IEnumerator Start()
    {
        var handler = new StreamingDownloadHandler();
        _request = new UnityWebRequest(url, "POST");
        _request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
        _request.downloadHandler = handler;
        _request.SetRequestHeader("Content-Type", "application/json");
        _request.SetRequestHeader("Accept", "text/event-stream");
        _request.timeout = 0;

        yield return _request.SendWebRequest();

        Debug.Log($"result={_request.result} code={_request.responseCode} " +
                  $"error={_request.error} callbacks={handler.CallbackCount} " +
                  $"first={(handler.Chunks.Count > 0 ? handler.Chunks[0].t : -1):F3}s");
        foreach (var c in handler.Chunks) Debug.Log($"chunk t={c.t:F3}s bytes={c.bytes}");
    }

    public void AbortNow() => _request?.Abort();
}
```

- [ ] **Step 3.3: Unity 手动执行**

1. Unity Hub 新建 2021.3 3D 项目到 `spikes/UnityUwrSpike`；
2. 复制 `Assets/Scripts/*.cs`；
3. 新建场景，创建空 GameObject，挂 `UwrSpike`；
4. 启动 Task 2 服务，Play；
5. 记录 Console：回调次数、首块时间、总时长；
6. 运行中调用 `AbortNow()`，继续观察 2 秒；
7. 把 URL 改成错误端口，记录 `result`/`code`/`error`；
8. 把 URL 改成 `/sse-error`，记录错误体是否到达 handler。

Expected:
- `callbacks > 1` 且首块时间明显小于总时长；
- Abort 后 2 秒内无新增有效 chunk（或记录残留数）；
- 错误端口：`result=ConnectionError`、`code=0`、`error` 非空；
- `/sse-error`：`code=500`，错误体可从 handler/下载器读到。

- [ ] **Step 3.4: 记录并提交**

把实测数据写入 `docs/spike/uwr-streaming.md`，然后：

```bash
git add spikes/UnityUwrSpike docs/spike/uwr-streaming.md
git commit -m "spike: UWR streaming facts"
```

---

### Task 4: 代理 / TLS 矩阵

**Files:**
- Create: `spikes/ProxyTlsSpike/ProxyTlsSpike.csproj`
- Create: `spikes/ProxyTlsSpike/Program.cs`
- Create: `docs/spike/proxy-tls-matrix.md`

**Interfaces:**
- Consumes: Task 2 的本地 HTTP SSE；一个本地 HTTPS/自签端点；系统/显式代理。
- Produces: UWR / HttpClient / ClientWebSocket 的代理/TLS 支持矩阵。

- [ ] **Step 4.1: 写 HttpClient 探针**

```csharp
using System.Net;
using System.Net.Http;

var cases = new (string Name, HttpClientHandler Handler)[]
{
    ("no-proxy", new HttpClientHandler { UseProxy = false }),
    ("system-proxy", new HttpClientHandler { UseProxy = true }),
    ("explicit-proxy", new HttpClientHandler
    {
        UseProxy = true,
        Proxy = new WebProxy("http://127.0.0.1:7897"),
    }),
};

foreach (var (name, handler) in cases)
{
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    try
    {
        var resp = await client.GetAsync("https://example.com/");
        Console.WriteLine($"{name}: {(int)resp.StatusCode}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"{name}: {e.GetType().Name}: {e.Message}");
    }
}
```

- [ ] **Step 4.2: 跑自动部分**

```bash
dotnet run --project spikes/ProxyTlsSpike
```

记录每格结果；没有代理/证书环境时写“未配置”。

- [ ] **Step 4.3: Unity 手动部分**

- UWR：系统代理开/关，本地/远程，正常/自签证书；显式代理无 API，记录“不可用”；
- ClientWebSocket：本地 ws、远程 wss、系统代理；
- 每格记录：成功/失败、`result`/`responseCode`/异常、耗时。

- [ ] **Step 4.4: 写矩阵并提交**

`docs/spike/proxy-tls-matrix.md` 使用固定表头：栈 | 网络 | 代理 | TLS | 结果 | 错误 | 证据。

```bash
git add spikes/ProxyTlsSpike docs/spike/proxy-tls-matrix.md
git commit -m "spike: proxy and TLS matrix"
```

---

### Task 5: WebSocket 本地/远程

**Files:**
- Create: `spikes/WsTestServer/WsTestServer.csproj`
- Create: `spikes/WsTestServer/Program.cs`
- Create: `spikes/UnityUwrSpike/Assets/Scripts/WsSpike.cs`
- Create: `docs/spike/websocket-matrix.md`

**Interfaces:**
- Consumes: Unity 2021.3；本地 ws 服务器。
- Produces: 本地 ws / 远程 wss / 代理 / IL2CPP 支持等级。

- [ ] **Step 5.1: 写本地 ws echo 服务**

```csharp
using System.Net;
using System.Net.WebSockets;
using System.Text;

var listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:5200/ws/");
listener.Start();
Console.WriteLine("WS echo: ws://127.0.0.1:5200/ws/");

while (true)
{
    var ctx = await listener.GetContextAsync();
    if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
    var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
    var buffer = new byte[4096];
    var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
    var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
    var reply = Encoding.UTF8.GetBytes("echo:" + text);
    await ws.SendAsync(reply, WebSocketMessageType.Text, true, CancellationToken.None);
    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
}
```

- [ ] **Step 5.2: 写 Unity 客户端**

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public sealed class WsSpike : MonoBehaviour
{
    public async Task Run(string url)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new System.Uri(url), CancellationToken.None);
        var bytes = Encoding.UTF8.GetBytes("hello");
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        Debug.Log($"recv type={result.MessageType} bytes={result.Count} " +
                  $"text={Encoding.UTF8.GetString(buffer, 0, result.Count)}");
    }
}
```

- [ ] **Step 5.3: 手动矩阵**

- 本地 ws：`ws://127.0.0.1:5200/ws/`；
- 远程 wss：公共 echo 或自建自签 wss；
- 系统代理；
- Close/Abort；
- IL2CPP。

Expected：本地 ws 成功；远程 wss 成功/失败原因明确（TLS/代理/握手/超时）；本地与远程分开记录。

- [ ] **Step 5.4: 写矩阵并提交**

```bash
git add spikes/WsTestServer spikes/UnityUwrSpike docs/spike/websocket-matrix.md
git commit -m "spike: websocket local and remote facts"
```

---

### Task 6: ChannelReadStream 桥接

**Files:**
- Create: `spikes/ChannelBridge/ChannelBridge.csproj`
- Create: `spikes/ChannelBridge/ChannelReadStream.cs`
- Create: `spikes/ChannelBridge.Tests/ChannelBridge.Tests.csproj`
- Create: `spikes/ChannelBridge.Tests/ChannelReadStreamTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `SseParser`。
- Produces: 可工作的 `ChannelReadStream : Stream`；证明 UWR 回调可桥到 SseParser。

- [ ] **Step 6.1: 写 ChannelReadStream**

```csharp
using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

public sealed class ChannelReadStream : Stream
{
    private readonly ChannelReader<byte[]> _reader;
    private ReadOnlyMemory<byte> _current;

    public ChannelReadStream(ChannelReader<byte[]> reader) => _reader = reader;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_current.IsEmpty)
        {
            if (!await _reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0;
            if (!_reader.TryRead(out var chunk)) return 0;
            _current = chunk;
        }
        var n = Math.Min(buffer.Length, _current.Length);
        _current.Slice(0, n).CopyTo(buffer);
        _current = _current.Slice(n);
        return n;
    }

    protected override void Dispose(bool disposing) { base.Dispose(disposing); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
```

- [ ] **Step 6.2: 写测试**

```csharp
using System.Net.ServerSentEvents;
using System.Text;
using System.Threading.Channels;
using Xunit;

public class ChannelReadStreamTests
{
    [Fact]
    public async Task Parser_Consumes_Chunks_Across_Boundaries()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        var raw = Encoding.UTF8.GetBytes("data: 你\r\n\r\ndata: 好\r\n\r\n");
        foreach (var b in raw) channel.Writer.TryWrite(new[] { b });
        channel.Writer.TryComplete();

        using var stream = new ChannelReadStream(channel.Reader);
        var items = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            items.Add(item);

        Assert.Equal(2, items.Count);
        Assert.Equal("你", items[0].Data);
        Assert.Equal("好", items[1].Data);
    }
}
```

- [ ] **Step 6.3: 跑测试**

```bash
dotnet test spikes/ChannelBridge.Tests/ChannelBridge.Tests.csproj -c Release --nologo
```

Expected: 1/1 passed。

- [ ] **Step 6.4: 提交**

```bash
git add spikes/ChannelBridge spikes/ChannelBridge.Tests
git commit -m "spike: channel read stream bridge"
```

---

### Task 7: IL2CPP / link.xml

**Files:**
- Create: `spikes/UnityUwrSpike/Assets/link.xml`
- Create: `docs/spike/il2cpp.md`

**Interfaces:**
- Consumes: Task 3 的 Unity 项目。
- Produces: IL2CPP 下 handler 重写未被剥离的证据。

- [ ] **Step 7.1: 写 link.xml**

```xml
<linker>
  <assembly fullname="Assembly-CSharp">
    <type fullname="StreamingDownloadHandler" preserve="all" />
  </assembly>
</linker>
```

- [ ] **Step 7.2: 构建**

Unity 2021.3：File → Build Settings → Windows / IL2CPP → Build；运行 Build 产物。

- [ ] **Step 7.3: 运行同样实验**

Expected：`ReceiveData` 被调用；Abort 生效；无缺失回调。若失败，记录错误并降级为“仅 Mono”。

- [ ] **Step 7.4: 记录并提交**

```bash
git add spikes/UnityUwrSpike/Assets/link.xml docs/spike/il2cpp.md
git commit -m "spike: IL2CPP handler preservation"
```

---

### Task 8: 汇总与 go/no-go

**Files:**
- Create: `docs/spike/phase0-summary.md`

**Interfaces:**
- Consumes: Task 1–7 的记录。
- Produces: 对 Phase 1/2 的明确修订与 go/no-go。

- [ ] **Step 8.1: 写汇总**

```markdown
# Phase 0 Summary

## 结论
- UWR 主路径：是/否
- HttpClient：M1 提供/后置
- WebSocket：本地 ws / 远程 wss / IL2CPP / WebGL 支持等级
- gzip：自动解压/需解压
- IL2CPP：通过/仅 Mono

## 证据
| 实验 | 结论 | 证据 |
|---|---|---|
| S0-1 | ... | docs/spike/sse-compile.md |
| ... | ... | ... |

## 对 Phase 1/2 的修订
- ...
```

- [ ] **Step 8.2: 提交**

```bash
git add docs/spike/phase0-summary.md
git commit -m "spike: phase0 summary and go-no-go"
```

---

## Self-Review

### Spec coverage

| Spec 要求 | 任务 |
|---|---|
| S0-1 SseParser 编译 | Task 1 |
| S0-2 UWR 流式/gzip/Abort | Task 3 |
| S0-3 代理/TLS 矩阵 | Task 4 |
| S0-4 WebSocket 本地/远程 | Task 5 |
| S0-5 IL2CPP | Task 7 |
| S0-6 ChannelReadStream | Task 6 |
| 汇总/go-no-go | Task 8 |

### Placeholder scan

- 无 TBD/TODO/“similar to Task N”。
- Unity 手动步骤均给出确切操作与 expected。
- 代码步骤均含完整代码。

### Type consistency

- `StreamingDownloadHandler` / `UwrSpike` 在 Task 3/7 一致。
- `ChannelReadStream` 的 `ReadAsync(Memory<byte>)` 与 vendor `SseParser` 的 `Stream` 消费一致。
- `SseParser.Create` / `EnumerateAsync` 名称来自 vendor 源码。
