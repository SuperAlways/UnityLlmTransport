# Phase 5 · WebSocket 核心（传输侧）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 `UnityLlmTransport.WebSocket`：基于 `System.Net.WebSockets.ClientWebSocket` 的通用 `IWebSocketTransport`/session（连接/收发/Close/Abort/超时/消息上限），四版本 Unity Mono + IL2CPP 验证；不包含 WS→SSE 回退、消费方集成、RawTcp/WebGL。

**Architecture:** 无状态 transport（每次连接一个 `ClientWebSocket`）；session 负责发送串行化、帧级接收（`EndOfMessage`）、Close/Abort、Idle/消息上限与生命周期取消。错误统一经 `WebSocketErrorClassifier` 映射到 `TransportException`；回退/重连/缓存由消费方或后续阶段负责。

**Tech Stack:** C# 9、netstandard2.1、`System.Net.WebSockets.ClientWebSocket`、net10.0 xUnit、进程内 Kestrel `UseWebSockets`（测试）、Unity 四版本 Mono/IL2CPP harness。

## Global Constraints

- 包工程：`netstandard2.1`、`<LangVersion>9.0</LangVersion>`、`<Nullable>enable</Nullable>`、`<ImplicitUsings>disable</ImplicitUsings>`，零 `UnityEngine`、零第三方 NuGet。
- 不修改 `Abstractions`/`Sse`/UWR/HttpClient/Auth 公共形状；只新增 WebSocket 包/测试/harness。
- **不包含** WS→SSE 回退、自动重连/`WebSocketCached`、RawTcp、WebGL、消费方集成/门禁。
- 代理：仅 System；`Explicit`/`None` → `Unsupported`。TLS：默认/accept-all/客户端证书；`CustomRoots`/`Protocols` → `Unsupported`。`Redirects` 仅 `Error`；`Follow` → `Unsupported`。
- Capabilities 固定：`WebSocket | WebSocketOverTls | SystemProxy | TlsCertificateHandler`；不声明 `StreamingBody`/`ExplicitProxy`/`ProxyAuthentication`/`Http2`/`RequestFinalizer`。
- 四版本：`2022.3.62f3c1`（主基线）、`2021.3.45f1`、`6000.3.28f1`、`6000.6.8f1`；WebGL 不测。
- 显式 `git add`，禁 `git add -A`；不提交 bin/obj/Library。
- 测试端口：dotnet `WsTestSupport` 用 5271/5273；Unity harness 用现有 `WsTestServer` 5200；远程 wss `wss://ws.postman-echo.com/raw`。

## 文件结构

```
src/UnityLlmTransport.WebSocket/
  UnityLlmTransport.WebSocket.csproj
  Internal/IsExternalInit.cs
  Internal/AssemblyInfo.cs
  WebSocketErrorClassifier.cs          # internal
  WebSocketBufferBudget.cs             # internal
  ClientWebSocketTransport.cs
  ClientWebSocketSession.cs            # internal
tests/UnityLlmTransport.WebSocket.Tests/
  UnityLlmTransport.WebSocket.Tests.csproj
  WsTestSupport.cs                     # 进程内 Kestrel UseWebSockets（http+https 自签）
  WebSocketErrorClassifierTests.cs
  WebSocketBufferBudgetTests.cs
  ClientWebSocketTransportTests.cs
  ClientWebSocketSessionTests.cs
  WebSocketTlsAndOptionsTests.cs
unity/Phase5Harness/
  README.md
  Assets/Scripts/Phase5Runner.cs
  Assets/Editor/Phase5Build.cs
scripts/assemble-phase5-unity.sh
```

---

### Task 1: WebSocket 包骨架 + 错误分类 + 消息预算

**Files:**
- Create: `src/UnityLlmTransport.WebSocket/UnityLlmTransport.WebSocket.csproj`
- Create: `src/UnityLlmTransport.WebSocket/Internal/IsExternalInit.cs`
- Create: `src/UnityLlmTransport.WebSocket/Internal/AssemblyInfo.cs`
- Create: `src/UnityLlmTransport.WebSocket/WebSocketErrorClassifier.cs`
- Create: `src/UnityLlmTransport.WebSocket/WebSocketBufferBudget.cs`
- Create: `tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj`
- Test: `tests/UnityLlmTransport.WebSocket.Tests/WebSocketErrorClassifierTests.cs`
- Test: `tests/UnityLlmTransport.WebSocket.Tests/WebSocketBufferBudgetTests.cs`
- Modify: `UnityLlmTransport.sln`

**Interfaces:**
- Consumes: `TransportException`/`TransportErrorKind`。
- Produces: `WebSocketErrorClassifier.ClassifyConnect(Exception, bool callerCancelled, bool connectTimeout) → TransportException`；`WebSocketErrorClassifier.ClassifyOperation(Exception, bool callerCancelled) → TransportException`；`WebSocketBufferBudget(int? maxBytes)` / `Add(int)` / `Reset()`。

- [ ] **Step 1: 建包与测试工程**

`src/UnityLlmTransport.WebSocket/UnityLlmTransport.WebSocket.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\UnityLlmTransport.Abstractions\UnityLlmTransport.Abstractions.csproj" />
  </ItemGroup>
</Project>
```

`src/UnityLlmTransport.WebSocket/Internal/IsExternalInit.cs`：

```csharp
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
```

`src/UnityLlmTransport.WebSocket/Internal/AssemblyInfo.cs`：

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnityLlmTransport.WebSocket.Tests")]
```

`tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\UnityLlmTransport.Abstractions\UnityLlmTransport.Abstractions.csproj" />
    <ProjectReference Include="..\..\src\UnityLlmTransport.WebSocket\UnityLlmTransport.WebSocket.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln UnityLlmTransport.sln add src/UnityLlmTransport.WebSocket/UnityLlmTransport.WebSocket.csproj
dotnet sln UnityLlmTransport.sln add tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj
```

Expected: 两行 `has been added to the solution.`。

- [ ] **Step 2: 写失败测试**

`tests/UnityLlmTransport.WebSocket.Tests/WebSocketErrorClassifierTests.cs`：

```csharp
using System;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class WebSocketErrorClassifierTests
    {
        [Fact]
        public void Caller_Cancelled_Is_Cancelled()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new OperationCanceledException(), true, false);
            Assert.Equal(TransportErrorKind.Cancelled, error.Kind);
        }

        [Fact]
        public void Connect_Timeout_Is_Retryable_Connect()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new OperationCanceledException(), false, true);
            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public void Authentication_Exception_Is_Tls()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new AuthenticationException("bad cert"), false, false);
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void Socket_Inner_Exception_Is_Connect()
        {
            var inner = new SocketException((int)SocketError.ConnectionRefused);
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new WebSocketException("Unable to connect", inner), false, false);
            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public void Ssl_Text_Is_Tls()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new WebSocketException("SSL certificate error"), false, false);
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
        }

        [Fact]
        public void Other_WebSocketException_Is_Handshake()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new WebSocketException("The server returned status code '400'"), false, false);
            Assert.Equal(TransportErrorKind.Handshake, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void UriFormat_Is_Protocol()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new UriFormatException("bad uri"), false, false);
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }
    }
}
```

`tests/UnityLlmTransport.WebSocket.Tests/WebSocketBufferBudgetTests.cs`：

```csharp
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class WebSocketBufferBudgetTests
    {
        [Fact]
        public void Null_Max_Is_Unlimited()
        {
            var budget = new WebSocketBufferBudget(null);
            Assert.True(budget.Add(int.MaxValue));
        }

        [Fact]
        public void Accumulates_Until_Reset()
        {
            var budget = new WebSocketBufferBudget(10);
            Assert.True(budget.Add(4));
            Assert.True(budget.Add(6));
            Assert.False(budget.Add(1));

            budget.Reset();
            Assert.True(budget.Add(10));
        }

        [Fact]
        public void Exceed_Returns_False_And_Keeps_Count()
        {
            var budget = new WebSocketBufferBudget(5);
            Assert.True(budget.Add(4));
            Assert.False(budget.Add(2));
            Assert.False(budget.Add(0));
            budget.Reset();
            Assert.True(budget.Add(5));
        }

        [Fact]
        public void Negative_Count_Throws()
        {
            var budget = new WebSocketBufferBudget(5);
            Assert.Throws<System.ArgumentOutOfRangeException>(() => budget.Add(-1));
        }
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo`
Expected: `error CS0246`（找不到分类器/预算/包）。

- [ ] **Step 4: 实现分类器与预算**

`src/UnityLlmTransport.WebSocket/WebSocketErrorClassifier.cs`：

```csharp
using System;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace UnityLlmTransport
{
    internal static class WebSocketErrorClassifier
    {
        public static TransportException ClassifyConnect(Exception exception, bool callerCancelled, bool connectTimeout)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            if (exception is OperationCanceledException)
            {
                if (callerCancelled)
                {
                    return new TransportException(
                        TransportErrorKind.Cancelled,
                        "WebSocket connect cancelled.",
                        exception,
                        null,
                        null,
                        null,
                        false);
                }
                if (connectTimeout)
                {
                    return new TransportException(
                        TransportErrorKind.Connect,
                        "WebSocket connect timeout.",
                        exception,
                        null,
                        null,
                        null,
                        true);
                }
                return new TransportException(
                    TransportErrorKind.Connect,
                    "WebSocket connect canceled before handshake.",
                    exception,
                    null,
                    null,
                    null,
                    true);
            }

            if (exception is AuthenticationException)
            {
                return new TransportException(
                    TransportErrorKind.Tls,
                    "WebSocket TLS handshake failed: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            var webSocketException = exception as WebSocketException;
            if (webSocketException != null)
            {
                var inner = webSocketException.InnerException;
                if (inner is AuthenticationException)
                {
                    return new TransportException(
                        TransportErrorKind.Tls,
                        "WebSocket TLS handshake failed: " + inner.Message,
                        exception,
                        null,
                        null,
                        null,
                        false);
                }

                string text = webSocketException.Message + " " + (inner != null ? inner.Message : string.Empty);
                if (Contains(text, "SSL") || Contains(text, "certificate"))
                {
                    return new TransportException(
                        TransportErrorKind.Tls,
                        "WebSocket TLS handshake failed: " + text,
                        exception,
                        null,
                        null,
                        null,
                        false);
                }
                if (inner is SocketException || Contains(text, "refused") || Contains(text, "Unable to connect"))
                {
                    return new TransportException(
                        TransportErrorKind.Connect,
                        "WebSocket connection failed: " + text,
                        exception,
                        null,
                        null,
                        null,
                        true);
                }
                return new TransportException(
                    TransportErrorKind.Handshake,
                    "WebSocket handshake failed: " + text,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            if (exception is UriFormatException ||
                exception is ArgumentException ||
                exception is FormatException)
            {
                return new TransportException(
                    TransportErrorKind.Protocol,
                    "Invalid WebSocket request: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            return new TransportException(
                TransportErrorKind.Protocol,
                "WebSocket connect failed: " + exception.Message,
                exception,
                null,
                null,
                null,
                false);
        }

        public static TransportException ClassifyOperation(Exception exception, bool callerCancelled)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }
            if (exception is OperationCanceledException && callerCancelled)
            {
                return new TransportException(
                    TransportErrorKind.Cancelled,
                    "WebSocket operation cancelled.",
                    exception,
                    null,
                    null,
                    null,
                    false);
            }
            if (exception is WebSocketException || exception is System.IO.IOException)
            {
                return new TransportException(
                    TransportErrorKind.Protocol,
                    "WebSocket operation failed: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }
            return new TransportException(
                TransportErrorKind.Protocol,
                "WebSocket operation failed: " + exception.Message,
                exception,
                null,
                null,
                null,
                false);
        }

        private static bool Contains(string value, string fragment)
        {
            return value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
```

`src/UnityLlmTransport.WebSocket/WebSocketBufferBudget.cs`：

```csharp
using System;

namespace UnityLlmTransport
{
    internal sealed class WebSocketBufferBudget
    {
        private readonly int? _maxBytes;
        private int _currentBytes;

        public WebSocketBufferBudget(int? maxBytes)
        {
            _maxBytes = maxBytes;
        }

        public bool Add(int count)
        {
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            _currentBytes += count;
            return _maxBytes == null || _currentBytes <= _maxBytes.Value;
        }

        public void Reset()
        {
            _currentBytes = 0;
        }
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 11`。

- [ ] **Step 6: 提交**

```bash
git add src/UnityLlmTransport.WebSocket/UnityLlmTransport.WebSocket.csproj src/UnityLlmTransport.WebSocket/Internal/IsExternalInit.cs src/UnityLlmTransport.WebSocket/Internal/AssemblyInfo.cs src/UnityLlmTransport.WebSocket/WebSocketErrorClassifier.cs src/UnityLlmTransport.WebSocket/WebSocketBufferBudget.cs tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj tests/UnityLlmTransport.WebSocket.Tests/WebSocketErrorClassifierTests.cs tests/UnityLlmTransport.WebSocket.Tests/WebSocketBufferBudgetTests.cs UnityLlmTransport.sln
git commit -m "feat(ws): 包骨架/错误分类/消息预算"
```

### Task 2: 进程内 WsTestServer + ClientWebSocketTransport/Session（基础收发）

**Files:**
- Create: `tests/UnityLlmTransport.WebSocket.Tests/WsTestSupport.cs`
- Create: `src/UnityLlmTransport.WebSocket/ClientWebSocketTransport.cs`
- Create: `src/UnityLlmTransport.WebSocket/ClientWebSocketSession.cs`
- Test: `tests/UnityLlmTransport.WebSocket.Tests/ClientWebSocketTransportTests.cs`

**Interfaces:**
- Consumes: `IWebSocketTransport`/`IWebSocketSession`/`WebSocketOptions`/`WebSocketMessage`、`WebSocketErrorClassifier`、`WebSocketBufferBudget`。
- Produces: `ClientWebSocketTransport()`；`ConnectAsync`（ws/wss）；`ClientWebSocketSession`（Send/Receive/Close/Abort/Dispose）；`WsTestServer.Start(5271,5273)` 进程内测试服务。

- [ ] **Step 1: 写测试服务与基础测试**

`tests/UnityLlmTransport.WebSocket.Tests/WsTestSupport.cs`：

```csharp
using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace UnityLlmTransport.Tests
{
    public sealed class WsTestServer
    {
        private readonly WebApplication _app;
        private readonly X509Certificate2 _certificate;

        public string HttpBase { get; }
        public string HttpsBase { get; }

        public static WsTestServer Start(int httpPort, int httpsPort)
        {
            var certificate = CreateSelfSignedCertificate();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = Environments.Production
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(httpPort);
                options.ListenAnyIP(httpsPort, listen => listen.UseHttps(certificate));
            });

            var app = builder.Build();
            app.UseWebSockets();
            app.Run(HandleAsync);
            app.StartAsync().GetAwaiter().GetResult();

            return new WsTestServer(app, certificate, httpPort, httpsPort);
        }

        private WsTestServer(WebApplication app, X509Certificate2 certificate, int httpPort, int httpsPort)
        {
            _app = app;
            _certificate = certificate;
            HttpBase = "http://127.0.0.1:" + httpPort;
            HttpsBase = "https://127.0.0.1:" + httpsPort;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            _app.Dispose();
            _certificate.Dispose();
        }

        private static async Task HandleAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? "/";
            if (path == "/ws/slow")
            {
                await Task.Delay(500);
            }
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                switch (path)
                {
                    case "/ws/echo":
                        await EchoAsync(socket);
                        break;
                    case "/ws/fragment":
                        await SendAsync(socket, Encoding.UTF8.GetBytes("hel"), WebSocketMessageType.Text, false);
                        await SendAsync(socket, Encoding.UTF8.GetBytes("lo"), WebSocketMessageType.Text, true);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    case "/ws/close":
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    case "/ws/idle":
                        await Task.Delay(500);
                        await SendAsync(socket, Encoding.UTF8.GetBytes("late"), WebSocketMessageType.Text, true);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    case "/ws/drop":
                        socket.Abort();
                        break;
                    case "/ws/info":
                        var info = JsonSerializer.Serialize(new
                        {
                            protocols = context.WebSockets.WebSocketRequestedProtocols,
                            headers = context.Request.Headers.ToDictionary(
                                pair => pair.Key,
                                pair => pair.Value.ToString())
                        });
                        await SendAsync(socket, Encoding.UTF8.GetBytes(info), WebSocketMessageType.Text, true);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    case "/ws/slow":
                        await SendAsync(socket, Encoding.UTF8.GetBytes("slow"), WebSocketMessageType.Text, true);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    default:
                        context.Response.StatusCode = 404;
                        break;
                }
            }
            catch (Exception)
            {
                try { socket.Abort(); } catch { }
            }
        }

        private static async Task EchoAsync(WebSocket socket)
        {
            var buffer = new byte[4096];
            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = "echo:" + Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await SendAsync(socket, Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, result.EndOfMessage);
                }
                else
                {
                    await SendAsync(socket, buffer.AsSpan(0, result.Count).ToArray(), WebSocketMessageType.Binary, result.EndOfMessage);
                }
            }
        }

        private static Task SendAsync(WebSocket socket, byte[] payload, WebSocketMessageType type, bool endOfMessage)
        {
            return socket.SendAsync(new ArraySegment<byte>(payload), type, endOfMessage, CancellationToken.None);
        }

        private static X509Certificate2 CreateSelfSignedCertificate()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest(
                "CN=localhost",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            san.AddDnsName("localhost");
            san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());

            using var ephemeral = request.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddDays(-1),
                DateTimeOffset.UtcNow.AddDays(1));
            return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
        }
    }

    public sealed class WsServerFixture : IAsyncLifetime
    {
        public WsTestServer Server { get; private set; } = null!;

        public Task InitializeAsync()
        {
            Server = WsTestServer.Start(5271, 5273);
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            return Server.DisposeAsync().AsTask();
        }
    }

    [CollectionDefinition("ws-server")]
    public class WsServerCollection : ICollectionFixture<WsServerFixture> { }
}
```

`tests/UnityLlmTransport.WebSocket.Tests/ClientWebSocketTransportTests.cs`：

```csharp
using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("ws-server")]
    public class ClientWebSocketTransportTests
    {
        private readonly WsServerFixture _fixture;

        public ClientWebSocketTransportTests(WsServerFixture fixture)
        {
            _fixture = fixture;
        }

        private static WebSocketRequest Request(string url, TransportHeaders? headers = null)
        {
            return new WebSocketRequest(url, headers ?? new TransportHeaders());
        }

        [Fact]
        public async Task Echo_Text()
        {
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request(_fixture.Server.HttpBase + "/ws/echo"),
                new WebSocketOptions(),
                CancellationToken.None);
            await session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hello"), true),
                CancellationToken.None);

            var received = await ReceiveOne(session);
            Assert.Equal(WebSocketMessageType.Text, received.Type);
            Assert.Equal("echo:hello", Encoding.UTF8.GetString(received.Payload.ToArray()));
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Echo_Binary()
        {
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request(_fixture.Server.HttpBase + "/ws/echo"),
                new WebSocketOptions(),
                CancellationToken.None);
            var payload = new byte[] { 1, 2, 3 };
            await session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Binary, payload, true),
                CancellationToken.None);

            var received = await ReceiveOne(session);
            Assert.Equal(WebSocketMessageType.Binary, received.Type);
            Assert.Equal(payload, received.Payload.ToArray());
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Server_Close_Yields_Close_Message()
        {
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request(_fixture.Server.HttpBase + "/ws/close"),
                new WebSocketOptions(),
                CancellationToken.None);

            var message = await ReceiveOne(session);
            Assert.Equal(WebSocketMessageType.Close, message.Type);
            Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, message.CloseStatus);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Info_Request_Headers_And_SubProtocol_Reach_Server()
        {
            var headers = new TransportHeaders
            {
                ["X-Test"] = "yes",
                ["X-Skip"] = null
            };
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request(
                    _fixture.Server.HttpBase + "/ws/info",
                    headers),
                new WebSocketOptions { SubProtocol = "phase5" },
                CancellationToken.None);

            var message = await ReceiveOne(session);
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(message.Payload.ToArray()));
            var protocols = json.RootElement.GetProperty("protocols");
            Assert.Contains("phase5", protocols.EnumerateArray());
            var echoed = json.RootElement.GetProperty("headers");
            Assert.True(echoed.TryGetProperty("X-Test", out var test));
            Assert.Equal("yes", test.GetString());
            Assert.False(echoed.TryGetProperty("X-Skip", out _));
            await session.DisposeAsync();
        }

        private static async Task<WebSocketMessage> ReceiveOne(IWebSocketSession session)
        {
            await foreach (var message in session.ReceiveAsync(CancellationToken.None))
            {
                return message;
            }
            throw new InvalidOperationException("no message received");
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo`
Expected: `error CS0246`（找不到 `ClientWebSocketTransport`）。

- [ ] **Step 3: 实现 Transport/Session**

`src/UnityLlmTransport.WebSocket/ClientWebSocketTransport.cs`：

```csharp
using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed class ClientWebSocketTransport : IWebSocketTransport
    {
        private static readonly TransportCapabilities DefaultCapabilities =
            TransportCapabilities.WebSocket |
            TransportCapabilities.WebSocketOverTls |
            TransportCapabilities.SystemProxy |
            TransportCapabilities.TlsCertificateHandler;

        public TransportCapabilities Capabilities => DefaultCapabilities;

        public async Task<IWebSocketSession> ConnectAsync(
            WebSocketRequest request,
            WebSocketOptions options,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            Uri uri;
            try
            {
                uri = new Uri(request.Url, UriKind.Absolute);
            }
            catch (Exception ex)
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Invalid WebSocket URL: " + request.Url,
                    ex,
                    null,
                    null,
                    null,
                    false);
            }
            if (uri.Scheme != "ws" && uri.Scheme != "wss")
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "WebSocket URL must use ws:// or wss://; got: " + uri.Scheme);
            }

            var socket = new ClientWebSocket();
            try
            {
                ConfigureSocket(socket.Options, request, options);
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            CancellationTokenSource? timeoutCts = null;
            CancellationTokenSource? linkedCts = null;
            try
            {
                CancellationToken token = cancellationToken;
                if (options.ConnectTimeout.HasValue && options.ConnectTimeout.Value > TimeSpan.Zero)
                {
                    timeoutCts = new CancellationTokenSource(options.ConnectTimeout.Value);
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    token = linkedCts.Token;
                }

                try
                {
                    await socket.ConnectAsync(uri, token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    bool callerCancelled = cancellationToken.IsCancellationRequested;
                    bool connectTimeout = timeoutCts != null &&
                                          timeoutCts.IsCancellationRequested &&
                                          !callerCancelled;
                    socket.Dispose();
                    throw WebSocketErrorClassifier.ClassifyConnect(ex, callerCancelled, connectTimeout);
                }

                return new ClientWebSocketSession(socket, options, cancellationToken);
            }
            finally
            {
                linkedCts?.Dispose();
                timeoutCts?.Dispose();
            }
        }

        private static void ConfigureSocket(
            ClientWebSocketOptions socketOptions,
            WebSocketRequest request,
            WebSocketOptions options)
        {
            if (options.Proxy != null && options.Proxy.Mode != ProxyMode.System)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "ClientWebSocket supports only the system proxy.");
            }

            if (options.Redirects != RedirectPolicy.Error)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "ClientWebSocket does not support configurable handshake redirects.");
            }

            if (options.Tls != null)
            {
                if (options.Tls.CustomRoots != null || options.Tls.Protocols != null)
                {
                    throw new TransportException(
                        TransportErrorKind.Unsupported,
                        "ClientWebSocket supports default validation, accept-all, and client certificates only.");
                }
                if (!options.Tls.ValidateCertificate)
                {
                    socketOptions.RemoteCertificateValidationCallback = (sender, certificate, chain, errors) => true;
                }
                if (options.Tls.ClientCertificate != null)
                {
                    socketOptions.ClientCertificates.Add(options.Tls.ClientCertificate);
                }
            }

            if (!string.IsNullOrEmpty(options.SubProtocol))
            {
                socketOptions.AddSubProtocol(options.SubProtocol);
            }

            if (!string.IsNullOrEmpty(options.Origin) &&
                !request.Headers.ContainsKey("Origin"))
            {
                socketOptions.SetRequestHeader("Origin", options.Origin);
            }

            foreach (var pair in request.Headers)
            {
                if (pair.Value != null)
                {
                    socketOptions.SetRequestHeader(pair.Key, pair.Value);
                }
            }

            if (options.KeepAliveInterval.HasValue && options.KeepAliveInterval.Value > TimeSpan.Zero)
            {
                socketOptions.KeepAliveInterval = options.KeepAliveInterval.Value;
            }
        }
    }
}
```

`src/UnityLlmTransport.WebSocket/ClientWebSocketSession.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    internal sealed class ClientWebSocketSession : IWebSocketSession
    {
        private const int ReceiveBufferSize = 16 * 1024;

        private readonly ClientWebSocket _socket;
        private readonly WebSocketOptions _options;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly WebSocketBufferBudget _budget;
        private readonly CancellationTokenRegistration _registration;
        private TransportException? _terminalError;
        private int _receiveStarted;
        private int _disposed;

        public ClientWebSocketSession(
            ClientWebSocket socket,
            WebSocketOptions options,
            CancellationToken lifetimeToken)
        {
            _socket = socket;
            _options = options;
            _budget = new WebSocketBufferBudget(options.MaxMessageBytes);
            _registration = lifetimeToken.Register(() => AbortWith(TransportErrorKind.Cancelled));
        }

        public WebSocketState State => _socket.State;

        public async Task SendAsync(WebSocketMessage message, CancellationToken ct)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            if (message.Type == WebSocketMessageType.Close)
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Close must be sent with CloseAsync, not SendAsync.");
            }

            ThrowIfTerminal();
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfTerminal();
                if (_socket.State != WebSocketState.Open)
                {
                    throw new TransportException(TransportErrorKind.Aborted, "WebSocket is not open.");
                }

                var type = message.Type == WebSocketMessageType.Text
                    ? System.Net.WebSockets.WebSocketMessageType.Text
                    : System.Net.WebSockets.WebSocketMessageType.Binary;
                await _socket.SendAsync(
                    new ArraySegment<byte>(message.Payload.ToArray()),
                    type,
                    message.EndOfMessage,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TransportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowIfTerminal();
                throw WebSocketErrorClassifier.ClassifyOperation(ex, ct.IsCancellationRequested);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async IAsyncEnumerable<WebSocketMessage> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _receiveStarted, 1) != 0)
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Only one ReceiveAsync enumeration is supported per session.");
            }

            var buffer = new byte[ReceiveBufferSize];
            while (true)
            {
                ThrowIfTerminal();
                if (_socket.State != WebSocketState.Open && _socket.State != WebSocketState.CloseReceived)
                {
                    yield break;
                }

                CancellationTokenSource? idleCts = null;
                CancellationTokenSource? linkedCts = null;
                WebSocketReceiveResult result;
                try
                {
                    CancellationToken token = ct;
                    if (_options.IdleTimeout.HasValue && _options.IdleTimeout.Value > TimeSpan.Zero)
                    {
                        idleCts = new CancellationTokenSource(_options.IdleTimeout.Value);
                        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);
                        token = linkedCts.Token;
                    }

                    result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (
                    !ct.IsCancellationRequested &&
                    idleCts != null &&
                    idleCts.IsCancellationRequested)
                {
                    AbortWith(TransportErrorKind.IdleTimeout);
                    throw new TransportException(
                        TransportErrorKind.IdleTimeout,
                        "WebSocket idle timeout.",
                        ex,
                        null,
                        null,
                        null,
                        false);
                }
                catch (OperationCanceledException ex)
                {
                    AbortWith(TransportErrorKind.Cancelled);
                    throw new TransportException(
                        TransportErrorKind.Cancelled,
                        "WebSocket receive cancelled.",
                        ex,
                        null,
                        null,
                        null,
                        false);
                }
                catch (Exception ex)
                {
                    ThrowIfTerminal();
                    AbortWith(TransportErrorKind.Protocol);
                    throw WebSocketErrorClassifier.ClassifyOperation(ex, ct.IsCancellationRequested);
                }
                finally
                {
                    linkedCts?.Dispose();
                    idleCts?.Dispose();
                }

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    yield return new WebSocketMessage(
                        WebSocketMessageType.Close,
                        ReadOnlyMemory<byte>.Empty,
                        true,
                        result.CloseStatus);
                    yield break;
                }

                var type = result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text
                    ? WebSocketMessageType.Text
                    : WebSocketMessageType.Binary;
                var payload = new ReadOnlyMemory<byte>(
                    buffer.AsSpan(0, result.Count).ToArray());
                if (!_budget.Add(result.Count))
                {
                    AbortWith(TransportErrorKind.Overflow);
                    throw new TransportException(
                        TransportErrorKind.Overflow,
                        "WebSocket message exceeded MaxMessageBytes.");
                }

                yield return new WebSocketMessage(type, payload, result.EndOfMessage);
                if (result.EndOfMessage)
                {
                    _budget.Reset();
                }
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            ThrowIfTerminal();
            if (_socket.State == WebSocketState.Closed || _socket.State == WebSocketState.Aborted)
            {
                return;
            }
            try
            {
                await _socket.CloseAsync(status, description, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowIfTerminal();
                throw WebSocketErrorClassifier.ClassifyOperation(ex, ct.IsCancellationRequested);
            }
        }

        public void Abort()
        {
            AbortWith(TransportErrorKind.Aborted);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                AbortWith(TransportErrorKind.Aborted);
                _socket.Dispose();
                _registration.Dispose();
                _sendLock.Dispose();
            }
            return default;
        }

        private void AbortWith(TransportErrorKind kind)
        {
            var error = new TransportException(kind, "WebSocket " + kind + ".");
            if (Interlocked.CompareExchange(ref _terminalError, error, null) == null)
            {
                try { _socket.Abort(); } catch { }
            }
        }

        private void ThrowIfTerminal()
        {
            var error = Volatile.Read(ref _terminalError);
            if (error != null)
            {
                throw error;
            }
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 15`。

- [ ] **Step 5: 提交**

```bash
git add tests/UnityLlmTransport.WebSocket.Tests/WsTestSupport.cs tests/UnityLlmTransport.WebSocket.Tests/ClientWebSocketTransportTests.cs src/UnityLlmTransport.WebSocket/ClientWebSocketTransport.cs src/UnityLlmTransport.WebSocket/ClientWebSocketSession.cs
git commit -m "feat(ws): ClientWebSocket 传输/会话 + 进程内测试服务"
```

### Task 3: 会话边界/超时/TLS/能力测试

**Files:**
- Create: `tests/UnityLlmTransport.WebSocket.Tests/ClientWebSocketSessionTests.cs`
- Create: `tests/UnityLlmTransport.WebSocket.Tests/WebSocketTlsAndOptionsTests.cs`

**Interfaces:**
- Consumes: Task 2 的 transport/session；`DefaultTransportSelector`/`TransportSelectionContext`/`TransportFallbackPolicy`。
- Produces: 分帧/Close/Abort/ConnectTimeout/IdleTimeout/MaxMessageBytes/取消/丢流/选项边界/TLS/能力/Selector 的自动化证据。

- [ ] **Step 1: 写会话边界测试**

`tests/UnityLlmTransport.WebSocket.Tests/ClientWebSocketSessionTests.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("ws-server")]
    public class ClientWebSocketSessionTests
    {
        private readonly WsServerFixture _fixture;

        public ClientWebSocketSessionTests(WsServerFixture fixture)
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

        private static async Task<WebSocketMessage> ReceiveOne(IWebSocketSession session, CancellationToken ct = default)
        {
            await foreach (var message in session.ReceiveAsync(ct))
            {
                return message;
            }
            throw new InvalidOperationException("no message");
        }

        [Fact]
        public async Task Fragment_Yields_EndOfMessage_Frames()
        {
            var session = await Connect("/ws/fragment");
            var messages = new List<WebSocketMessage>();
            await foreach (var message in session.ReceiveAsync(CancellationToken.None))
            {
                messages.Add(message);
                if (messages.Count == 2)
                {
                    break;
                }
            }
            await session.DisposeAsync();

            Assert.False(messages[0].EndOfMessage);
            Assert.Equal("hel", Encoding.UTF8.GetString(messages[0].Payload.ToArray()));
            Assert.True(messages[1].EndOfMessage);
            Assert.Equal("lo", Encoding.UTF8.GetString(messages[1].Payload.ToArray()));
        }

        [Fact]
        public async Task Client_Close_Handshake_Closes_State()
        {
            var session = await Connect("/ws/echo");
            await session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hello"), true),
                CancellationToken.None);
            await ReceiveOne(session);
            await session.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                "bye",
                CancellationToken.None);

            Assert.Equal(System.Net.WebSockets.WebSocketState.Closed, session.State);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Abort_Makes_Send_And_Receive_Throw_Aborted()
        {
            var session = await Connect("/ws/echo");
            session.Abort();

            var sendError = await Assert.ThrowsAsync<TransportException>(() => session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("x"), true),
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Aborted, sendError.Kind);

            var receiveError = await Assert.ThrowsAsync<TransportException>(() =>
                ReceiveOne(session));
            Assert.Equal(TransportErrorKind.Aborted, receiveError.Kind);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task ConnectTimeout_Is_Retryable_Connect()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                new WebSocketRequest(_fixture.Server.HttpBase + "/ws/slow", new TransportHeaders()),
                new WebSocketOptions { ConnectTimeout = TimeSpan.FromMilliseconds(100) },
                CancellationToken.None));

            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public async Task IdleTimeout_Is_Idle_Timeout()
        {
            var session = await Connect("/ws/idle", new WebSocketOptions
            {
                IdleTimeout = TimeSpan.FromMilliseconds(100)
            });
            var error = await Assert.ThrowsAsync<TransportException>(() => ReceiveOne(session));
            Assert.Equal(TransportErrorKind.IdleTimeout, error.Kind);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task MaxMessageBytes_Overflow_Aborts()
        {
            var session = await Connect("/ws/fragment", new WebSocketOptions
            {
                MaxMessageBytes = 3
            });
            var received = new List<WebSocketMessage>();
            var error = await Assert.ThrowsAsync<TransportException>(async () =>
            {
                await foreach (var message in session.ReceiveAsync(CancellationToken.None))
                {
                    received.Add(message);
                }
            });
            await session.DisposeAsync();

            Assert.Single(received);
            Assert.Equal(TransportErrorKind.Overflow, error.Kind);
        }

        [Fact]
        public async Task Caller_Cancel_Receive_Is_Cancelled()
        {
            var session = await Connect("/ws/idle");
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(100);

            var error = await Assert.ThrowsAsync<TransportException>(() =>
                ReceiveOne(session, cts.Token));
            Assert.True(
                error.Kind == TransportErrorKind.Cancelled ||
                error.Kind == TransportErrorKind.Aborted);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Server_Drop_Is_Protocol()
        {
            var session = await Connect("/ws/drop");
            var error = await Assert.ThrowsAsync<TransportException>(() => ReceiveOne(session));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Send_Close_Type_Is_Protocol()
        {
            var session = await Connect("/ws/echo");
            var error = await Assert.ThrowsAsync<TransportException>(() => session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Close, ReadOnlyMemory<byte>.Empty, true),
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Lifetime_Cancel_Aborts_Session()
        {
            using var lifetime = new CancellationTokenSource();
            var session = await Connect("/ws/echo", null, lifetime.Token);
            lifetime.Cancel();

            var error = await Assert.ThrowsAsync<TransportException>(() => session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("x"), true),
                CancellationToken.None));
            Assert.True(
                error.Kind == TransportErrorKind.Cancelled ||
                error.Kind == TransportErrorKind.Aborted);
            await session.DisposeAsync();
        }
    }
}
```

- [ ] **Step 2: 写 TLS/选项/能力/Selector 测试**

`tests/UnityLlmTransport.WebSocket.Tests/WebSocketTlsAndOptionsTests.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("ws-server")]
    public class WebSocketTlsAndOptionsTests
    {
        private readonly WsServerFixture _fixture;

        public WebSocketTlsAndOptionsTests(WsServerFixture fixture)
        {
            _fixture = fixture;
        }

        private WebSocketRequest Request(string basePath = "/ws/echo", string? baseUrl = null)
        {
            return new WebSocketRequest((baseUrl ?? _fixture.Server.HttpBase) + basePath, new TransportHeaders());
        }

        [Fact]
        public async Task Invalid_Scheme_Is_Protocol()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                new WebSocketRequest("ftp://127.0.0.1/x", new TransportHeaders()),
                new WebSocketOptions(),
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public async Task Explicit_Proxy_Is_Unsupported()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request(),
                new WebSocketOptions
                {
                    Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" }
                },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public async Task Proxy_None_Is_Unsupported()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request(),
                new WebSocketOptions { Proxy = new ProxyOptions { Mode = ProxyMode.None } },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public async Task Redirect_Follow_Is_Unsupported()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request(),
                new WebSocketOptions { Redirects = RedirectPolicy.Follow },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public async Task Custom_Tls_Is_Unsupported()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request(),
                new WebSocketOptions
                {
                    Tls = new TlsOptions { CustomRoots = Array.Empty<X509Certificate2>() }
                },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public async Task SelfSigned_Default_Is_Tls()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                Request("/ws/echo", _fixture.Server.HttpsBase),
                new WebSocketOptions(),
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
        }

        [Fact]
        public async Task SelfSigned_AcceptAll_Connects()
        {
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request("/ws/echo", _fixture.Server.HttpsBase),
                new WebSocketOptions { Tls = new TlsOptions { ValidateCertificate = false } },
                CancellationToken.None);
            await session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, System.Text.Encoding.UTF8.GetBytes("tls"), true),
                CancellationToken.None);

            await foreach (var message in session.ReceiveAsync(CancellationToken.None))
            {
                Assert.Equal("echo:tls", System.Text.Encoding.UTF8.GetString(message.Payload.ToArray()));
                break;
            }
            await session.DisposeAsync();
        }

        [Fact]
        public void Capabilities_Are_Exact()
        {
            var transport = new ClientWebSocketTransport();
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.WebSocket));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.WebSocketOverTls));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.SystemProxy));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.TlsCertificateHandler));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.StreamingBody));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.ExplicitProxy));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.Http2));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.RequestFinalizer));
        }

        [Fact]
        public void Selector_Selects_WebSocket_And_Rejects_Cached()
        {
            var transport = new ClientWebSocketTransport();
            var selector = new DefaultTransportSelector(
                new Dictionary<TransportKind, IStreamingHttpTransport>(),
                new Dictionary<TransportKind, IWebSocketTransport>
                {
                    [TransportKind.WebSocket] = transport
                });

            var selection = selector.Select(
                new TransportSelectionContext("chat", "test", "http://localhost", TransportKind.WebSocket));
            Assert.Same(transport, selection.WebSocket);

            Assert.Throws<TransportException>(() => selector.Select(
                new TransportSelectionContext("chat", "test", "http://localhost", TransportKind.WebSocketCached)));
        }
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo`
Expected: 新测试编译失败或断言失败（会话/选项行为尚未验证）。

- [ ] **Step 4: 仅当测试暴露真实缺陷才改产品代码**

预期无需改产品代码；若失败先判断是服务端时序/测试假设还是实现缺陷：
- 分帧/Close/Abort/超时/上限失败 → 对照 §5.2–5.5 的最小行为修产品代码，记录 deviation；
- 不得放宽断言（如把 `Tls` 改 `Connect`、把 `Overflow` 改成 `0`）。

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 34`。

- [ ] **Step 6: 提交**

```bash
git add tests/UnityLlmTransport.WebSocket.Tests/ClientWebSocketSessionTests.cs tests/UnityLlmTransport.WebSocket.Tests/WebSocketTlsAndOptionsTests.cs
git commit -m "test(ws): 会话边界/超时/TLS/能力矩阵"
```

### Task 4: Phase5 Unity harness + 2022.3 Mono 门禁

**Files:**
- Create: `scripts/assemble-phase5-unity.sh`
- Create: `unity/Phase5Harness/Assets/Editor/Phase5Build.cs`
- Create: `unity/Phase5Harness/Assets/Scripts/Phase5Runner.cs`
- Create: `unity/Phase5Harness/README.md`

**Interfaces:**
- Consumes: Task 1–3 的 `ClientWebSocketTransport`；现有 `spikes/WsTestServer`（ws://127.0.0.1:5200/ws/）。
- Produces: 四版本可复用的 H1–W6 harness；2022.3 Mono 首轮门禁证据。

- [ ] **Step 1: 写装配脚本**

`scripts/assemble-phase5-unity.sh`：

```bash
#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
project="${1:?usage: assemble-phase5-unity.sh <unity-project-path>}"
assets="$project/Assets"
dest="$assets/UnityLlmTransport"

rm -rf "$dest"
mkdir -p "$dest/Abstractions" "$dest/WebSocket"
mkdir -p "$assets/Scripts" "$assets/Editor"

copy_cs() {
  local src="$1" dst="$2"
  find "$src" -name '*.cs' -not -path '*/obj/*' -not -path '*/bin/*' -print0 |
  while IFS= read -r -d '' file; do
    local rel="${file#"$src"/}"
    mkdir -p "$dst/$(dirname "$rel")"
    cp "$file" "$dst/$rel"
  done
}

copy_cs "$repo/src/UnityLlmTransport.Abstractions" "$dest/Abstractions"
copy_cs "$repo/src/UnityLlmTransport.WebSocket" "$dest/WebSocket"
rm -f "$dest/WebSocket/Internal/IsExternalInit.cs"

cp "$repo/unity/Phase5Harness/Assets/Scripts/Phase5Runner.cs" "$assets/Scripts/"
cp "$repo/unity/Phase5Harness/Assets/Editor/Phase5Build.cs" "$assets/Editor/"

echo "assembled Phase 5 assets -> $project"
```

```bash
chmod +x scripts/assemble-phase5-unity.sh
```

- [ ] **Step 2: 写 build/runner**

`unity/Phase5Harness/Assets/Editor/Phase5Build.cs`：

```csharp
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class Phase5Build
{
    public static void BuildMono()
    {
        Build("mono", ScriptingImplementation.Mono2x);
    }

    public static void BuildIl2cpp()
    {
        Build("il2cpp", ScriptingImplementation.IL2CPP);
    }

    private static void Build(string name, ScriptingImplementation backend)
    {
        var sceneDir = "Assets/Scenes";
        Directory.CreateDirectory(sceneDir);
        var scenePath = sceneDir + "/Phase5.unity";

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var runner = new GameObject("Phase5Runner");
        runner.AddComponent<Phase5Runner>();
        EditorSceneManager.SaveScene(scene, scenePath);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, backend);

        var outputDir = Path.Combine(@"D:\UnitySpikes\Builds\Phase5", Application.unityVersion, name);
        Directory.CreateDirectory(outputDir);
        var outputExe = Path.Combine(outputDir, "Phase5.exe");

        var report = BuildPipeline.BuildPlayer(
            new[] { scenePath },
            outputExe,
            BuildTarget.StandaloneWindows64,
            BuildOptions.Development);

        Debug.Log("PHASE5_BUILD " + name + " " + report.summary.result +
                  " errors=" + report.summary.totalErrors + " output=" + outputExe);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
```

`unity/Phase5Harness/Assets/Scripts/Phase5Runner.cs`：

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using UnityEngine;

public sealed class Phase5Runner : MonoBehaviour
{
    private const string LocalWs = "ws://127.0.0.1:5200/ws/";
    private const string RemoteWs = "wss://ws.postman-echo.com/raw";

    private readonly StringBuilder _results = new StringBuilder();
    private ClientWebSocketTransport _transport;

    private IEnumerator Start()
    {
        _transport = new ClientWebSocketTransport();
        _results.AppendLine("unity_version=" + Application.unityVersion);
        _results.AppendLine("main_thread=" + Thread.CurrentThread.ManagedThreadId);

        yield return Run("W1_text", ScenarioText);
        yield return Run("W2_utf8", ScenarioUtf8);
        yield return Run("W3_close", ScenarioClose);
        yield return Run("W4_abort", ScenarioAbort);
        yield return Run("W5_remote_wss", ScenarioRemoteWss);
        yield return Run("W6_error_port", ScenarioErrorPort);

        var path = Path.Combine(Application.persistentDataPath, "phase5-results.txt");
        File.WriteAllText(path, _results.ToString());
        UnityEngine.Debug.Log("PHASE5_RESULTS_PATH=" + path);
        foreach (var line in _results.ToString().Split('\n'))
        {
            UnityEngine.Debug.Log("PHASE5 " + line);
        }
        yield return new WaitForSeconds(1f);
        Application.Quit();
    }

    private IEnumerator Run(string name, Func<Task<string>> scenario)
    {
        Task<string> task = scenario();
        while (!task.IsCompleted)
        {
            yield return null;
        }
        if (task.IsFaulted)
        {
            Exception error = task.Exception?.InnerException ?? task.Exception;
            _results.AppendLine(name + "|FAIL|" + Describe(error));
        }
        else if (task.IsCanceled)
        {
            _results.AppendLine(name + "|FAIL|cancelled");
        }
        else
        {
            _results.AppendLine(name + "|OK|" + task.Result);
        }
    }

    private static string Describe(Exception error)
    {
        if (error == null)
        {
            return "none";
        }
        if (error is TransportException transport)
        {
            return "TransportException kind=" + transport.Kind +
                   " msg=" + Trunc(transport.Message, 80);
        }
        return error.GetType().Name + ": " + Trunc(error.Message, 80);
    }

    private async Task<string> ScenarioText()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = await _transport.ConnectAsync(Req(LocalWs), new WebSocketOptions(), cts.Token);
        await session.SendAsync(
            new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hello"), true),
            cts.Token);
        var message = await ReceiveOne(session, cts.Token);
        await session.DisposeAsync();
        return "type=" + message.Type +
               "|payload=" + Encoding.UTF8.GetString(message.Payload.ToArray());
    }

    private async Task<string> ScenarioUtf8()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = await _transport.ConnectAsync(Req(LocalWs), new WebSocketOptions(), cts.Token);
        await session.SendAsync(
            new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("你好，世界"), true),
            cts.Token);
        var message = await ReceiveOne(session, cts.Token);
        await session.DisposeAsync();
        return "payload=" + Encoding.UTF8.GetString(message.Payload.ToArray());
    }

    private async Task<string> ScenarioClose()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = await _transport.ConnectAsync(Req(LocalWs), new WebSocketOptions(), cts.Token);
        await session.SendAsync(
            new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("bye"), true),
            cts.Token);
        await ReceiveOne(session, cts.Token);
        var close = await ReceiveOne(session, cts.Token);
        var status = close.CloseStatus.HasValue ? close.CloseStatus.Value.ToString() : "none";
        await session.DisposeAsync();
        return "type=" + close.Type + "|close=" + status;
    }

    private async Task<string> ScenarioAbort()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var session = await _transport.ConnectAsync(Req(LocalWs), new WebSocketOptions(), cts.Token);
        session.Abort();
        string sendKind;
        try
        {
            await session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("x"), true),
                CancellationToken.None);
            sendKind = "FAIL_no_error";
        }
        catch (TransportException ex)
        {
            sendKind = ex.Kind.ToString();
        }
        await session.DisposeAsync();
        return "send_kind=" + sendKind;
    }

    private async Task<string> ScenarioRemoteWss()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var session = await _transport.ConnectAsync(Req(RemoteWs), new WebSocketOptions(), cts.Token);
        await session.SendAsync(
            new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hello"), true),
            cts.Token);
        var message = await ReceiveOne(session, cts.Token);
        await session.DisposeAsync();
        return "type=" + message.Type +
               "|payload=" + Encoding.UTF8.GetString(message.Payload.ToArray());
    }

    private async Task<string> ScenarioErrorPort()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await _transport.ConnectAsync(Req("ws://127.0.0.1:5299/ws/"), new WebSocketOptions(), cts.Token);
            return "FAIL_no_error";
        }
        catch (TransportException ex)
        {
            return "kind=" + ex.Kind;
        }
    }

    private static WebSocketRequest Req(string url)
    {
        return new WebSocketRequest(url, new TransportHeaders());
    }

    private static async Task<WebSocketMessage> ReceiveOne(IWebSocketSession session, CancellationToken ct)
    {
        await foreach (var message in session.ReceiveAsync(ct))
        {
            return message;
        }
        throw new InvalidOperationException("no WebSocket message received");
    }

    private static string Trunc(string value, int max)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var singleLine = value.Replace('\n', ' ').Replace('\r', ' ');
        return singleLine.Length <= max ? singleLine : singleLine.Substring(0, max) + "...";
    }
}
```

`unity/Phase5Harness/README.md`：

```markdown
# Phase 5 Unity Harness

## 前置

```bash
# 终端 A：本地 ws echo（5200）
dotnet run -c Release --project spikes/WsTestServer
```

W5 需要外网访问 `wss://ws.postman-echo.com/raw`（系统代理/系统 CA）。

## 装配/构建/运行

```bash
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -createProject D:/UnitySpikes/Phase5-<ver> -batchmode -quit -logFile D:/UnitySpikes/create-phase5-<ver>.log
bash scripts/assemble-phase5-unity.sh D:/UnitySpikes/Phase5-<ver>
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -batchmode -quit -projectPath D:/UnitySpikes/Phase5-<ver> -executeMethod Phase5Build.BuildMono -logFile D:/UnitySpikes/phase5-<ver>-mono.log
D:/UnitySpikes/Builds/Phase5/<unityVersion>/mono/Phase5.exe -logFile D:/UnitySpikes/phase5-<ver>-mono-player.log
```

判据：`PHASE5 W1_text|OK|...` 至 `W6_error_port|OK|...`；`W6 kind` 为 `Connect` 或 `Handshake`；结果文件 `%LOCALAPPDATA%Low\DefaultCompany\<工程名>\phase5-results.txt`。
服务端 PID 级清理；禁止 mass-kill dotnet。
```

- [ ] **Step 3: 2022.3 工程 + Mono 首轮门禁**

```bash
mkdir -p D:/UnitySpikes/Phase5-2022
D:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe -createProject D:/UnitySpikes/Phase5-2022 -batchmode -quit -logFile D:/UnitySpikes/create-phase5-2022.log
bash scripts/assemble-phase5-unity.sh D:/UnitySpikes/Phase5-2022
D:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe -batchmode -quit -projectPath D:/UnitySpikes/Phase5-2022 -executeMethod Phase5Build.BuildMono -logFile D:/UnitySpikes/phase5-2022-mono.log
( tail -f /dev/null | dotnet run -c Release --project spikes/WsTestServer > /tmp/phase5-ws-server.log 2>&1 ) &
sleep 3
netstat -ano | grep LISTENING | grep ':5200'
D:/UnitySpikes/Builds/Phase5/2022.3.62f3c1/mono/Phase5.exe -logFile D:/UnitySpikes/phase5-2022-mono-player.log
grep "PHASE5 " D:/UnitySpikes/phase5-2022-mono-player.log
```

Expected 6 条 `|OK|`；W6 kind Connect/Handshake。清理：按 `CommandLine` 含 `WsTestServer` 的精确 PID 终止（PowerShell `Get-CimInstance` + `taskkill //PID <pid> //T //F`）；禁止 mass-kill。

- [ ] **Step 4: 提交**

```bash
git add scripts/assemble-phase5-unity.sh unity/Phase5Harness/Assets/Editor/Phase5Build.cs unity/Phase5Harness/Assets/Scripts/Phase5Runner.cs unity/Phase5Harness/README.md
git commit -m "test(phase5): Unity Mono harness W1-W6"
```

---

### Task 5: 四版本矩阵 + 结果/证据/文档收尾

**Files:**
- Create: `docs/spike/phase5-results.md`
- Create: `docs/spike/evidence/phase5/<ver>-mono-results.txt`（4 份）
- Create: `docs/spike/evidence/phase5/<ver>-il2cpp-results.txt`（4 份）
- Modify: `docs/05-版本与兼容基线.md`
- Modify: `docs/11-开发阶段划分.md`
- Modify: `README.md`
- Modify: `docs/SOP/Unity多版本通讯测试-SOP-2026-11-06.md`

**Interfaces:**
- Consumes: Task 4 harness + WsTestServer + 四版本编辑器。
- Produces: Phase 5 验收结果与文档。

- [ ] **Step 1: 建 2021/6.3/6.6 工程并装配**

```bash
for row in "2021.3.45f1|D:/UnitySpikes/Phase5-2021|D:/Unity/Hub/Editor/2021.3.45f1/Editor/Unity.exe" \
           "6000.3.28f1|D:/UnitySpikes/Phase5-6003|D:/Unity/Hub/Editor/6000.3.28f1/Editor/Unity.exe" \
           "6000.6.8f1|D:/UnitySpikes/Phase5-6006|D:/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe"; do
  ver="${row%%|*}"; rest="${row#*|}"; proj="${rest%%|*}"; editor="${rest#*|}"
  mkdir -p "$proj"
  "$editor" -createProject "$proj" -batchmode -quit -logFile "D:/UnitySpikes/create-phase5-$ver.log"
  bash scripts/assemble-phase5-unity.sh "$proj"
done
```

Expected: 三个 `assembled Phase 5 assets -> ...`。

- [ ] **Step 2: 四版本 Mono 构建 + 运行**

```bash
for row in "2022.3.62f3c1|D:/UnitySpikes/Phase5-2022|D:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe" \
           "2021.3.45f1|D:/UnitySpikes/Phase5-2021|D:/Unity/Hub/Editor/2021.3.45f1/Editor/Unity.exe" \
           "6000.3.28f1|D:/UnitySpikes/Phase5-6003|D:/Unity/Hub/Editor/6000.3.28f1/Editor/Unity.exe" \
           "6000.6.8f1|D:/UnitySpikes/Phase5-6006|D:/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe"; do
  ver="${row%%|*}"; rest="${row#*|}"; proj="${rest%%|*}"; editor="${rest#*|}"
  "$editor" -batchmode -quit -projectPath "$proj" -executeMethod Phase5Build.BuildMono -logFile "D:/UnitySpikes/phase5-$ver-mono.log"
  grep -n "PHASE5_BUILD mono Succeeded" "D:/UnitySpikes/phase5-$ver-mono.log" || (echo "MONO BUILD FAILED $ver"; exit 1)
done
```

启动 WsTestServer，逐版本运行：

```bash
( tail -f /dev/null | dotnet run -c Release --project spikes/WsTestServer > /tmp/phase5-ws-server-final.log 2>&1 ) &
sleep 3
for ver in 2022.3.62f3c1 2021.3.45f1c2 6000.3.28f1 6000.6.8f1; do
  D:/UnitySpikes/Builds/Phase5/$ver/mono/Phase5.exe -logFile D:/UnitySpikes/phase5-$ver-mono-player.log
  grep "PHASE5 " D:/UnitySpikes/phase5-$ver-mono-player.log > D:/UnitySpikes/phase5-$ver-mono-results.txt
done
```

Expected 每版本 6 条 OK（W1–W6）；2021 目录为 `2021.3.45f1c2`，按实际 `Application.unityVersion`。

- [ ] **Step 3: 四版本 IL2CPP 构建 + 运行**

```bash
for row in "2022.3.62f3c1|D:/UnitySpikes/Phase5-2022|D:/Unity/Hub/Editor/2022.3.62f3c1/Editor/Unity.exe" \
           "2021.3.45f1|D:/UnitySpikes/Phase5-2021|D:/Unity/Hub/Editor/2021.3.45f1/Editor/Unity.exe" \
           "6000.3.28f1|D:/UnitySpikes/Phase5-6003|D:/Unity/Hub/Editor/6000.3.28f1/Editor/Unity.exe" \
           "6000.6.8f1|D:/UnitySpikes/Phase5-6006|D:/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe"; do
  ver="${row%%|*}"; rest="${row#*|}"; proj="${rest%%|*}"; editor="${rest#*|}"
  "$editor" -batchmode -quit -projectPath "$proj" -executeMethod Phase5Build.BuildIl2cpp -logFile "D:/UnitySpikes/phase5-$ver-il2cpp.log"
  grep -n "PHASE5_BUILD il2cpp Succeeded" "D:/UnitySpikes/phase5-$ver-il2cpp.log" || (echo "IL2CPP BUILD FAILED $ver"; exit 1)
done

( tail -f /dev/null | dotnet run -c Release --project spikes/WsTestServer > /tmp/phase5-ws-server-il2cpp.log 2>&1 ) &
sleep 3
for ver in 2022.3.62f3c1 2021.3.45f1c2 6000.3.28f1 6000.6.8f1; do
  D:/UnitySpikes/Builds/Phase5/$ver/il2cpp/Phase5.exe -logFile D:/UnitySpikes/phase5-$ver-il2cpp-player.log
  grep "PHASE5 " D:/UnitySpikes/phase5-$ver-il2cpp-player.log > D:/UnitySpikes/phase5-$ver-il2cpp-results.txt
done
```

- [ ] **Step 3.1: 关闭 WsTestServer**

```bash
powershell -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { \$_.CommandLine -like '*WsTestServer*' } | Select-Object ProcessId" | grep -oE '[0-9]+' | while read pid; do taskkill //PID "$pid" //T //F 2>/dev/null || true; done
netstat -ano | grep LISTENING | grep ':5200' || echo '5200 free'
```

Expected: `5200 free`；不要 taskkill PID 4。

Expected IL2CPP W1/W3/W4 至少 OK（W2/W5/W6 若跑也记录；W5 远程依赖网络）。

- [ ] **Step 4: 写结果与证据**

`docs/spike/phase5-results.md` 结构：

```markdown
# Phase 5 · WebSocket 四版本实机结果

> 日期 + 环境 + 命令见 `unity/Phase5Harness/README.md`。
> 原始日志：`D:/UnitySpikes/phase5-<ver>-<backend>-player.log`；证据在 `docs/spike/evidence/phase5/`。

## Mono（W1–W6）

| 场景 | 2022.3.62f3c1 | 2021.3.45f1c2 | 6000.3.28f1 | 6000.6.8f1 | 判据 |
|---|---|---|---|---|---|
| W1 text | | | | | echo:hello |
| W2 utf8 | | | | | 中文回环 |
| W3 close | | | | | NormalClosure |
| W4 abort | | | | | Aborted |
| W5 remote wss | | | | | hello 回环 |
| W6 error | | | | | Connect/Handshake |

## IL2CPP

| 场景 | 2022.3 | 2021.3 | 6.3 | 6.6 |
|---|---|---|---|---|
| W1/W3/W4 | | | | |

## 结论

- 四版本 Mono 全过/失败项（照实填）；
- ClientWebSocket 在 Unity 的真实行为；
- 远程 wss 依赖外网/系统代理的记录；
- 已知遗留（WebGL/RawTcp/WS→SSE 回退/WebSocketCached 不在本阶段）。
```

逐格粘贴 `PHASE5` 原始行；将 4+4 份 results.txt 复制到 `docs/spike/evidence/phase5/`。空白=未通过，不得推断。

- [ ] **Step 5: 更新文档**

`docs/05` WebSocket 行改为：

```
WebSocket | ✅ 四版本 Mono + IL2CPP 本地 ws/Close/Abort + 远程 wss（2026-11-07，`docs/spike/phase5-results.md`）；WebGL 不支持（M2）；WS→SSE 回退属消费方
```

`docs/11` §7 改为：

```
## 7. 阶段 5 · WebSocket 核心（传输侧）
> 本阶段只做 WebSocket 传输适配器；WS→SSE 回退、自动重连、WebSocketCached 属消费方/后续；RawTcp/WebGL 后置。
| S5-1 | ClientWebSocketTransport/Session：连接、收发、Close/Abort |
| S5-2 | 超时/上限/取消/错误分类 |
| S5-3 | 四版本 Mono + IL2CPP 本地 ws/Close/Abort；远程 wss |
**门禁**：dotnet 测试全绿；四版本 Mono W1–W6 + IL2CPP W1/W3/W4；无消费方集成；WebGL 不声明。
**状态**：已完成（2026-11-07）...
```

`README` 状态：`Phase 0/1/2/3/4/5 已完成；Phase 6 待开工`；目录树补 `UnityLlmTransport.WebSocket`、`WebSocket.Tests`、`Phase5Harness`。

`docs/SOP` 追加：

```
## Phase 5 harness（WebSocket）
1. `dotnet run -c Release --project spikes/WsTestServer`（ws 5200）
2. `assemble-phase5-unity.sh`；`Phase5Build.BuildMono` / `BuildIl2cpp`
3. 判据 `PHASE5 <name>|OK|`；W5 需外网 wss；WebGL 不测
4. 服务 PID 级清理（apphost），禁止 mass-kill
```

- [ ] **Step 6: 阶段门禁**

```bash
dotnet build UnityLlmTransport.sln -c Release
dotnet test tests/UnityLlmTransport.WebSocket.Tests/UnityLlmTransport.WebSocket.Tests.csproj --nologo
grep -rn "UnityEngine" src/UnityLlmTransport.WebSocket --include='*.cs' || echo "OK: WS zero UnityEngine"
grep -rn "PiUnityAgent" src/UnityLlmTransport.WebSocket --include='*.cs' || echo "OK: WS no consumer references"
```

Expected: build 0 errors；`Passed: 34`；两个 OK。

- [ ] **Step 7: 提交**

```bash
git add docs/spike/phase5-results.md docs/spike/evidence/phase5 docs/05-版本与兼容基线.md docs/11-开发阶段划分.md README.md docs/SOP/Unity多版本通讯测试-SOP-2026-11-06.md
git commit -m "docs(phase5): 四版本 WebSocket 结果与收尾"
```

---

## 验收对照（Spec §9）

| Spec 验收项 | 落点 |
|---|---|
| 包 netstandard2.1 / 零 Unity / 零三方 | Task 1 + Task 5 |
| dotnet 34 项测试 | Task 1–3 |
| 四版本 Mono W1–W6 + IL2CPP W1/W3/W4 | Task 4–5 + results/evidence |
| Capabilities 与实测一致 | Task 3 `Capabilities_Are_Exact` |
| Selector 选中 WebSocket / Cached Unsupported | Task 3 |
| 无消费方回退/集成 | 全部任务；Task 5 grep |
| 文档更新 | Task 5 |
| 不改 Abstractions/Sse/UWR/HttpClient/Auth 形状 | 全部任务仅新增 WebSocket 包/测试/harness |

## 执行注意

- WsTestServer 用 `HttpListener`，http.sys 监听 netstat 显示 PID 4；**不可 taskkill PID 4**；用 PowerShell 找 `CommandLine` 含 `WsTestServer` 的 apphost PID 点杀。
- W5 远程 wss 依赖外网/系统代理；失败记录环境，不伪造。
- 四版本端口 5200 串行使用；一次只跑一个 player。



