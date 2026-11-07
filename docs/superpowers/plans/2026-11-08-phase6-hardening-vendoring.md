# Phase 6 · 稳定性加固与源码搬运 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在不做 NuGet/UPM/源码 zip 的前提下，完成稳定性 soak、WS 安全默认上限、源码搬运/可观测指引、许可与版本、四版本矩阵复跑，让 pi 能按 commit 搬运源码。

**Architecture:** 稳定性测试直接扩展现有三个测试工程（HttpClient/WebSocket/Auth），复用 Phase3TestSupport/WsTestSupport/MockTransport；唯一的运行时行为变更在 WS 适配器默认 16MiB 消息上限。源码搬运只有文档与许可证，不生成包。

**Tech Stack:** C# 9 / netstandard2.1、net10 xUnit、Unity 四版本 Mono/IL2CPP harness。

## Global Constraints

- 不发布 NuGet/UPM/源码 zip；不建公共 tag；`VERSION` 仅内部分发里程碑 `0.6.0`。
- 不修改 Abstractions/Sse/HttpClient/Auth/WebSocket/UWR 公共类型形状；唯一允许的行为语义变化 = WS 默认 16MiB 消息上限（适配器内部）。
- 不包含 WS→SSE 回退、自动重连、WebSocketCached、RawTcp、WebGL、消费方集成。
- 源码搬运只复制 `src/` + `unity/` Runtime + link.xml，排除 tests/spikes/harness/scripts/obj/bin/Library；同一程序集只保留一份 `IsExternalInit`。
- 四版本：2022.3.62f3c1、2021.3.45f1、6000.3.28f1、6000.6.8f1。
- 显式 `git add`，禁 `git add -A`；不提交 bin/obj/Library。
- 测试端口：5199（SSE）、5201/5233/5234（Phase3）、5271/5273（WS dotnet）、5200（WS Unity）。

## 文件结构

```
src/UnityLlmTransport.WebSocket/ClientWebSocketSession.cs      # default budget
src/UnityLlmTransport.WebSocket/ClientWebSocketTransport.cs   # DefaultMaxMessageBytes
tests/UnityLlmTransport.WebSocket.Tests/WsTestSupport.cs      # /ws/huge
tests/UnityLlmTransport.WebSocket.Tests/StabilityTests.cs
tests/UnityLlmTransport.HttpClient.Tests/StabilityTests.cs
tests/UnityLlmTransport.Auth.Tests/StabilityTests.cs
tests/UnityLlmTransport.HttpClient.Tests/HttpClientRequestBuilderTests.cs  # warning fix
tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj # CA2022 NoWarn
docs/13-源码搬运与集成指引.md
docs/14-可观测与脱敏指引.md
LICENSE / NOTICE / VERSION
docs/spike/phase6-release-results.md
docs/spike/evidence/phase6/            # 本轮矩阵原始行
```

---

### Task 1: WebSocket 默认消息上限（16MiB）

**Files:**
- Modify: `src/UnityLlmTransport.WebSocket/ClientWebSocketTransport.cs`（新增 `DefaultMaxMessageBytes`）
- Modify: `src/UnityLlmTransport.WebSocket/ClientWebSocketSession.cs`（null 时使用默认）
- Modify: `tests/UnityLlmTransport.WebSocket.Tests/WsTestSupport.cs`（`/ws/huge`）
- Test: `tests/UnityLlmTransport.WebSocket.Tests/StabilityTests.cs`（默认上限测试放此文件，后续 Task 2 扩展）

**Interfaces:**
- Consumes: `WebSocketBufferBudget`。
- Produces: `ClientWebSocketTransport.DefaultMaxMessageBytes = 16 * 1024 * 1024`；`MaxMessageBytes==null` 时 session 使用该值。

- [ ] **Step 1: 写失败测试与 `/ws/huge`**

在 `WsTestSupport.cs` 的 switch 中追加：

```csharp
                    case "/ws/huge":
                        var hugeChunk = new byte[64 * 1024];
                        for (int i = 0; i < 257; i++)
                        {
                            await SendAsync(socket, hugeChunk, WebSocketMessageType.Binary, false);
                        }
                        await SendAsync(socket, new byte[1], WebSocketMessageType.Binary, true);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
```

创建 `tests/UnityLlmTransport.WebSocket.Tests/StabilityTests.cs`：

```csharp
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("ws-server")]
    public class StabilityTests
    {
        private readonly WsServerFixture _fixture;

        public StabilityTests(WsServerFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<IWebSocketSession> Connect(
            string path,
            WebSocketOptions? options = null,
            CancellationToken lifetime = default)
        {
            var transport = new ClientWebSocketTransport();
            return await transport.ConnectAsync(
                new WebSocketRequest(_fixture.Server.HttpBase + path, new TransportHeaders()),
                options ?? new WebSocketOptions(),
                lifetime);
        }

        [Fact]
        public async Task Default_MaxMessageBytes_Overflows_Huge_Message()
        {
            var session = await Connect("/ws/huge");
            var error = await Assert.ThrowsAsync<TransportException>(async () =>
            {
                await foreach (var _ in session.ReceiveAsync(CancellationToken.None))
                {
                }
            });
            await session.DisposeAsync();

            Assert.Equal(TransportErrorKind.Overflow, error.Kind);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo --filter FullyQualifiedName~Default_MaxMessageBytes`
Expected: FAIL（无默认上限，收到 16MiB+ 消息后仍继续/最终等待 Close；测试超时或未 Overflow）。

- [ ] **Step 3: 实现默认上限**

`ClientWebSocketTransport.cs` 增加：

```csharp
        public const int DefaultMaxMessageBytes = 16 * 1024 * 1024;
```

`ClientWebSocketSession.cs` 构造函数改：

```csharp
            _budget = new WebSocketBufferBudget(options.MaxMessageBytes ?? ClientWebSocketTransport.DefaultMaxMessageBytes);
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 39`。

- [ ] **Step 5: 提交**

```bash
git add src/UnityLlmTransport.WebSocket/ClientWebSocketTransport.cs src/UnityLlmTransport.WebSocket/ClientWebSocketSession.cs tests/UnityLlmTransport.WebSocket.Tests/WsTestSupport.cs tests/UnityLlmTransport.WebSocket.Tests/StabilityTests.cs
git commit -m "feat(ws): 默认 16MiB 消息上限"
```

---

### Task 2: 三套稳定性 soak

**Files:**
- Modify: `tests/UnityLlmTransport.WebSocket.Tests/StabilityTests.cs`（追加 5 tests）
- Create: `tests/UnityLlmTransport.HttpClient.Tests/StabilityTests.cs`（4 tests）
- Create: `tests/UnityLlmTransport.Auth.Tests/StabilityTests.cs`（2 tests）

**Interfaces:**
- Consumes: 现有 `ServerFixture`（Phase3 端口）/`WsServerFixture`/`MockTransport`/Auth 中间件。
- Produces: 重复连接、长流、取消、错误体、并发与上限的稳定性证据；若发现真实缺陷最小修复。

- [ ] **Step 1: WS 稳定性测试追加**

在 `tests/UnityLlmTransport.WebSocket.Tests/StabilityTests.cs` 追加：

```csharp
        [Fact]
        public async Task Sequential_Connections_No_Leak()
        {
            for (int i = 0; i < 30; i++)
            {
                var session = await Connect("/ws/echo");
                await session.SendAsync(
                    new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hello"), true),
                    CancellationToken.None);
                await foreach (var message in session.ReceiveAsync(CancellationToken.None))
                {
                    Assert.Equal("echo:hello", Encoding.UTF8.GetString(message.Payload.ToArray()));
                    break;
                }
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task Message_Size_Boundary_Exact_And_Overflow()
        {
            var exact = await Connect("/ws/echo", new WebSocketOptions { MaxMessageBytes = 8 });
            await exact.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("abc"), true),
                CancellationToken.None);
            await foreach (var message in exact.ReceiveAsync(CancellationToken.None))
            {
                Assert.Equal("echo:abc", Encoding.UTF8.GetString(message.Payload.ToArray()));
                break;
            }
            await exact.DisposeAsync();

            var overflow = await Connect("/ws/echo", new WebSocketOptions { MaxMessageBytes = 7 });
            await overflow.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("abc"), true),
                CancellationToken.None);
            var error = await Assert.ThrowsAsync<TransportException>(async () =>
            {
                await foreach (var _ in overflow.ReceiveAsync(CancellationToken.None))
                {
                }
            });
            await overflow.DisposeAsync();
            Assert.Equal(TransportErrorKind.Overflow, error.Kind);
        }

        [Fact]
        public async Task Abort_Repeated_No_Leak()
        {
            for (int i = 0; i < 20; i++)
            {
                var session = await Connect("/ws/echo");
                session.Abort();
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task Concurrent_Sends_Under_Load()
        {
            var session = await Connect("/ws/echo");
            var sends = new Task[10];
            for (int i = 0; i < sends.Length; i++)
            {
                sends[i] = session.SendAsync(
                    new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("a"), true),
                    CancellationToken.None);
            }
            await Task.WhenAll(sends);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Close_Handshake_Repeated()
        {
            for (int i = 0; i < 20; i++)
            {
                var session = await Connect("/ws/close");
                await foreach (var message in session.ReceiveAsync(CancellationToken.None))
                {
                    Assert.Equal(WebSocketMessageType.Close, message.Type);
                    break;
                }
                await session.DisposeAsync();
            }
        }
```

- [ ] **Step 2: HttpClient 稳定性测试**

`tests/UnityLlmTransport.HttpClient.Tests/StabilityTests.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using UnityLlmTransport.Sse;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("phase3-server")]
    public class StabilityTests
    {
        private readonly ServerFixture _fixture;

        public StabilityTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        private static TransportRequest Request(string url)
        {
            return new TransportRequest(
                "POST",
                url,
                new TransportHeaders(),
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{}")));
        }

        private static async Task<List<string>> ParseAsync(Stream stream, CancellationToken ct = default)
        {
            var items = new List<string>();
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct))
            {
                items.Add(item.Data);
            }
            return items;
        }

        [Fact]
        public async Task Sequential_SSE_Requests_No_Leak()
        {
            var transport = new HttpClientTransport();
            for (int i = 0; i < 20; i++)
            {
                var session = await transport.OpenAsync(
                    Request(_fixture.Server.HttpBase + "/sse"),
                    new TransportOptions(),
                    CancellationToken.None);
                var items = await ParseAsync(session.Body);
                await session.DisposeAsync();
                Assert.Equal(6, items.Count);
            }
        }

        [Fact]
        public async Task Cancel_And_Dispose_During_Stream()
        {
            var transport = new HttpClientTransport();
            for (int i = 0; i < 10; i++)
            {
                using var cts = new CancellationTokenSource();
                var session = await transport.OpenAsync(
                    Request(_fixture.Server.HttpBase + "/sse"),
                    new TransportOptions(),
                    cts.Token);
                var parse = Task.Run(() => ParseAsync(session.Body));
                await Task.Delay(50);
                cts.Cancel();
                try { await parse; } catch (Exception) { }
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task Error_Body_Repeated()
        {
            var transport = new HttpClientTransport();
            for (int i = 0; i < 20; i++)
            {
                var session = await transport.OpenAsync(
                    Request(_fixture.Server.HttpBase + "/sse-error"),
                    new TransportOptions(),
                    CancellationToken.None);
                var buffer = new byte[256];
                var text = new StringBuilder();
                int read;
                while ((read = await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
                {
                    text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
                await session.DisposeAsync();
                Assert.Equal(500, session.StatusCode);
                Assert.Contains("boom", text.ToString(), StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Injected_Client_Reuse()
        {
            using var client = new System.Net.Http.HttpClient();
            var transport = new HttpClientTransport(new HttpClientTransportOptions { Client = client });
            for (int i = 0; i < 10; i++)
            {
                var session = await transport.OpenAsync(
                    Request(_fixture.Server.HttpBase + "/echo"),
                    new TransportOptions(),
                    CancellationToken.None);
                await session.DisposeAsync();
                Assert.Equal(200, session.StatusCode);
            }
        }
    }
}
```

- [ ] **Step 3: Auth 稳定性测试**

`tests/UnityLlmTransport.Auth.Tests/StabilityTests.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class StabilityTests
    {
        private static TransportRequest Request()
        {
            return new TransportRequest(
                "GET",
                "http://localhost/test",
                new TransportHeaders(),
                ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public async Task Retry_Refresh_Repeated()
        {
            for (int iteration = 0; iteration < 20; iteration++)
            {
                string token = "old";
                int attempts = 0;
                int refreshes = 0;

                var oauth = new OAuthTokenMiddleware(ct => new ValueTask<string>(token));
                var mock = new MockTransport(_ =>
                {
                    attempts++;
                    return attempts == 1
                        ? new MockResponse { StatusCode = 401 }
                        : new MockResponse { StatusCode = 200 };
                });
                var middleware = new MiddlewareTransport(mock, new IRequestMiddleware[] { oauth });
                var retry = new RetryTransport(
                    middleware,
                    new RetryOptions { MaxRetries = 1 },
                    new ZeroDelayPolicy(),
                    new ActionRefreshHandler(() =>
                    {
                        refreshes++;
                        token = "new";
                    }));

                var session = await retry.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);
                await session.DisposeAsync();

                Assert.Equal(2, attempts);
                Assert.Equal(1, refreshes);
                Assert.Equal("Bearer new", mock.Requests[1].Headers["Authorization"]);
            }
        }

        [Fact]
        public async Task Middleware_Many_Requests()
        {
            int calls = 0;
            var middleware = new OAuthTokenMiddleware(ct =>
            {
                calls++;
                return new ValueTask<string>("token");
            });

            for (int i = 0; i < 50; i++)
            {
                var result = await middleware.InvokeAsync(Request(), CancellationToken.None);
                Assert.Equal("Bearer token", result.Headers["Authorization"]);
            }
            Assert.Equal(50, calls);
        }

        private sealed class ActionRefreshHandler : IAuthRefreshHandler
        {
            private readonly Action _action;

            public ActionRefreshHandler(Action action)
            {
                _action = action;
            }

            public Task<bool> RefreshAsync(CancellationToken ct)
            {
                _action();
                return Task.FromResult(true);
            }
        }

        private sealed class ZeroDelayPolicy : IRetryPolicy
        {
            public bool ShouldRetry(TransportException error, int attempt)
            {
                return error.IsRetryable;
            }

            public TimeSpan GetDelay(TransportException error, int attempt)
            {
                return TimeSpan.Zero;
            }
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo
dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo
dotnet test tests/UnityLlmTransport.Auth.Tests/UnityLlmTransport.Auth.Tests.csproj --nologo
```

Expected: WS 44、HttpClient 56、Auth 26；任一失败先判断真缺陷，最小修复并记录（不放宽 soak 判据）。

- [ ] **Step 5: 提交**

```bash
git add tests/UnityLlmTransport.WebSocket.Tests/StabilityTests.cs tests/UnityLlmTransport.HttpClient.Tests/StabilityTests.cs tests/UnityLlmTransport.Auth.Tests/StabilityTests.cs
git commit -m "test(phase6): 三传输稳定性 soak"
```

### Task 3: 源码搬运/可观测指引 + 许可与版本

**Files:**
- Create: `docs/13-源码搬运与集成指引.md`
- Create: `docs/14-可观测与脱敏指引.md`
- Create: `LICENSE`
- Create: `NOTICE`
- Create: `VERSION`

**Interfaces:**
- Consumes: Phase 1–5 的源码布局与 harness 装配脚本。
- Produces: pi 可直接执行的搬运规则、许可声明、内部版本号。

- [ ] **Step 1: 写 `LICENSE` / `NOTICE` / `VERSION`**

`LICENSE`：

```text
MIT License

Copyright (c) 2026 UnityLlmTransport contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

`NOTICE`：

```text
UnityLlmTransport

This product includes software derived from System.Net.ServerSentEvents
(dotnet/runtime), licensed under the MIT License.
Copyright (c) .NET Foundation and Contributors.
Source: https://github.com/dotnet/runtime
```

`VERSION`：

```text
0.6.0
```

- [ ] **Step 2: 写 `docs/13-源码搬运与集成指引.md`**

```markdown
# 13 · 源码搬运与集成指引

> 当前分发形态 = **源码搬运**：pi/消费方把本仓库指定路径拷入自己的 Unity 工程，钉 commit。
> 不做 NuGet/UPM/源码 zip；等消费方验收后再评估打包。

## 1. 复制范围

复制（保持相对目录）：

```
src/UnityLlmTransport.Abstractions/**/*.cs
src/UnityLlmTransport.Sse/**/*.cs
src/UnityLlmTransport.HttpClient/**/*.cs
src/UnityLlmTransport.Auth/**/*.cs
src/UnityLlmTransport.WebSocket/**/*.cs
src/UnityLlmTransport.UnityWebRequest.Core/**/*.cs
unity/UnityLlmTransport.UnityWebRequest/Runtime/**/*.cs
unity/UnityLlmTransport.Hosts.Unity/Runtime/**/*.cs
unity/UnityLlmTransport.UnityWebRequest/link.xml
```

排除：`tests/`、`spikes/`、`unity/Phase*Harness/`、`scripts/`、`obj/`、`bin/`、`Library/`、`.superpowers/`。

## 2. 唯一 IsExternalInit 规则

每个纯包都有 `Internal/IsExternalInit.cs`。若所有源码进**同一程序集**（Assembly-CSharp 或无 asmdef 的 mod）：

- **只保留 `src/UnityLlmTransport.Abstractions/Internal/IsExternalInit.cs`**；
- 删除 Sse/HttpClient/Auth/WebSocket/UWR.Core 下同名文件；
- 否则 CS0101 duplicate type。

若每个包分别使用 asmdef（各自独立程序集），保留各自 shim 即可。

## 3. link.xml

`unity/UnityLlmTransport.UnityWebRequest/link.xml` 当前：

```xml
<linker>
  <assembly fullname="Assembly-CSharp">
    <type fullname="UnityLlmTransport.StreamingDownloadHandler" preserve="all" />
    <type fullname="UnityLlmTransport.UnityCoroutineHost" preserve="all" />
  </assembly>
</linker>
```

- 源码进 `Assembly-CSharp` → 原样使用；
- 使用自定义 asmdef → 把 `fullname` 改成承载 `StreamingDownloadHandler`/`UnityCoroutineHost` 的 asmdef 程序集名；
- IL2CPP 无 link.xml 会出现 `Curl error 42`/卡死（Phase 0 实锤）。

## 4. 接入示例（UWR）

```csharp
var host = new GameObject("LlmHost").AddComponent<UnityCoroutineHost>();
var transport = new UnityWebRequestTransport(host);
var stack = new TimeoutTransport(
    new RetryTransport(
        new MiddlewareTransport(
            new ObserverTransport(
                new FinalizerTransport(transport, signers),
                observer),
            new IRequestMiddleware[] { new ApiKeyMiddleware("key") }),
        new RetryOptions { MaxRetries = 2 },
        null,
        null));
var session = await stack.OpenAsync(request, new TransportOptions(), host.ShutdownToken);
```

WebSocket：`new ClientWebSocketTransport()`；HttpClient：`new HttpClientTransport()`；Auth 中间件按需组合。

## 5. 版本钉

- 消费方钉 `commit=<sha>`（本仓库 main），不跟 `main` 漂移；
- 升级：按 commit diff 比较 `src/`/`unity/` 路径，逐包搬运；
- 随源码保留 `LICENSE`、`NOTICE`、`VERSION`。
```

- [ ] **Step 3: 写 `docs/14-可观测与脱敏指引.md`**

```markdown
# 14 · 可观测与脱敏指引

> 本组件不提供日志实现；用现有 `ITransportObserver` 做观测，脱敏由宿主负责。

## 1. 可观测点

- `ObserverTransport.OnResponseAsync`：状态码/响应头（响应体消费前）；
- `ObserverTransport.OnBodyChunkAsync`：原始字节块（可统计字节数/首字节时间）；
- `RetryTransport`：重试次数/错误分类（经自定义 `IRetryPolicy` 或 wrapper）；
- `TransportException`：Kind/StatusCode/IsRetryable（由宿主记录）。

## 2. 脱敏规则（强制）

- **禁止**记录请求/响应中的 `Authorization`、`x-api-key`、`Proxy-Authorization`、Cookie、OAuth token；
- 禁止记录完整请求体/SSE 事件体（可能含 prompt/用户数据）；只记字节数/长度/耗时；
- URL 若含 key 查询参数（Google API key 等），日志前删除该参数值；
- 错误体显示前截断（建议 ≤4000 字符，pi `MAX_PROVIDER_ERROR_BODY_CHARS` 口径）。

## 3. 示例

```csharp
var observer = new MyObserver(); // 只累计 status/bytes/elapsed
var transport = new ObserverTransport(inner, observer);
```

`MyObserver` 不持有 header/body 原文；只写数字与枚举。
```

- [ ] **Step 4: 更新 `docs/11` §8 范围与状态**

把 §8 的任务/门禁替换为缩减版（与 spec §2 一致）：

```markdown
## 8. 阶段 6 · 稳定性加固与源码搬运

> 本阶段不打包；pi 直接搬运源码并钉 commit。

| 任务 | 内容 |
|---|---|
| S6-1 | 稳定性 soak：HttpClient/WebSocket/Auth 重复连接/长流/取消/上限 |
| S6-2 | 源码搬运指引（唯一 IsExternalInit/link.xml/asmdef/commit pin） |
| S6-3 | LICENSE/NOTICE/VERSION 与可观测/脱敏指引 |
| S6-4 | 四版本矩阵复跑 + 干净 Unity 工程搬运验证 |

**门禁**：dotnet 全绿；Release 0 errors/警告清零；Phase2/3/5 四版本矩阵复跑；按指引装配的 Unity 工程 W1/W3/W4 通过；用户验收。**状态**：待执行。
```

- [ ] **Step 5: 提交**

```bash
git add docs/13-源码搬运与集成指引.md docs/14-可观测与脱敏指引.md docs/11-开发阶段划分.md LICENSE NOTICE VERSION
git commit -m "docs(phase6): 源码搬运/脱敏指引 + LICENSE/NOTICE/VERSION"
```

---

### Task 4: Release 警告清零

**Files:**
- Modify: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientRequestBuilderTests.cs`
- Modify: `tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj`

**Interfaces:**
- Consumes: 现有测试。
- Produces: Release 构建 0 warnings。

- [ ] **Step 1: 修 xUnit2013**

`HttpClientRequestBuilderTests.cs` 中 `Assert.Equal(0, message.Headers.Count());` 改为：

```csharp
            Assert.Empty(message.Headers);
```

- [ ] **Step 2: 显式抑制 CA2022（测试中的有意单次读）**

在 `UnityLlmTransport.HttpClient.Tests.csproj` 的 `<PropertyGroup>` 增加：

```xml
    <!-- Tests intentionally read once from a repeatedly-read stream; CA2022 is not applicable. -->
    <NoWarn>$(NoWarn);CA2022</NoWarn>
```

- [ ] **Step 3: 跑 Release 构建确认 0 warnings**

```bash
dotnet build UnityLlmTransport.sln -c Release --nologo 2>&1 | grep -E "warning" || echo "OK: zero warnings"
```

Expected: `OK: zero warnings`。

- [ ] **Step 4: 提交**

```bash
git add tests/UnityLlmTransport.HttpClient.Tests/HttpClientRequestBuilderTests.cs tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj
git commit -m "chore(phase6): Release 测试警告清零"
```

### Task 5: 四版本矩阵复跑 + 搬运验证 + 发布收尾

**Files:**
- Create: `docs/spike/phase6-release-results.md`
- Create: `docs/spike/evidence/phase6/`（本轮原始行）
- Modify: `README.md`
- Modify: `docs/05-版本与兼容基线.md`
- Modify: `docs/11-开发阶段划分.md`（§8 状态）

**Interfaces:**
- Consumes: 现有 Phase2/Phase3/Phase5 构建与 harness、`docs/13` 搬运规则。
- Produces: 发布验收证据、README/docs 状态、pi 可钉的 commit。

- [ ] **Step 1: 复跑 Phase2（UWR）四版本 Mono/IL2CPP**

现有构建应位于 `D:/UnitySpikes/Builds/Phase2/<ver>/<backend>/Phase2.exe`（若缺失，按 Phase2 SOP 先 assemble+build）。

```bash
( tail -f /dev/null | dotnet run -c Release --project spikes/SseTestServer > /tmp/p6-sse.log 2>&1 ) &
sleep 2
for ver in 2022.3.62f3c1 2021.3.45f1c2 6000.3.28f1 6000.6.8f1; do
  for backend in mono il2cpp; do
    D:/UnitySpikes/Builds/Phase2/$ver/$backend/Phase2.exe -logFile D:/UnitySpikes/p6-phase2-$ver-$backend-player.log
    grep "PHASE2 " D:/UnitySpikes/p6-phase2-$ver-$backend-player.log > D:/UnitySpikes/p6-phase2-$ver-$backend-results.txt
  done
done
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like '*SseTestServer*' } | Select-Object ProcessId" | grep -oE '[0-9]+' | while read pid; do taskkill //PID "$pid" //T //F 2>/dev/null || true; done
netstat -ano | grep LISTENING | grep ':5199' || echo '5199 free'
```

Expected: 每份结果 15 行 OK（U1–U11）；8 组全过。

- [ ] **Step 2: 复跑 Phase3（HttpClient）四版本 Mono**

现有构建 `D:/UnitySpikes/Builds/Phase3/<ver>/mono/Phase3.exe`。

```bash
( tail -f /dev/null | dotnet run -c Release --project spikes/Phase3TestServer > /tmp/p6-hc.log 2>&1 ) &
sleep 3
for ver in 2022.3.62f3c1 2021.3.45f1c2 6000.3.28f1 6000.6.8f1; do
  D:/UnitySpikes/Builds/Phase3/$ver/mono/Phase3.exe -logFile D:/UnitySpikes/p6-phase3-$ver-mono-player.log
  grep "PHASE3 " D:/UnitySpikes/p6-phase3-$ver-mono-player.log > D:/UnitySpikes/p6-phase3-$ver-mono-results.txt
done
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like '*Phase3TestServer*' } | Select-Object ProcessId" | grep -oE '[0-9]+' | while read pid; do taskkill //PID "$pid" //T //F 2>/dev/null || true; done
netstat -ano | grep LISTENING | grep -E ':5201|:5233|:5234' || echo 'phase3 ports free'
```

Expected: 每份 12 行 OK（H1–H9 变体）；4 组全过。

- [ ] **Step 3: 重建/复跑 Phase5 四版本 Mono/IL2CPP**

WS 默认上限改变了产品代码，必须重建：

```bash
for row in "2022.3.62f3c1|D:/UnitySpikes/Phase5-2022|D:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe" \
           "2021.3.45f1|D:/UnitySpikes/Phase5-2021|D:/Unity/Hub/Editor/2021.3.45f1/Editor/Unity.exe" \
           "6000.3.28f1|D:/UnitySpikes/Phase5-6003|D:/Unity/Hub/Editor/6000.3.28f1/Editor/Unity.exe" \
           "6000.6.8f1|D:/UnitySpikes/Phase5-6006|D:/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe"; do
  ver="${row%%|*}"; rest="${row#*|}"; proj="${rest%%|*}"; editor="${rest#*|}"
  bash scripts/assemble-phase5-unity.sh "$proj"
  "$editor" -batchmode -quit -projectPath "$proj" -executeMethod Phase5Build.BuildMono -logFile "D:/UnitySpikes/p6-phase5-$ver-mono.log"
  grep -n "PHASE5_BUILD mono Succeeded" "D:/UnitySpikes/p6-phase5-$ver-mono.log" || (echo "MONO BUILD FAILED $ver"; exit 1)
  "$editor" -batchmode -quit -projectPath "$proj" -executeMethod Phase5Build.BuildIl2cpp -logFile "D:/UnitySpikes/p6-phase5-$ver-il2cpp.log"
  grep -n "PHASE5_BUILD il2cpp Succeeded" "D:/UnitySpikes/p6-phase5-$ver-il2cpp.log" || (echo "IL2CPP BUILD FAILED $ver"; exit 1)
done

( tail -f /dev/null | dotnet run -c Release --project spikes/WsTestServer > /tmp/p6-ws.log 2>&1 ) &
sleep 3
for ver in 2022.3.62f3c1 2021.3.45f1c2 6000.3.28f1 6000.6.8f1; do
  for backend in mono il2cpp; do
    D:/UnitySpikes/Builds/Phase5/$ver/$backend/Phase5.exe -logFile D:/UnitySpikes/p6-phase5-$ver-$backend-player.log
    grep "PHASE5 " D:/UnitySpikes/p6-phase5-$ver-$backend-player.log > D:/UnitySpikes/p6-phase5-$ver-$backend-results.txt
  done
done
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like '*WsTestServer*' } | Select-Object ProcessId" | grep -oE '[0-9]+' | while read pid; do taskkill //PID "$pid" //T //F 2>/dev/null || true; done
netstat -ano | grep LISTENING | grep ':5200' || echo '5200 free'
```

Expected: 每份 6 行 OK；8 组全过；W5 远程 wss 需外网/系统代理。

- [ ] **Step 4: 按 `docs/13` 搬运规则验证干净 Unity 工程**

```bash
mkdir -p D:/UnitySpikes/Phase6Vendor-2022
D:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe -createProject D:/UnitySpikes/Phase6Vendor-2022 -batchmode -quit -logFile D:/UnitySpikes/p6-vendor-create.log
proj=D:/UnitySpikes/Phase6Vendor-2022
dest=$proj/Assets/UnityLlmTransport
mkdir -p "$dest/Abstractions" "$dest/Sse" "$dest/HttpClient" "$dest/Auth" "$dest/WebSocket" "$dest/UwrCore" "$dest/UwrRuntime" "$dest/Hosts"
copy_cs() {
  local src="$1" dst="$2"
  find "$src" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print0 |
  while IFS= read -r -d '' file; do
    local rel="${file#"$src"/}"
    mkdir -p "$dst/$(dirname "$rel")"
    cp "$file" "$dst/$rel"
  done
}
copy_cs src/UnityLlmTransport.Abstractions "$dest/Abstractions"
copy_cs src/UnityLlmTransport.Sse "$dest/Sse"
copy_cs src/UnityLlmTransport.HttpClient "$dest/HttpClient"
copy_cs src/UnityLlmTransport.Auth "$dest/Auth"
copy_cs src/UnityLlmTransport.WebSocket "$dest/WebSocket"
copy_cs src/UnityLlmTransport.UnityWebRequest.Core "$dest/UwrCore"
copy_cs unity/UnityLlmTransport.UnityWebRequest/Runtime "$dest/UwrRuntime"
copy_cs unity/UnityLlmTransport.Hosts.Unity/Runtime "$dest/Hosts"
rm -f "$dest/Sse/Internal/IsExternalInit.cs" "$dest/HttpClient/Internal/IsExternalInit.cs" "$dest/Auth/Internal/IsExternalInit.cs" "$dest/WebSocket/Internal/IsExternalInit.cs" "$dest/UwrCore/Internal/IsExternalInit.cs"
cp unity/UnityLlmTransport.UnityWebRequest/link.xml "$proj/Assets/link.xml"
mkdir -p "$proj/Assets/Scripts" "$proj/Assets/Editor"
cp unity/Phase5Harness/Assets/Scripts/Phase5Runner.cs "$proj/Assets/Scripts/"
cp unity/Phase5Harness/Assets/Editor/Phase5Build.cs "$proj/Assets/Editor/"
D:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe -batchmode -quit -projectPath "$proj" -executeMethod Phase5Build.BuildMono -logFile D:/UnitySpikes/p6-vendor-build.log
grep -n "PHASE5_BUILD mono Succeeded" D:/UnitySpikes/p6-vendor-build.log | tail -1
( tail -f /dev/null | dotnet run -c Release --project spikes/WsTestServer > /tmp/p6-vendor-ws.log 2>&1 ) &
sleep 3
D:/UnitySpikes/Builds/Phase5/2022.3.62f3c1/mono/Phase5.exe -logFile D:/UnitySpikes/p6-vendor-player.log
grep "PHASE5 " D:/UnitySpikes/p6-vendor-player.log | tail -8
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like '*WsTestServer*' } | Select-Object ProcessId" | grep -oE '[0-9]+' | while read pid; do taskkill //PID "$pid" //T //F 2>/dev/null || true; done
```

Expected: 编译成功；W1/W3/W4 至少 OK（W2/W5/W6 若跑也记录）。注意构建输出目录与 Phase5-2022 相同，使用最新构建即为 vendor 工程构建。

- [ ] **Step 5: 写发布结果与证据**

创建 `docs/spike/phase6-release-results.md`：

```markdown
# Phase 6 · 稳定性与搬运发布结果

> 日期：2026-11-08。构建/运行命令见 plan `docs/superpowers/plans/2026-11-08-phase6-hardening-vendoring.md`。
> 本轮复跑，不修改 phase2/3/5 的历史结果文档；原始行在 `docs/spike/evidence/phase6/`。

## 1. dotnet 稳定性

| 套件 | 结果 |
|---|---|
| Core（UnityLlmTransport.Tests） | 53/53 |
| UWR（UnityLlmTransport.UnityWebRequest.Tests） | 47/47 |
| HttpClient（含 StabilityTests） | 56/56 |
| Auth（含 StabilityTests） | 26/26 |
| WebSocket（含 StabilityTests + 默认上限） | 44/44 |

## 2. 四版本矩阵复跑

| 传输 | 2022.3 | 2021.3 | 6.3 | 6.6 | 判据 |
|---|---|---|---|---|---|
| Phase2 UWR Mono/IL2CPP | | | | | U1–U11 15/15 |
| Phase3 HttpClient Mono | | | | | H1–H9 12/12 |
| Phase5 WebSocket Mono/IL2CPP | | | | | W1–W6 6/6 |

## 3. 源码搬运验证

- 工程：`D:/UnitySpikes/Phase6Vendor-2022`（按 `docs/13` 手工搬运）
- 结果：W1/W3/W4（及 W2/W5/W6）记录；编译/运行结论。

## 4. 结论 / 已知遗留

- WS 默认 16MiB 上限已验证；
- Unity accept-all wss/客户端证书仍 dotnet-only（Phase 5 遗留）；
- 打包（NuGet/UPM/zip）后置，等 pi 验收；
- 版本：`VERSION=0.6.0`，无公共 tag。
```

把本轮 `D:/UnitySpikes/p6-*-results.txt` 复制为 `docs/spike/evidence/phase6/`（同名文件），并复制 vendor player 的 PHASE5 行。空=未过，不得推断。

- [ ] **Step 6: 更新 README / docs/05 / docs/11**

`README.md` 状态行：

```markdown
> **状态**：Phase 0/1/2/3/4/5/6 已完成（源码搬运形态，`VERSION 0.6.0`）；pi 按 commit 搬运源码；正式打包（NuGet/UPM/zip）待消费方验收后再评估。
```

`docs/05` §8 版本记录追加：

```markdown
| 2026-11-08 | Phase 6 稳定性/搬运发布：三传输 soak、WS 默认 16MiB、四版本 Phase2/3/5 矩阵复跑、源码搬运验证；不做 NuGet/UPM/zip（`docs/13`/`docs/14`/`phase6-release-results.md`） |
```

`docs/11` §8 状态改为：

```markdown
**状态**：已完成（2026-11-08）；不发布包，pi 按 `docs/13` 搬运源码并钉 commit。
```

- [ ] **Step 7: 阶段门禁**

```bash
dotnet build UnityLlmTransport.sln -c Release --nologo 2>&1 | grep -E "warning|error" || echo "OK: zero warnings/errors"
for p in tests/UnityLlmTransport.Tests/UnityLlmTransport.Tests.csproj tests/UnityLlmTransport.UnityWebRequest.Tests/UnityLlmTransport.UnityWebRequest.Tests.csproj tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj tests/UnityLlmTransport.Auth.Tests/UnityLlmTransport.Auth.Tests.csproj tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj; do dotnet test "$p" --nologo | tail -1; done
grep -rn "UnityEngine" src --include='*.cs' || echo "OK: src zero UnityEngine"
grep -rn "PiUnityAgent" src --include='*.cs' || echo "OK: src no consumer references"
```

Expected: 零警告/错误；五套件 53/47/56/26/44 全绿；两个 OK。

- [ ] **Step 8: 提交**

```bash
git add README.md docs/05-版本与兼容基线.md docs/11-开发阶段划分.md docs/spike/phase6-release-results.md docs/spike/evidence/phase6
git commit -m "docs(phase6): 稳定性/四版本复跑/源码搬运发布结果"
```

---

## 验收对照（Spec §8）

| Spec 验收项 | 落点 |
|---|---|
| 三个 StabilityTests 全绿 | Task 2 |
| 审计清单结论 | Task 2（失败即修复记录） |
| WS 默认 16MiB | Task 1 |
| `docs/13` 搬运指引 | Task 3 |
| `docs/14` 可观测/脱敏 | Task 3 |
| LICENSE/NOTICE/VERSION | Task 3 |
| 全量测试 + Release 0 errors/警告清零 | Task 4 + Task 5 Step 7 |
| 四版本矩阵复跑 | Task 5 Steps 1–3 |
| 搬运验证 W1/W3/W4 | Task 5 Step 4 |
| 用户验收 + pi commit pin | 合并后由用户/pi 执行 |

## 执行注意

- Phase2/Phase3 构建若缺失，按各自 SOP 重建；本轮不修改其产品代码。
- 服务必须 PID 级清理；Phase3/Sse/WsTestServer 均为 apphost+HttpListener，禁止 mass-kill/PID 4。
- 固定端口串行；远程 W5 依赖外网/系统代理，失败记录环境。
- 不修改 phase2/3/5 历史结果文档；本轮结果写 `phase6-release-results.md`。


