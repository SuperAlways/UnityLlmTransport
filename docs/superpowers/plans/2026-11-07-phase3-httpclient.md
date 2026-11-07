# Phase 3 · HttpClient Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 实现 `UnityLlmTransport.HttpClient`（netstandard2.1、零 UnityEngine）：显式代理/关闭代理/代理认证、TLS accept-all/客户端证书、重定向、gzip、错误分类与会话生命周期；用 dotnet 进程内服务 + 三版本 Unity Mono harness 验证。

**Architecture:** 默认每请求按 `TransportOptions` 创建 `HttpClientHandler + HttpClient`（逐请求兑现代理/TLS/重定向/解压，代价是不保跨请求连接池）；可注入共享 `HttpClient`（此时 per-request 非默认项显式 `Unsupported`）。`OpenAsync` 在响应头返回，首字节/空闲/响应体上限交给 Phase 1 装饰器；`HttpResponseReadStream` 把读错误归一为 `TransportException`。

**Tech Stack:** C# 9、netstandard2.1（包）、net10.0 xUnit（测试）、`Microsoft.AspNetCore.App` FrameworkReference（测试专用 Kestrel）、Unity 三版本 Mono harness、`System.Net.Http`。

## Global Constraints

- 包工程：`netstandard2.1`、`<LangVersion>9.0</LangVersion>`、`<Nullable>enable</Nullable>`、`<ImplicitUsings>disable</ImplicitUsings>`，零 `UnityEngine`、零第三方 NuGet。
- C# 9 天花板：`{ }` 类体、block namespace；禁 file-scoped namespace / `required` / 集合表达式。
- 测试工程 `net10.0`，xunit 2.9.3 / Microsoft.NET.Test.Sdk 17.14.1 / xunit.runner.visualstudio 3.1.4，`<NuGetAudit>false</NuGetAudit>`。
- 测试支持工程 `tests/Phase3TestSupport` net10 + `FrameworkReference Microsoft.AspNetCore.App`（测试专用，不进产品）。
- `HttpClientTransport.Capabilities` 固定为：
  `StreamingBody | SystemProxy | ExplicitProxy | ProxyAuthentication | TlsCertificateHandler | Compression | RequestFinalizer`
  不声明 `WebSocket`/`Http2`。
- 注入 `HttpClient` 时，per-request `Proxy != System`、TLS 可选字段/accept-all、`Redirects != Follow`、`Decompress == false` → `TransportException(Unsupported)`。
- `BodyFactory` → `Unsupported`；`Content-Length` 由实际 body 决定。
- Unity 门禁只跑三版本 **Mono**；IL2CPP 不声明。
- 所有命令在仓库根执行，Git Bash，裸 `dotnet`。显式 `git add` 路径，禁 `git add -A`。
- Unity 编辑器：`D:\Unity\Hub\Editor\{2021.3.45f1,6000.3.28f1,6000.6.8f1}\Editor\Unity.exe`；外部工程 `D:\UnitySpikes\Phase3-<ver>`；构建产物 `D:\UnitySpikes\Builds\Phase3\<version>\mono\`。
- 服务端口：自动化测试 5211/5243/5244；Unity harness 5201/5233/5234（HTTP/HTTPS/显式代理）。

## 文件结构

```
src/UnityLlmTransport.HttpClient/
  UnityLlmTransport.HttpClient.csproj
  Internal/IsExternalInit.cs
  Internal/AssemblyInfo.cs                # InternalsVisibleTo 测试工程
  HttpClientTransportOptions.cs
  HttpClientTransport.cs
  HttpClientSession.cs                    # internal
  HttpResponseReadStream.cs              # internal
  HttpClientRequestBuilder.cs            # internal
  HttpClientRequestValidator.cs          # internal
  HttpClientHandlerFactory.cs            # internal
  HttpClientResponseHeaders.cs           # internal
  HttpClientErrorClassifier.cs           # internal
tests/Phase3TestSupport/
  Phase3TestSupport.csproj
  Phase3TestServer.cs                     # Kestrel HTTP/HTTPS 自签 + 裸 TCP 显式代理
tests/UnityLlmTransport.HttpClient.Tests/
  UnityLlmTransport.HttpClient.Tests.csproj
  ServerFixture.cs
  HttpClientValidatorTests.cs
  HttpClientErrorClassifierTests.cs
  HttpClientRequestBuilderTests.cs
  HttpClientHandlerFactoryTests.cs
  HttpClientTransportTests.cs
  HttpClientProxyTlsTests.cs
  HttpClientTimeoutTests.cs
  HttpClientContractTests.cs
spikes/Phase3TestServer/
  Phase3TestServer.csproj
  Program.cs
unity/Phase3Harness/
  README.md
  Assets/Scripts/Phase3Runner.cs
  Assets/Editor/Phase3Build.cs
scripts/assemble-phase3-unity.sh
```

---

### Task 1: 包骨架 + Options + Validator + ErrorClassifier

**Files:**
- Create: `src/UnityLlmTransport.HttpClient/UnityLlmTransport.HttpClient.csproj`
- Create: `src/UnityLlmTransport.HttpClient/Internal/IsExternalInit.cs`
- Create: `src/UnityLlmTransport.HttpClient/Internal/AssemblyInfo.cs`
- Create: `src/UnityLlmTransport.HttpClient/HttpClientTransportOptions.cs`
- Create: `src/UnityLlmTransport.HttpClient/HttpClientRequestValidator.cs`
- Create: `src/UnityLlmTransport.HttpClient/HttpClientErrorClassifier.cs`
- Create: `tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj`
- Test: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientValidatorTests.cs`
- Test: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientErrorClassifierTests.cs`
- Modify: `UnityLlmTransport.sln`

**Interfaces:**
- Consumes: `TransportOptions`/`ProxyOptions`/`TlsOptions`/`TransportException`/`TransportErrorKind`/`TransportCapabilities`（Abstractions）。
- Produces: `HttpClientTransportOptions { HttpClient? Client; TransportCapabilities? CapabilitiesOverride; }`；`HttpClientRequestValidator.Validate(TransportOptions, bool injectedClient)`；`HttpClientErrorClassifier.Classify(Exception, bool callerCancelled, bool connectTimeout) → TransportException`。

- [ ] **Step 1: 建包与测试工程**

`src/UnityLlmTransport.HttpClient/UnityLlmTransport.HttpClient.csproj`：

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

`src/UnityLlmTransport.HttpClient/Internal/IsExternalInit.cs`：

```csharp
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
```

`src/UnityLlmTransport.HttpClient/Internal/AssemblyInfo.cs`：

```csharp
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("UnityLlmTransport.HttpClient.Tests")]
```

`tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj`：

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
    <ProjectReference Include="..\..\src\UnityLlmTransport.Abstractions\UnityLlmTransport.Abstractions.csproj" />
    <ProjectReference Include="..\..\src\UnityLlmTransport.Sse\UnityLlmTransport.Sse.csproj" />
    <ProjectReference Include="..\..\src\UnityLlmTransport.HttpClient\UnityLlmTransport.HttpClient.csproj" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln UnityLlmTransport.sln add src/UnityLlmTransport.HttpClient/UnityLlmTransport.HttpClient.csproj
dotnet sln UnityLlmTransport.sln add tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj
```

Expected: 两行 `has been added to the solution.`。

- [ ] **Step 2: 写失败测试**

`tests/UnityLlmTransport.HttpClient.Tests/HttpClientValidatorTests.cs`：

```csharp
using System;
using System.Threading;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class HttpClientValidatorTests
    {
        private static TransportOptions Options()
        {
            return new TransportOptions();
        }

        [Fact]
        public void Injected_Client_Allows_Defaults()
        {
            HttpClientRequestValidator.Validate(Options(), true);
        }

        [Fact]
        public void Injected_Client_Rejects_Explicit_Proxy()
        {
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" }
            };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestValidator.Validate(options, true));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Injected_Client_Rejects_Tls_Override()
        {
            var options = new TransportOptions
            {
                Tls = new TlsOptions { ValidateCertificate = false }
            };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestValidator.Validate(options, true));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Injected_Client_Rejects_Redirect_Error()
        {
            var options = new TransportOptions { Redirects = RedirectPolicy.Error };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestValidator.Validate(options, true));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Injected_Client_Rejects_Decompress_False()
        {
            var options = new TransportOptions { Decompress = false };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestValidator.Validate(options, true));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Default_Transport_Allows_All_Options()
        {
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" },
                Tls = new TlsOptions { ValidateCertificate = false },
                Redirects = RedirectPolicy.Error,
                Decompress = false
            };

            HttpClientRequestValidator.Validate(options, false);
        }
    }
}
```

`tests/UnityLlmTransport.HttpClient.Tests/HttpClientErrorClassifierTests.cs`：

```csharp
using System;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class HttpClientErrorClassifierTests
    {
        [Fact]
        public void Caller_Cancelled_Is_Cancelled()
        {
            var error = HttpClientErrorClassifier.Classify(new OperationCanceledException(), true, false);
            Assert.Equal(TransportErrorKind.Cancelled, error.Kind);
        }

        [Fact]
        public void Connect_Timeout_Is_Retryable_Connect()
        {
            var error = HttpClientErrorClassifier.Classify(new OperationCanceledException(), false, true);
            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public void Authentication_Exception_Is_Tls()
        {
            var error = HttpClientErrorClassifier.Classify(new AuthenticationException("bad cert"), false, false);
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void Proxy_Text_Is_Proxy()
        {
            var inner = new SocketException((int)SocketError.ConnectionRefused);
            var error = HttpClientErrorClassifier.Classify(
                new HttpRequestException("CONNECT tunnel failed, response 407", inner), false, false);
            Assert.Equal(TransportErrorKind.Proxy, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void Plain_HttpRequestException_Is_Retryable_Connect()
        {
            var inner = new SocketException((int)SocketError.HostNotFound);
            var error = HttpClientErrorClassifier.Classify(
                new HttpRequestException("Name or service not known", inner), false, false);
            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public void Format_Exception_Is_Protocol()
        {
            var error = HttpClientErrorClassifier.Classify(new UriFormatException("bad uri"), false, false);
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
            Assert.False(error.IsRetryable);
        }
    }
}
```

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo`
Expected: 编译失败 `error CS0246`（找不到 `HttpClientTransportOptions` / `HttpClientRequestValidator` / `HttpClientErrorClassifier`）。

- [ ] **Step 4: 实现四个文件**

`src/UnityLlmTransport.HttpClient/HttpClientTransportOptions.cs`：

```csharp
using System.Net.Http;

namespace UnityLlmTransport
{
    public sealed record HttpClientTransportOptions
    {
        /// <summary>注入共享 HttpClient（复用连接池）。非 null 时 per-request Proxy/Tls/Redirects/Decompress 只允许默认值。</summary>
        public HttpClient? Client { get; init; }

        /// <summary>非 null 时覆盖传输默认能力声明（注入自定义 client 的高级用法）。</summary>
        public TransportCapabilities? CapabilitiesOverride { get; init; }
    }
}
```

`src/UnityLlmTransport.HttpClient/HttpClientRequestValidator.cs`：

```csharp
using System;

namespace UnityLlmTransport
{
    internal static class HttpClientRequestValidator
    {
        public static void Validate(TransportOptions options, bool injectedClient)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            if (!injectedClient)
            {
                return;
            }

            if (options.Proxy != null && options.Proxy.Mode != ProxyMode.System)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Injected HttpClient has a fixed proxy; configure the client instead of TransportOptions.Proxy.");
            }

            if (options.Tls != null &&
                (!options.Tls.ValidateCertificate ||
                 options.Tls.ClientCertificate != null ||
                 options.Tls.CustomRoots != null ||
                 options.Tls.Protocols != null))
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Injected HttpClient has fixed TLS settings; configure the client handler instead.");
            }

            if (options.Redirects != RedirectPolicy.Follow)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Injected HttpClient has fixed redirect settings; configure the client handler instead.");
            }

            if (!options.Decompress)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Injected HttpClient has fixed decompression settings; configure the client handler instead.");
            }
        }
    }
}
```

`src/UnityLlmTransport.HttpClient/HttpClientErrorClassifier.cs`：

```csharp
using System;
using System.Net.Http;
using System.Security.Authentication;

namespace UnityLlmTransport
{
    internal static class HttpClientErrorClassifier
    {
        public static TransportException Classify(Exception exception, bool callerCancelled, bool connectTimeout)
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
                        "Request cancelled.",
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
                        "Connect/headers timeout.",
                        exception,
                        null,
                        null,
                        null,
                        true);
                }
                return new TransportException(
                    TransportErrorKind.Connect,
                    "Request canceled before response headers.",
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
                    "TLS handshake failed: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            var requestException = exception as HttpRequestException;
            if (requestException != null)
            {
                var inner = requestException.InnerException;
                if (inner is AuthenticationException)
                {
                    return new TransportException(
                        TransportErrorKind.Tls,
                        "TLS handshake failed: " + inner.Message,
                        exception,
                        null,
                        null,
                        null,
                        false);
                }

                string text = requestException.Message + " " + (inner != null ? inner.Message : string.Empty);
                if (Contains(text, "proxy") || Contains(text, "tunnel") || Contains(text, "407"))
                {
                    return new TransportException(
                        TransportErrorKind.Proxy,
                        "Proxy failure: " + text,
                        exception,
                        null,
                        null,
                        null,
                        false);
                }

                return new TransportException(
                    TransportErrorKind.Connect,
                    "Connection failed: " + text,
                    exception,
                    null,
                    null,
                    null,
                    true);
            }

            if (exception is UriFormatException ||
                exception is ArgumentException ||
                exception is FormatException)
            {
                return new TransportException(
                    TransportErrorKind.Protocol,
                    "Invalid request: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            return new TransportException(
                TransportErrorKind.Protocol,
                "HTTP transport failed: " + exception.Message,
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

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 12`。

- [ ] **Step 6: 提交**

```bash
git add src/UnityLlmTransport.HttpClient/UnityLlmTransport.HttpClient.csproj src/UnityLlmTransport.HttpClient/Internal/IsExternalInit.cs src/UnityLlmTransport.HttpClient/Internal/AssemblyInfo.cs src/UnityLlmTransport.HttpClient/HttpClientTransportOptions.cs src/UnityLlmTransport.HttpClient/HttpClientRequestValidator.cs src/UnityLlmTransport.HttpClient/HttpClientErrorClassifier.cs tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj tests/UnityLlmTransport.HttpClient.Tests/HttpClientValidatorTests.cs tests/UnityLlmTransport.HttpClient.Tests/HttpClientErrorClassifierTests.cs UnityLlmTransport.sln
git commit -m "feat(httpclient): 包骨架/选项校验/错误分类"
```

---

### Task 2: HttpClientRequestBuilder

**Files:**
- Create: `src/UnityLlmTransport.HttpClient/HttpClientRequestBuilder.cs`
- Test: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientRequestBuilderTests.cs`

**Interfaces:**
- Consumes: `TransportRequest`/`TransportHeaders`/`TransportException`。
- Produces: `HttpClientRequestBuilder.Build(TransportRequest) → HttpRequestMessage`；`BodyFactory` → `Unsupported`；URI/method 非法 → `Protocol`；内容头路由；`Content-Length` 由 body 决定。

- [ ] **Step 1: 写失败测试**

`tests/UnityLlmTransport.HttpClient.Tests/HttpClientRequestBuilderTests.cs`：

```csharp
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class HttpClientRequestBuilderTests
    {
        private static TransportRequest Request(string method = "POST", string url = "http://localhost/test", TransportHeaders? headers = null)
        {
            return new TransportRequest(
                method,
                url,
                headers ?? new TransportHeaders(),
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{}")));
        }

        [Fact]
        public void Builds_Method_Uri_And_Body()
        {
            using var message = HttpClientRequestBuilder.Build(Request("POST", "http://localhost/test"));

            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(new Uri("http://localhost/test"), message.RequestUri);
            Assert.NotNull(message.Content);
            Assert.Equal(2, message.Content.Headers.ContentLength);
        }

        [Fact]
        public void BodyFactory_Is_Unsupported()
        {
            var request = Request() with
            {
                BodyFactory = ct => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1 }))
            };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestBuilder.Build(request));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Invalid_Uri_Is_Protocol()
        {
            var request = Request(url: "not a url");
            var error = Assert.Throws<TransportException>(() => HttpClientRequestBuilder.Build(request));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public void Invalid_Method_Is_Protocol()
        {
            var request = Request(method: "BAD METHOD");
            var error = Assert.Throws<TransportException>(() => HttpClientRequestBuilder.Build(request));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public void Null_Header_Is_Skipped()
        {
            var headers = new TransportHeaders
            {
                ["X-Skip"] = null,
                ["X-Test"] = "1"
            };

            using var message = HttpClientRequestBuilder.Build(Request(headers: headers));

            Assert.False(message.Headers.Contains("X-Skip"));
            Assert.Equal("1", message.Headers.GetValues("X-Test").Single());
        }

        [Fact]
        public void Content_Header_Is_Routed_To_Content()
        {
            var headers = new TransportHeaders { ["Content-Type"] = "application/json" };
            using var message = HttpClientRequestBuilder.Build(Request(headers: headers));

            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            Assert.False(message.Headers.Contains("Content-Type"));
        }

        [Fact]
        public void Caller_Content_Length_Is_Ignored()
        {
            var headers = new TransportHeaders { ["Content-Length"] = "999" };
            using var message = HttpClientRequestBuilder.Build(Request(headers: headers));

            Assert.Equal(2, message.Content!.Headers.ContentLength);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo`
Expected: `error CS0246`（找不到 `HttpClientRequestBuilder`）。

- [ ] **Step 3: 实现 RequestBuilder**

`src/UnityLlmTransport.HttpClient/HttpClientRequestBuilder.cs`：

```csharp
using System;
using System.Net.Http;

namespace UnityLlmTransport
{
    internal static class HttpClientRequestBuilder
    {
        public static HttpRequestMessage Build(TransportRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (request.BodyFactory != null)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "HttpClient transport does not support BodyFactory (streaming request body).");
            }

            HttpMethod method;
            try
            {
                method = new HttpMethod(request.Method);
            }
            catch (Exception ex)
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Invalid HTTP method: " + request.Method,
                    ex,
                    null,
                    null,
                    null,
                    false);
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
                    "Invalid request URI: " + request.Url,
                    ex,
                    null,
                    null,
                    null,
                    false);
            }

            var message = new HttpRequestMessage(method, uri);
            if (request.Body.Length > 0)
            {
                message.Content = new ByteArrayContent(request.Body.ToArray());
            }

            foreach (var pair in request.Headers)
            {
                if (pair.Value == null)
                {
                    continue;
                }
                if (pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 由实际 body 决定
                }
                if (IsContentHeader(pair.Key))
                {
                    if (message.Content != null)
                    {
                        message.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    }
                    continue;
                }
                message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }

            return message;
        }

        private static bool IsContentHeader(string name)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Language", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Location", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Range", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Expires", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 19`。

- [ ] **Step 5: 提交**

```bash
git add src/UnityLlmTransport.HttpClient/HttpClientRequestBuilder.cs tests/UnityLlmTransport.HttpClient.Tests/HttpClientRequestBuilderTests.cs
git commit -m "feat(httpclient): 请求构建规则"
```

### Task 3: HttpClientHandlerFactory（代理/TLS/重定向/解压映射）

**Files:**
- Create: `src/UnityLlmTransport.HttpClient/HttpClientHandlerFactory.cs`
- Test: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientHandlerFactoryTests.cs`

**Interfaces:**
- Consumes: `TransportOptions`/`ProxyOptions`/`TlsOptions`/`TransportException`。
- Produces: `HttpClientHandlerFactory.Create(TransportOptions) → HttpClientHandler`；Proxy System/None/Explicit；TLS accept-all/client cert；Redirect AllowAutoRedirect；Decompression GZip|Deflate；`CustomRoots`/`Protocols` → `Unsupported`。

- [ ] **Step 1: 写失败测试**

`tests/UnityLlmTransport.HttpClient.Tests/HttpClientHandlerFactoryTests.cs`：

```csharp
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class HttpClientHandlerFactoryTests
    {
        private static TransportOptions Options()
        {
            return new TransportOptions();
        }

        [Fact]
        public void Default_Is_System_Proxy_Follow_Decompress()
        {
            using var handler = HttpClientHandlerFactory.Create(Options());

            Assert.True(handler.UseProxy);
            Assert.Null(handler.Proxy);
            Assert.True(handler.AllowAutoRedirect);
            Assert.Equal(
                DecompressionMethods.GZip | DecompressionMethods.Deflate,
                handler.AutomaticDecompression);
        }

        [Fact]
        public void Proxy_None_Disables_System_Proxy()
        {
            var options = new TransportOptions { Proxy = new ProxyOptions { Mode = ProxyMode.None } };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.False(handler.UseProxy);
        }

        [Fact]
        public void Proxy_Explicit_Sets_WebProxy()
        {
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions
                {
                    Mode = ProxyMode.Explicit,
                    Url = "http://127.0.0.1:7897",
                    Credentials = new NetworkCredential("user", "pass")
                }
            };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.True(handler.UseProxy);
            var proxy = Assert.IsType<WebProxy>(handler.Proxy);
            Assert.Equal(new Uri("http://127.0.0.1:7897"), proxy.Address);
            Assert.NotNull(proxy.Credentials);
        }

        [Fact]
        public void Redirect_Error_Disables_Auto_Redirect()
        {
            var options = new TransportOptions { Redirects = RedirectPolicy.Error };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.False(handler.AllowAutoRedirect);
        }

        [Fact]
        public void Decompress_False_Disables_Automatic_Decompression()
        {
            var options = new TransportOptions { Decompress = false };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.Equal(DecompressionMethods.None, handler.AutomaticDecompression);
        }

        [Fact]
        public void Accept_All_Installs_Validation_Callback()
        {
            var options = new TransportOptions { Tls = new TlsOptions { ValidateCertificate = false } };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.NotNull(handler.ServerCertificateCustomValidationCallback);
        }

        [Fact]
        public void Client_Certificate_Is_Added()
        {
            using var rsa = RSA.Create(2048);
            var certificateRequest = new CertificateRequest(
                "CN=phase3-test-client",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            using var certificate = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddMinutes(5));

            var options = new TransportOptions
            {
                Tls = new TlsOptions { ClientCertificate = certificate }
            };
            using var handler = HttpClientHandlerFactory.Create(options);

            Assert.Single(handler.ClientCertificates.Cast<X509Certificate2>());
        }

        [Fact]
        public void Custom_Roots_Are_Unsupported()
        {
            var options = new TransportOptions
            {
                Tls = new TlsOptions { CustomRoots = Array.Empty<X509Certificate2>() }
            };

            var error = Assert.Throws<TransportException>(() => HttpClientHandlerFactory.Create(options));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Protocols_Are_Unsupported()
        {
            var options = new TransportOptions
            {
                Tls = new TlsOptions { Protocols = SslProtocols.Tls12 }
            };

            var error = Assert.Throws<TransportException>(() => HttpClientHandlerFactory.Create(options));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo`
Expected: `error CS0246`（找不到 `HttpClientHandlerFactory`）。

- [ ] **Step 3: 实现 HandlerFactory**

`src/UnityLlmTransport.HttpClient/HttpClientHandlerFactory.cs`：

```csharp
using System;
using System.Net;
using System.Net.Http;

namespace UnityLlmTransport
{
    internal static class HttpClientHandlerFactory
    {
        public static HttpClientHandler Create(TransportOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var handler = new HttpClientHandler();
            ConfigureProxy(handler, options.Proxy);
            ConfigureTls(handler, options.Tls);

            handler.AllowAutoRedirect = options.Redirects == RedirectPolicy.Follow;
            handler.AutomaticDecompression = options.Decompress
                ? (DecompressionMethods.GZip | DecompressionMethods.Deflate)
                : DecompressionMethods.None;
            return handler;
        }

        private static void ConfigureProxy(HttpClientHandler handler, ProxyOptions? proxy)
        {
            if (proxy == null || proxy.Mode == ProxyMode.System)
            {
                handler.UseProxy = true;
                handler.Proxy = null;
                return;
            }
            if (proxy.Mode == ProxyMode.None)
            {
                handler.UseProxy = false;
                return;
            }

            if (string.IsNullOrEmpty(proxy.Url))
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "ProxyOptions.Url is required for ProxyMode.Explicit.");
            }

            var webProxy = new WebProxy(proxy.Url);
            if (proxy.Credentials != null)
            {
                webProxy.Credentials = proxy.Credentials;
            }
            handler.UseProxy = true;
            handler.Proxy = webProxy;
        }

        private static void ConfigureTls(HttpClientHandler handler, TlsOptions? tls)
        {
            if (tls == null)
            {
                return;
            }
            if (tls.CustomRoots != null || tls.Protocols != null)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "HttpClient transport supports default TLS validation, accept-all, and client certificates only.");
            }
            if (!tls.ValidateCertificate)
            {
                handler.ServerCertificateCustomValidationCallback = (message, certificate, chain, errors) => true;
            }
            if (tls.ClientCertificate != null)
            {
                handler.ClientCertificates.Add(tls.ClientCertificate);
            }
        }
    }
}
```

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 28`。

- [ ] **Step 5: 提交**

```bash
git add src/UnityLlmTransport.HttpClient/HttpClientHandlerFactory.cs tests/UnityLlmTransport.HttpClient.Tests/HttpClientHandlerFactoryTests.cs
git commit -m "feat(httpclient): handler 映射（代理/TLS/重定向/解压）"
```

### Task 4: 测试支持服务 + Transport/Session/ReadStream + 基础集成测试

**Files:**
- Create: `tests/Phase3TestSupport/Phase3TestSupport.csproj`
- Create: `tests/Phase3TestSupport/Phase3TestServer.cs`
- Create: `tests/UnityLlmTransport.HttpClient.Tests/ServerFixture.cs`
- Create: `src/UnityLlmTransport.HttpClient/HttpClientTransport.cs`
- Create: `src/UnityLlmTransport.HttpClient/HttpClientSession.cs`
- Create: `src/UnityLlmTransport.HttpClient/HttpResponseReadStream.cs`
- Create: `src/UnityLlmTransport.HttpClient/HttpClientResponseHeaders.cs`
- Test: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientTransportTests.cs`
- Modify: `tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj`
- Modify: `UnityLlmTransport.sln`

**Interfaces:**
- Consumes: Task 1–3 的 Options/Validator/Builder/HandlerFactory/Classifier；Abstractions `ITransportSession`、`TransportException`。
- Produces: `HttpClientTransport()` / `(HttpClientTransportOptions)`；`OpenAsync`（响应头返回、任意状态码交付会话）；`HttpClientSession`（Abort/Dispose）；`HttpResponseReadStream`（读错误归一/Abort 后 `Aborted`）；`Phase3TestServer.Start(httpPort, httpsPort, proxyPort)`。

- [ ] **Step 1: 建测试支持工程并接入**

`tests/Phase3TestSupport/Phase3TestSupport.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

```bash
dotnet sln UnityLlmTransport.sln add tests/Phase3TestSupport/Phase3TestSupport.csproj
```

修改 `tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj`，在 ProjectReference 组中追加：

```xml
    <ProjectReference Include="..\Phase3TestSupport\Phase3TestSupport.csproj" />
```

- [ ] **Step 2: 写测试服务**

`tests/Phase3TestSupport/Phase3TestServer.cs`：

```csharp
using System.IO.Compression;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Phase3TestSupport;

public sealed class Phase3TestServer
{
    private readonly WebApplication _app;
    private readonly TcpListener _proxyListener;
    private readonly CancellationTokenSource _proxyCts = new CancellationTokenSource();
    private readonly Task _proxyLoop;
    private readonly X509Certificate2 _certificate;

    public int HttpPort { get; }
    public int HttpsPort { get; }
    public int ProxyPort { get; }

    public string HttpBase => "http://127.0.0.1:" + HttpPort;
    public string HttpsBase => "https://127.0.0.1:" + HttpsPort;
    public string ProxyUrl => "http://127.0.0.1:" + ProxyPort;

    public static Phase3TestServer Start(int httpPort, int httpsPort, int proxyPort)
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
        app.Run(HandleAsync);
        app.StartAsync().GetAwaiter().GetResult();

        var proxyListener = new TcpListener(IPAddress.Loopback, proxyPort);
        proxyListener.Start();

        var server = new Phase3TestServer(app, proxyListener, certificate, httpPort, httpsPort, proxyPort);
        return server;
    }

    private Phase3TestServer(
        WebApplication app,
        TcpListener proxyListener,
        X509Certificate2 certificate,
        int httpPort,
        int httpsPort,
        int proxyPort)
    {
        _app = app;
        _proxyListener = proxyListener;
        _certificate = certificate;
        HttpPort = httpPort;
        HttpsPort = httpsPort;
        ProxyPort = proxyPort;
        _proxyLoop = Task.Run(() => ProxyLoopAsync(_proxyCts.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _proxyCts.Cancel();
        _proxyListener.Stop();
        try { await _proxyLoop.ConfigureAwait(false); } catch { }
        await _app.StopAsync().ConfigureAwait(false);
        _app.Dispose();
        _certificate.Dispose();
    }

    private async Task ProxyLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _proxyListener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => HandleProxyClientAsync(client));
        }
    }

    private static async Task HandleProxyClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                string requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                string line;
                while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null && line.Length > 0)
                {
                }
                var body = "proxied";
                var response =
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Length: " + body.Length + "\r\n" +
                    "Content-Type: text/plain\r\n" +
                    "X-Phase3-Proxy: yes\r\n" +
                    "\r\n" +
                    body;
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response)).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private static async Task HandleAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "/";

        if (path == "/sse-error")
        {
            context.Response.StatusCode = 500;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"boom\"}");
            return;
        }
        if (path == "/redirect")
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = "/sse";
            return;
        }
        if (path == "/echo")
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("ok");
            return;
        }
        if (path == "/echo-headers")
        {
            var headers = context.Request.Headers.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToString());
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(headers));
            return;
        }
        if (path == "/fixture")
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(
                "data: 你\n\n" +
                "data: 好\n\n" +
                "data: ，\n\n" +
                "data: 世\n\n" +
                "data: 界\n\n" +
                "data: [DONE]\n\n");
            return;
        }
        if (path == "/drop")
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: partial\n\n");
            await context.Response.Body.FlushAsync();
            context.Abort();
            return;
        }
        if (path == "/slow-headers")
        {
            await Task.Delay(500);
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("ok");
            return;
        }
        if (path == "/big")
        {
            context.Response.ContentType = "text/event-stream";
            var payload = Encoding.UTF8.GetBytes("data: " + new string('x', 512) + "\n\n");
            for (int i = 0; i < 4096; i++)
            {
                await context.Response.Body.WriteAsync(payload);
                await context.Response.Body.FlushAsync();
            }
            await context.Response.WriteAsync("data: [DONE]\n\n");
            return;
        }

        if (path == "/sse" || path == "/gzip" || path == "/slow-first" || path == "/slow-idle")
        {
            context.Response.ContentType = "text/event-stream";
            Stream output = context.Response.Body;
            GZipStream? gzip = null;
            if (path == "/gzip")
            {
                context.Response.Headers.ContentEncoding = "gzip";
                gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
                output = gzip;
            }
            if (path == "/slow-first")
            {
                await context.Response.StartAsync();
                await Task.Delay(500);
            }

            int delayMs = path == "/slow-idle" ? 400 : 100;
            foreach (var part in new[] { "你", "好", "，", "世", "界" })
            {
                var payload = Encoding.UTF8.GetBytes(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"" + part + "\"}}]}\n\n");
                await output.WriteAsync(payload);
                await output.FlushAsync();
                await Task.Delay(delayMs);
            }
            await output.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            await output.FlushAsync();
            if (gzip != null)
            {
                await gzip.DisposeAsync();
            }
            return;
        }

        context.Response.StatusCode = 404;
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
        return X509CertificateLoader.LoadPkcs12(
            ephemeral.Export(X509ContentType.Pkcs12),
            null);
    }
}
```

`tests/UnityLlmTransport.HttpClient.Tests/ServerFixture.cs`：

```csharp
using System.Threading.Tasks;
using Phase3TestSupport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public sealed class ServerFixture : IAsyncLifetime
    {
        public Phase3TestServer Server { get; private set; } = null!;

        public Task InitializeAsync()
        {
            Server = Phase3TestServer.Start(5211, 5243, 5244);
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            return Server.DisposeAsync().AsTask();
        }
    }

    [CollectionDefinition("phase3-server")]
    public class Phase3ServerCollection : ICollectionFixture<ServerFixture> { }
}
```

- [ ] **Step 3: 写失败测试（基础集成）**

`tests/UnityLlmTransport.HttpClient.Tests/HttpClientTransportTests.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using UnityLlmTransport.Sse;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("phase3-server")]
    public class HttpClientTransportTests
    {
        private readonly ServerFixture _fixture;

        public HttpClientTransportTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        private HttpClientTransport CreateTransport()
        {
            return new HttpClientTransport();
        }

        private TransportRequest Request(string url, TransportHeaders? headers = null, string body = "{}")
        {
            return new TransportRequest(
                "POST",
                url,
                headers ?? new TransportHeaders(),
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)));
        }

        private static async Task<List<string>> ParseAsync(Stream stream)
        {
            var items = new List<string>();
            await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            {
                items.Add(item.Data);
            }
            return items;
        }

        [Fact]
        public async Task Streaming_Opens_Before_Body_Completes()
        {
            var transport = CreateTransport();
            var sw = Stopwatch.StartNew();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/sse"),
                new TransportOptions(),
                CancellationToken.None);
            long openMs = sw.ElapsedMilliseconds;

            var items = await ParseAsync(session.Body);
            await session.DisposeAsync();

            Assert.Equal(6, items.Count);
            Assert.True(openMs < sw.ElapsedMilliseconds);
        }

        [Fact]
        public async Task Non2xx_Returns_Session_With_Error_Body()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/sse-error"),
                new TransportOptions(),
                CancellationToken.None);

            var text = await ReadAllTextAsync(session.Body, CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(500, session.StatusCode);
            Assert.Contains("boom", text, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Captures_Status_And_Headers()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/echo"),
                new TransportOptions(),
                CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(200, session.StatusCode);
            Assert.True(session.Headers.TryGetValue("content-type", out var contentType));
            Assert.Contains("text/plain", contentType, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Gzip_Is_Decompressed_By_Default()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/gzip"),
                new TransportOptions(),
                CancellationToken.None);
            var items = await ParseAsync(session.Body);
            await session.DisposeAsync();

            Assert.Equal(6, items.Count);
        }

        [Fact]
        public async Task Redirect_Follow_Works_And_Error_Throws()
        {
            var transport = CreateTransport();
            var follow = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/redirect"),
                new TransportOptions(),
                CancellationToken.None);
            var items = await ParseAsync(follow.Body);
            await follow.DisposeAsync();
            Assert.Equal(6, items.Count);

            var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/redirect"),
                new TransportOptions { Redirects = RedirectPolicy.Error },
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public async Task Null_Header_Is_Not_Sent()
        {
            var headers = new TransportHeaders
            {
                ["X-Skip"] = null,
                ["X-Test"] = "yes"
            };
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/echo-headers", headers),
                new TransportOptions(),
                CancellationToken.None);
            var text = await ReadAllTextAsync(session.Body, CancellationToken.None);
            await session.DisposeAsync();

            using var json = JsonDocument.Parse(text);
            Assert.False(json.RootElement.TryGetProperty("X-Skip", out _));
            Assert.True(json.RootElement.TryGetProperty("X-Test", out var received));
            Assert.Equal("yes", received.GetString());
        }

        [Fact]
        public async Task Gzip_False_Leaves_Compressed_Bytes()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/gzip"),
                new TransportOptions { Decompress = false },
                CancellationToken.None);

            var buffer = new byte[2];
            var read = await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            await session.DisposeAsync();

            Assert.True(read >= 2);
            Assert.Equal(0x1f, buffer[0]);
            Assert.Equal(0x8b, buffer[1]);
        }

        [Fact]
        public void Capabilities_Are_Exact()
        {
            var transport = CreateTransport();
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.StreamingBody));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.SystemProxy));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.ExplicitProxy));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.ProxyAuthentication));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.TlsCertificateHandler));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.Compression));
            Assert.True(transport.Capabilities.HasFlag(TransportCapabilities.RequestFinalizer));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.WebSocket));
            Assert.False(transport.Capabilities.HasFlag(TransportCapabilities.Http2));
        }

        [Fact]
        public async Task Abort_Makes_Reads_Throw_Aborted()
        {
            var transport = CreateTransport();
            var session = await transport.OpenAsync(
                Request(_fixture.Server.HttpBase + "/sse"),
                new TransportOptions(),
                CancellationToken.None);
            var buffer = new byte[64];
            await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            session.Abort();

            var error = await Assert.ThrowsAsync<TransportException>(
                () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
            Assert.Equal(TransportErrorKind.Aborted, error.Kind);
            await session.DisposeAsync();
        }

        private static async Task<string> ReadAllTextAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[256];
            var builder = new StringBuilder();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            return builder.ToString();
        }
    }
}
```

- [ ] **Step 4: 跑测试确认失败**

Run: `dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo`
Expected: `error CS0246`（找不到 `HttpClientTransport` / `Phase3TestServer`）。

- [ ] **Step 5: 实现 Transport/Session/ReadStream/Headers**

`src/UnityLlmTransport.HttpClient/HttpClientResponseHeaders.cs`：

```csharp
using System.Net.Http;

namespace UnityLlmTransport
{
    internal static class HttpClientResponseHeaders
    {
        public static TransportHeaders From(HttpResponseMessage response)
        {
            var headers = new TransportHeaders();
            foreach (var header in response.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }
            }
            return headers;
        }
    }
}
```

`src/UnityLlmTransport.HttpClient/HttpResponseReadStream.cs`：

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    internal sealed class HttpResponseReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private TransportException? _terminalError;
        private bool _disposed;

        public HttpResponseReadStream(Stream inner, HttpResponseMessage response)
        {
            _inner = inner;
            _response = response;
        }

        public void Abort(TransportErrorKind kind)
        {
            var error = new TransportException(kind, "Response aborted (" + kind + ").");
            if (Interlocked.CompareExchange(ref _terminalError, error, null) == null)
            {
                try
                {
                    _response.Dispose();
                }
                catch (Exception)
                {
                }
            }
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ThrowIfTerminal();
            try
            {
                return _inner.Read(buffer, offset, count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowIfTerminal();
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Response stream failed.",
                    ex,
                    null,
                    null,
                    null,
                    false);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfTerminal();
            try
            {
                return await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowIfTerminal();
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Response stream failed.",
                    ex,
                    null,
                    null,
                    null,
                    false);
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

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _disposed = true;
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
```

`src/UnityLlmTransport.HttpClient/HttpClientSession.cs`：

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    internal sealed class HttpClientSession : ITransportSession
    {
        private readonly HttpResponseMessage _response;
        private readonly HttpResponseReadStream _body;
        private readonly HttpClient _client;
        private readonly bool _ownsClient;
        private readonly CancellationTokenRegistration _registration;
        private int _disposed;

        public HttpClientSession(
            HttpResponseMessage response,
            HttpResponseReadStream body,
            int statusCode,
            TransportHeaders headers,
            HttpClient client,
            bool ownsClient,
            CancellationToken lifetimeToken)
        {
            _response = response;
            _body = body;
            _client = client;
            _ownsClient = ownsClient;
            StatusCode = statusCode;
            Headers = headers;
            _registration = lifetimeToken.Register(OnLifetimeCancelled);
        }

        public int StatusCode { get; }
        public TransportHeaders Headers { get; }
        public Stream Body => _body;

        public void Abort()
        {
            AbortWith(TransportErrorKind.Aborted);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                AbortWith(TransportErrorKind.Aborted);
                _body.Dispose();
                _registration.Dispose();
                if (_ownsClient)
                {
                    _client.Dispose();
                }
            }
            return default;
        }

        private void OnLifetimeCancelled()
        {
            AbortWith(TransportErrorKind.Cancelled);
        }

        private void AbortWith(TransportErrorKind kind)
        {
            _body.Abort(kind);
            try
            {
                _response.Dispose();
            }
            catch (Exception)
            {
            }
        }
    }
}
```

`src/UnityLlmTransport.HttpClient/HttpClientTransport.cs`：

```csharp
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed class HttpClientTransport : IStreamingHttpTransport
    {
        private static readonly TransportCapabilities DefaultCapabilities =
            TransportCapabilities.StreamingBody |
            TransportCapabilities.SystemProxy |
            TransportCapabilities.ExplicitProxy |
            TransportCapabilities.ProxyAuthentication |
            TransportCapabilities.TlsCertificateHandler |
            TransportCapabilities.Compression |
            TransportCapabilities.RequestFinalizer;

        private readonly HttpClient? _injectedClient;
        private readonly TransportCapabilities _capabilities;

        public HttpClientTransport()
            : this(new HttpClientTransportOptions())
        {
        }

        public HttpClientTransport(HttpClientTransportOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            _injectedClient = options.Client;
            _capabilities = options.CapabilitiesOverride ?? DefaultCapabilities;
        }

        public TransportCapabilities Capabilities => _capabilities;

        public async Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
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

            HttpClientRequestValidator.Validate(options, _injectedClient != null);

            HttpRequestMessage message;
            try
            {
                message = HttpClientRequestBuilder.Build(request);
            }
            catch (TransportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw HttpClientErrorClassifier.Classify(ex, false, false);
            }

            HttpClient client = _injectedClient;
            bool ownsClient = false;
            if (client == null)
            {
                HttpClientHandler handler;
                try
                {
                    handler = HttpClientHandlerFactory.Create(options);
                }
                catch (TransportException)
                {
                    message.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    message.Dispose();
                    throw HttpClientErrorClassifier.Classify(ex, false, false);
                }
                client = new HttpClient(handler, disposeHandler: true);
                client.Timeout = Timeout.InfiniteTimeSpan;
                ownsClient = true;
            }

            CancellationTokenSource? timeoutCts = null;
            CancellationTokenSource? linkedCts = null;
            HttpResponseMessage? response = null;
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
                    response = await client.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    bool callerCancelled = cancellationToken.IsCancellationRequested;
                    bool connectTimeout = timeoutCts != null &&
                                          timeoutCts.IsCancellationRequested &&
                                          !callerCancelled;
                    message.Dispose();
                    if (ownsClient)
                    {
                        client.Dispose();
                    }
                    throw HttpClientErrorClassifier.Classify(ex, callerCancelled, connectTimeout);
                }

                TransportHeaders headers = HttpClientResponseHeaders.From(response);

                if (options.Redirects == RedirectPolicy.Error &&
                    (int)response.StatusCode >= 300 &&
                    (int)response.StatusCode < 400)
                {
                    var redirectError = new TransportException(
                        TransportErrorKind.Protocol,
                        "Redirect not allowed by RedirectPolicy.Error.",
                        null,
                        (int)response.StatusCode,
                        headers,
                        response.ReasonPhrase,
                        false);
                    response.Dispose();
                    message.Dispose();
                    if (ownsClient)
                    {
                        client.Dispose();
                    }
                    throw redirectError;
                }

                Stream inner;
                try
                {
                    inner = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response.Dispose();
                    message.Dispose();
                    if (ownsClient)
                    {
                        client.Dispose();
                    }
                    throw HttpClientErrorClassifier.Classify(ex, cancellationToken.IsCancellationRequested, false);
                }

                var body = new HttpResponseReadStream(inner, response);
                message.Dispose();
                return new HttpClientSession(
                    response,
                    body,
                    (int)response.StatusCode,
                    headers,
                    client,
                    ownsClient,
                    cancellationToken);
            }
            finally
            {
                linkedCts?.Dispose();
                timeoutCts?.Dispose();
            }
        }
    }
}
```

- [ ] **Step 6: 跑测试确认通过**

Run: `dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 37`。

- [ ] **Step 7: 提交**

```bash
git add tests/Phase3TestSupport/Phase3TestSupport.csproj tests/Phase3TestSupport/Phase3TestServer.cs tests/UnityLlmTransport.HttpClient.Tests/ServerFixture.cs tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj tests/UnityLlmTransport.HttpClient.Tests/HttpClientTransportTests.cs src/UnityLlmTransport.HttpClient/HttpClientTransport.cs src/UnityLlmTransport.HttpClient/HttpClientSession.cs src/UnityLlmTransport.HttpClient/HttpResponseReadStream.cs src/UnityLlmTransport.HttpClient/HttpClientResponseHeaders.cs UnityLlmTransport.sln
git commit -m "feat(httpclient): 传输/会话/响应流 + 进程内测试服务"
```

### Task 5: 代理/TLS/超时/取消/溢出/契约集成测试

**Files:**
- Create: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientTestHelpers.cs`
- Create: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientProxyTlsTests.cs`
- Create: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientTimeoutTests.cs`
- Create: `tests/UnityLlmTransport.HttpClient.Tests/HttpClientContractTests.cs`

**Interfaces:**
- Consumes: Task 4 的 `HttpClientTransport`/`Phase3TestServer`；Abstractions 的 `TimeoutTransport`/`MockTransport`/`SseParser`。
- Produces: 代理/TLS/超时/取消/溢出/读错误/契约/注入约束的自动化证据；若发现实现缺陷 → BLOCKED 并修复后重跑。

- [ ] **Step 1: 写共享测试助手**

`tests/UnityLlmTransport.HttpClient.Tests/HttpClientTestHelpers.cs`：

```csharp
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using UnityLlmTransport.Sse;

namespace UnityLlmTransport.Tests
{
    internal static class HttpClientTestHelpers
    {
        public static TransportRequest Request(
            string url,
            TransportHeaders? headers = null,
            string method = "POST",
            string body = "{}")
        {
            return new TransportRequest(
                method,
                url,
                headers ?? new TransportHeaders(),
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)));
        }

        public static async Task<List<string>> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var items = new List<string>();
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
            {
                items.Add(item.Data);
            }
            return items;
        }

        public static async Task<string> ReadAllTextAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[256];
            var builder = new StringBuilder();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            return builder.ToString();
        }
    }
}
```

- [ ] **Step 2: 写代理/TLS 测试**

`tests/UnityLlmTransport.HttpClient.Tests/HttpClientProxyTlsTests.cs`：

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("phase3-server")]
    public class HttpClientProxyTlsTests
    {
        private readonly ServerFixture _fixture;

        public HttpClientProxyTlsTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Explicit_Proxy_Is_Used()
        {
            var transport = new HttpClientTransport();
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions
                {
                    Mode = ProxyMode.Explicit,
                    Url = _fixture.Server.ProxyUrl
                }
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request("http://phase3.invalid/echo"),
                options,
                CancellationToken.None);
            var text = await HttpClientTestHelpers.ReadAllTextAsync(session.Body, CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(200, session.StatusCode);
            Assert.Equal("proxied", text);
            Assert.True(session.Headers.TryGetValue("x-phase3-proxy", out var proxyHeader));
            Assert.Equal("yes", proxyHeader);
        }

        [Fact]
        public async Task SelfSigned_Default_Is_Tls()
        {
            var transport = new HttpClientTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpsBase + "/echo"),
                new TransportOptions(),
                CancellationToken.None));

            Assert.Equal(TransportErrorKind.Tls, error.Kind);
        }

        [Fact]
        public async Task SelfSigned_AcceptAll_Returns_200()
        {
            var transport = new HttpClientTransport();
            var options = new TransportOptions
            {
                Tls = new TlsOptions { ValidateCertificate = false }
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpsBase + "/echo"),
                options,
                CancellationToken.None);
            var text = await HttpClientTestHelpers.ReadAllTextAsync(session.Body, CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(200, session.StatusCode);
            Assert.Equal("ok", text);
        }
    }
}
```

- [ ] **Step 3: 写超时/取消/溢出测试**

`tests/UnityLlmTransport.HttpClient.Tests/HttpClientTimeoutTests.cs`：

```csharp
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("phase3-server")]
    public class HttpClientTimeoutTests
    {
        private readonly ServerFixture _fixture;

        public HttpClientTimeoutTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task ConnectTimeout_Is_Connect_Timeout()
        {
            var transport = new HttpClientTransport();
            var options = new TransportOptions
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(100)
            };

            var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/slow-headers"),
                options,
                CancellationToken.None));

            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public async Task FirstByteTimeout_Is_Enforced_By_Core()
        {
            var transport = new TimeoutTransport(new HttpClientTransport());
            var options = new TransportOptions
            {
                FirstByteTimeout = TimeSpan.FromMilliseconds(100)
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/slow-first"),
                options,
                CancellationToken.None);
            var buffer = new byte[64];
            try
            {
                var error = await Assert.ThrowsAsync<TransportException>(
                    () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
                Assert.Equal(TransportErrorKind.FirstByteTimeout, error.Kind);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task IdleTimeout_Is_Enforced_By_Core()
        {
            var transport = new TimeoutTransport(new HttpClientTransport());
            var options = new TransportOptions
            {
                IdleTimeout = TimeSpan.FromMilliseconds(150)
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/slow-idle"),
                options,
                CancellationToken.None);
            var buffer = new byte[64];
            try
            {
                await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
                var error = await Assert.ThrowsAsync<TransportException>(
                    () => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
                Assert.Equal(TransportErrorKind.IdleTimeout, error.Kind);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task MaxResponseBytes_Overflow_Is_Enforced_By_Core()
        {
            var transport = new TimeoutTransport(new HttpClientTransport());
            var options = new TransportOptions
            {
                MaxResponseBytes = 64 * 1024
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/big"),
                options,
                CancellationToken.None);
            var buffer = new byte[4096];
            try
            {
                var error = await Assert.ThrowsAsync<TransportException>(async () =>
                {
                    while (await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None) > 0)
                    {
                    }
                });
                Assert.Equal(TransportErrorKind.Overflow, error.Kind);
            }
            finally
            {
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task Cancellation_Mid_Stream_Is_Cancelled()
        {
            var transport = new HttpClientTransport();
            using var cts = new CancellationTokenSource();
            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/sse"),
                new TransportOptions(),
                cts.Token);

            var parseTask = Task.Run(() => HttpClientTestHelpers.ParseAsync(session.Body));
            await Task.Delay(150);
            cts.Cancel();

            Exception? error = null;
            try
            {
                await parseTask;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            await session.DisposeAsync();

            var transportError = Assert.IsType<TransportException>(error);
            Assert.True(
                transportError.Kind == TransportErrorKind.Cancelled ||
                transportError.Kind == TransportErrorKind.Aborted);
        }

        [Fact]
        public async Task Stream_Failure_Is_Protocol()
        {
            var transport = new HttpClientTransport();
            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/drop"),
                new TransportOptions(),
                CancellationToken.None);

            var buffer = new byte[256];
            var error = await Assert.ThrowsAsync<TransportException>(async () =>
            {
                while (await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None) > 0)
                {
                }
            });
            await session.DisposeAsync();

            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public async Task Sync_Read_Is_Supported()
        {
            var transport = new HttpClientTransport();
            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/echo", method: "GET", body: string.Empty),
                new TransportOptions(),
                CancellationToken.None);

            var buffer = new byte[16];
            var read = session.Body.Read(buffer, 0, buffer.Length);
            await session.DisposeAsync();

            Assert.True(read > 0);
            Assert.Equal("ok", System.Text.Encoding.UTF8.GetString(buffer, 0, read));
        }
    }
}
```

- [ ] **Step 4: 写契约/注入约束测试**

`tests/UnityLlmTransport.HttpClient.Tests/HttpClientContractTests.cs`：

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
    [Collection("phase3-server")]
    public class HttpClientContractTests
    {
        private readonly ServerFixture _fixture;

        public HttpClientContractTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Same_Fixture_Produces_Same_Sse_Items_As_Mock()
        {
            var transport = new HttpClientTransport();
            var httpSession = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/fixture"),
                new TransportOptions(),
                CancellationToken.None);
            var httpItems = await HttpClientTestHelpers.ParseAsync(httpSession.Body);
            await httpSession.DisposeAsync();

            var fixtureText =
                "data: 你\n\n" +
                "data: 好\n\n" +
                "data: ，\n\n" +
                "data: 世\n\n" +
                "data: 界\n\n" +
                "data: [DONE]\n\n";
            var fixtureBytes = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(fixtureText));
            var mock = new MockTransport(_ => new MockResponse { Chunks = new[] { fixtureBytes } });
            var mockSession = await mock.OpenAsync(
                HttpClientTestHelpers.Request("http://localhost/fixture"),
                new TransportOptions(),
                CancellationToken.None);
            var mockItems = await HttpClientTestHelpers.ParseAsync(mockSession.Body);
            await mockSession.DisposeAsync();

            // Phase 2 UWR 的同一 SSE fixture 条目（见 docs/spike/phase2-results.md 的 data= 字段）。
            Assert.Equal(new[] { "你", "好", "，", "世", "界", "[DONE]" }, httpItems);
            Assert.Equal(httpItems, mockItems);
        }

        [Fact]
        public async Task Injected_Client_Rejects_PerRequest_Proxy()
        {
            using var client = new System.Net.Http.HttpClient();
            var transport = new HttpClientTransport(new HttpClientTransportOptions { Client = client });
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" }
            };

            var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/echo"),
                options,
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }
    }
}
```

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo`
Expected: `Passed!  - Failed: 0, Passed: 49`。

若任一格失败：先判断是实现缺陷还是测试服务时序；实现缺陷按 spec 修最小代码并记录 deviation；不得放宽判据。

- [ ] **Step 6: 提交**

```bash
git add tests/UnityLlmTransport.HttpClient.Tests/HttpClientTestHelpers.cs tests/UnityLlmTransport.HttpClient.Tests/HttpClientProxyTlsTests.cs tests/UnityLlmTransport.HttpClient.Tests/HttpClientTimeoutTests.cs tests/UnityLlmTransport.HttpClient.Tests/HttpClientContractTests.cs
git commit -m "test(httpclient): 代理/TLS/超时/取消/溢出/契约矩阵"
```

---

### Task 6: Phase3TestServer + Unity harness + 2021 Mono 门禁

**Files:**
- Create: `spikes/Phase3TestServer/Phase3TestServer.csproj`
- Create: `spikes/Phase3TestServer/Program.cs`
- Create: `scripts/assemble-phase3-unity.sh`
- Create: `unity/Phase3Harness/Assets/Editor/Phase3Build.cs`
- Create: `unity/Phase3Harness/Assets/Scripts/Phase3Runner.cs`
- Create: `unity/Phase3Harness/README.md`

**Interfaces:**
- Consumes: Task 1–5 的 `HttpClientTransport`；Abstractions 的 `TimeoutTransport`/`SseParser`；`Phase3TestSupport` 服务。
- Produces: 外部 Unity 工程可运行的 H1–H9 harness；2021.3 Mono 通过证据。

- [ ] **Step 1: 写测试服务 console**

`spikes/Phase3TestServer/Phase3TestServer.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\tests\Phase3TestSupport\Phase3TestSupport.csproj" />
  </ItemGroup>
</Project>
```

`spikes/Phase3TestServer/Program.cs`：

```csharp
using Phase3TestSupport;

var server = Phase3TestServer.Start(5201, 5233, 5234);
Console.WriteLine("Phase3 test server: HTTP 5201 / HTTPS 5233 / proxy 5234");
Console.WriteLine("Press Enter to stop...");
Console.ReadLine();
await server.DisposeAsync();
```

- [ ] **Step 2: 写装配脚本**

`scripts/assemble-phase3-unity.sh`：

```bash
#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
project="${1:?usage: assemble-phase3-unity.sh <unity-project-path>}"
assets="$project/Assets"
dest="$assets/UnityLlmTransport"

rm -rf "$dest"
mkdir -p "$dest/Abstractions" "$dest/Sse" "$dest/HttpClient"
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
copy_cs "$repo/src/UnityLlmTransport.Sse" "$dest/Sse"
rm -f "$dest/Sse/Internal/IsExternalInit.cs"
copy_cs "$repo/src/UnityLlmTransport.HttpClient" "$dest/HttpClient"
rm -f "$dest/HttpClient/Internal/IsExternalInit.cs"

cp "$repo/unity/Phase3Harness/Assets/Scripts/Phase3Runner.cs" "$assets/Scripts/"
cp "$repo/unity/Phase3Harness/Assets/Editor/Phase3Build.cs" "$assets/Editor/"

echo "assembled Phase 3 assets -> $project"
```

```bash
chmod +x scripts/assemble-phase3-unity.sh
```

- [ ] **Step 3: 写 Unity build/runner**

`unity/Phase3Harness/Assets/Editor/Phase3Build.cs`：

```csharp
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class Phase3Build
{
    public static void BuildMono()
    {
        var sceneDir = "Assets/Scenes";
        Directory.CreateDirectory(sceneDir);
        var scenePath = sceneDir + "/Phase3.unity";

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        var runner = new GameObject("Phase3Runner");
        runner.AddComponent<Phase3Runner>();
        EditorSceneManager.SaveScene(scene, scenePath);

        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(scenePath, true) };
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.Standalone, ScriptingImplementation.Mono2x);

        var outputDir = Path.Combine(@"D:\UnitySpikes\Builds\Phase3", Application.unityVersion, "mono");
        Directory.CreateDirectory(outputDir);
        var outputExe = Path.Combine(outputDir, "Phase3.exe");

        var report = BuildPipeline.BuildPlayer(
            new[] { scenePath },
            outputExe,
            BuildTarget.StandaloneWindows64,
            BuildOptions.Development);

        Debug.Log("PHASE3_BUILD mono " + report.summary.result +
                  " errors=" + report.summary.totalErrors + " output=" + outputExe);
        EditorApplication.Exit(report.summary.result == BuildResult.Succeeded ? 0 : 1);
    }
}
```

`unity/Phase3Harness/Assets/Scripts/Phase3Runner.cs`：

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
using UnityLlmTransport.Sse;
using UnityEngine;

public sealed class Phase3Runner : MonoBehaviour
{
    private const string HttpBase = "http://127.0.0.1:5201";
    private const string HttpsBase = "https://127.0.0.1:5233";
    private const string ProxyUrl = "http://127.0.0.1:5234";

    private readonly StringBuilder _results = new StringBuilder();
    private HttpClientTransport _transport;
    private int _mainThreadId;

    private IEnumerator Start()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _results.AppendLine("unity_version=" + Application.unityVersion);
        _results.AppendLine("main_thread=" + _mainThreadId);
        _transport = new HttpClientTransport();

        yield return Run("H1_stream", ScenarioStream);
        yield return Run("H2_cancel", ScenarioCancel);
        yield return Run("H3_first_byte_timeout", ScenarioFirstByteTimeout);
        yield return Run("H3_idle_timeout", ScenarioIdleTimeout);
        yield return Run("H4_error_body", ScenarioErrorBody);
        yield return Run("H5_gzip", ScenarioGzip);
        yield return Run("H6_redirect_error", ScenarioRedirectError);
        yield return Run("H6_redirect_follow", ScenarioRedirectFollow);
        yield return Run("H7_tls_default", ScenarioTlsDefault);
        yield return Run("H7_tls_accept", ScenarioTlsAccept);
        yield return Run("H8_explicit_proxy", ScenarioExplicitProxy);
        yield return Run("H9_headers_threads", ScenarioHeadersThreads);

        var path = Path.Combine(Application.persistentDataPath, "phase3-results.txt");
        File.WriteAllText(path, _results.ToString());
        UnityEngine.Debug.Log("PHASE3_RESULTS_PATH=" + path);
        foreach (var line in _results.ToString().Split('\n'))
        {
            UnityEngine.Debug.Log("PHASE3 " + line);
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
                   " code=" + (transport.StatusCode.HasValue ? transport.StatusCode.Value.ToString() : "-") +
                   " msg=" + Trunc(transport.Message, 80);
        }
        return error.GetType().Name + ": " + Trunc(error.Message, 80);
    }

    private async Task<string> ScenarioStream()
    {
        var sw = Stopwatch.StartNew();
        var session = await _transport.OpenAsync(Request("/sse"), new TransportOptions(), CancellationToken.None);
        long openMs = sw.ElapsedMilliseconds;
        var items = await ParseAsync(session.Body);
        await session.DisposeAsync();
        return "status=" + session.StatusCode +
               "|open_ms=" + openMs +
               "|items=" + items.Count +
               "|total_ms=" + sw.ElapsedMilliseconds +
               "|data=" + string.Join("", items);
    }

    private async Task<string> ScenarioCancel()
    {
        using var cts = new CancellationTokenSource();
        var session = await _transport.OpenAsync(Request("/sse"), new TransportOptions(), cts.Token);
        var parseTask = Task.Run(() => ParseAsync(session.Body));
        await Task.Delay(150);
        cts.Cancel();

        Exception error = null;
        try { await parseTask; } catch (Exception ex) { error = ex; }
        await session.DisposeAsync();
        return "cancel_error=" + Describe(error);
    }

    private async Task<string> ScenarioFirstByteTimeout()
    {
        var transport = new TimeoutTransport(_transport);
        var options = new TransportOptions { FirstByteTimeout = TimeSpan.FromMilliseconds(100) };
        var session = await transport.OpenAsync(Request("/slow-first"), options, CancellationToken.None);
        var buffer = new byte[64];
        try
        {
            var error = await AssertFault(() => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
            return "kind=" + KindOf(error);
        }
        finally { await session.DisposeAsync(); }
    }

    private async Task<string> ScenarioIdleTimeout()
    {
        var transport = new TimeoutTransport(_transport);
        var options = new TransportOptions { IdleTimeout = TimeSpan.FromMilliseconds(150) };
        var session = await transport.OpenAsync(Request("/slow-idle"), options, CancellationToken.None);
        var buffer = new byte[64];
        try
        {
            await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            var error = await AssertFault(() => session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
            return "kind=" + KindOf(error);
        }
        finally { await session.DisposeAsync(); }
    }

    private async Task<string> ScenarioErrorBody()
    {
        var session = await _transport.OpenAsync(Request("/sse-error"), new TransportOptions(), CancellationToken.None);
        var text = await ReadAllTextAsync(session.Body);
        await session.DisposeAsync();
        return "status=" + session.StatusCode + "|body=" + Trunc(text, 60);
    }

    private async Task<string> ScenarioGzip()
    {
        var session = await _transport.OpenAsync(Request("/gzip"), new TransportOptions(), CancellationToken.None);
        var items = await ParseAsync(session.Body);
        await session.DisposeAsync();
        return "items=" + items.Count + "|data=" + string.Join("", items);
    }

    private async Task<string> ScenarioRedirectError()
    {
        try
        {
            await _transport.OpenAsync(Request("/redirect"), new TransportOptions { Redirects = RedirectPolicy.Error }, CancellationToken.None);
            return "FAIL_no_redirect_error";
        }
        catch (TransportException ex)
        {
            return "kind=" + ex.Kind;
        }
    }

    private async Task<string> ScenarioRedirectFollow()
    {
        var session = await _transport.OpenAsync(Request("/redirect"), new TransportOptions { Redirects = RedirectPolicy.Follow }, CancellationToken.None);
        var items = await ParseAsync(session.Body);
        await session.DisposeAsync();
        return "status=" + session.StatusCode + "|items=" + items.Count;
    }

    private async Task<string> ScenarioTlsDefault()
    {
        try
        {
            await _transport.OpenAsync(HttpsRequest("/echo"), new TransportOptions(), CancellationToken.None);
            return "FAIL_no_tls_error";
        }
        catch (TransportException ex)
        {
            return "kind=" + ex.Kind;
        }
    }

    private async Task<string> ScenarioTlsAccept()
    {
        var options = new TransportOptions { Tls = new TlsOptions { ValidateCertificate = false } };
        var session = await _transport.OpenAsync(HttpsRequest("/echo"), options, CancellationToken.None);
        var text = await ReadAllTextAsync(session.Body);
        await session.DisposeAsync();
        return "status=" + session.StatusCode + "|body=" + Trunc(text, 20);
    }

    private async Task<string> ScenarioExplicitProxy()
    {
        var options = new TransportOptions
        {
            Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = ProxyUrl }
        };
        var session = await _transport.OpenAsync(
            new TransportRequest("GET", "http://phase3.invalid/echo", new TransportHeaders(), ReadOnlyMemory<byte>.Empty),
            options,
            CancellationToken.None);
        var text = await ReadAllTextAsync(session.Body);
        await session.DisposeAsync();
        return "status=" + session.StatusCode + "|body=" + Trunc(text, 20);
    }

    private async Task<string> ScenarioHeadersThreads()
    {
        var headers = new TransportHeaders
        {
            ["X-Skip"] = null,
            ["X-Test"] = "yes"
        };
        var session = await _transport.OpenAsync(Request("/echo-headers", headers), new TransportOptions(), CancellationToken.None);
        int parseThread = -1;
        string text = null;
        await Task.Run(async () =>
        {
            parseThread = Thread.CurrentThread.ManagedThreadId;
            text = await ReadAllTextAsync(session.Body);
        });
        await session.DisposeAsync();
        return "main=" + _mainThreadId +
               "|parse=" + parseThread +
               "|skip=" + text.Contains("\"X-Skip\"") +
               "|test=" + text.Contains("\"X-Test\"");
    }

    private static async Task<Exception> AssertFault(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static string KindOf(Exception error)
    {
        return error is TransportException transport ? transport.Kind.ToString() : "none";
    }

    private static async Task<List<string>> ParseAsync(Stream stream)
    {
        var items = new List<string>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync())
        {
            items.Add(item.Data);
        }
        return items;
    }

    private static async Task<string> ReadAllTextAsync(Stream stream)
    {
        var buffer = new byte[256];
        var builder = new StringBuilder();
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
        {
            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return builder.ToString();
    }

    private static TransportRequest Request(string path, TransportHeaders headers = null)
    {
        return new TransportRequest(
            "POST",
            HttpBase + path,
            headers ?? new TransportHeaders
            {
                ["accept"] = "text/event-stream",
                ["content-type"] = "application/json"
            },
            new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{}")));
    }

    private static TransportRequest HttpsRequest(string path)
    {
        return new TransportRequest(
            "GET",
            HttpsBase + path,
            new TransportHeaders(),
            ReadOnlyMemory<byte>.Empty);
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

`unity/Phase3Harness/README.md`：

```markdown
# Phase 3 Unity Harness

## 前置

```bash
# 终端 A：本地测试服务（HTTP 5201 / HTTPS 5233 / 显式代理 5234）
dotnet run -c Release --project spikes/Phase3TestServer
```

## 装配/构建/运行

```bash
# 一次性建工程
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -createProject D:/UnitySpikes/Phase3-<ver> -batchmode -quit -logFile D:/UnitySpikes/create-phase3-<ver>.log
bash scripts/assemble-phase3-unity.sh D:/UnitySpikes/Phase3-<ver>
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -batchmode -quit -projectPath D:/UnitySpikes/Phase3-<ver> -executeMethod Phase3Build.BuildMono -logFile D:/UnitySpikes/phase3-<ver>-mono.log
D:/UnitySpikes/Builds/Phase3/<unityVersion>/mono/Phase3.exe -logFile D:/UnitySpikes/phase3-<ver>-mono-player.log
```

判据：日志 `PHASE3 <name>|OK|...`；结果文件 `%LOCALAPPDATA%Low\DefaultCompany\<工程名>\phase3-results.txt`。
本阶段只跑 Mono（IL2CPP 不声明）；服务端为外部进程，结束后按 PID 关闭，禁止 mass-kill dotnet。
```

- [ ] **Step 4: 2021.3 工程 + Mono 门禁**

```bash
mkdir -p D:/UnitySpikes/Phase3-2021
D:/Unity/Hub/Editor/2021.3.45f1/Editor/Unity.exe -createProject D:/UnitySpikes/Phase3-2021 -batchmode -quit -logFile D:/UnitySpikes/create-phase3-2021.log
bash scripts/assemble-phase3-unity.sh D:/UnitySpikes/Phase3-2021
D:/Unity/Hub/Editor/2021.3.45f1/Editor/Unity.exe -batchmode -quit -projectPath D:/UnitySpikes/Phase3-2021 -executeMethod Phase3Build.BuildMono -logFile D:/UnitySpikes/phase3-2021-mono.log
dotnet run -c Release --project spikes/Phase3TestServer > /tmp/phase3-server.log 2>&1 &
SERVER_PID=$!
sleep 2
D:/UnitySpikes/Builds/Phase3/2021.3.45f1c2/mono/Phase3.exe -logFile D:/UnitySpikes/phase3-2021-mono-player.log
kill $SERVER_PID || true
grep "PHASE3 " D:/UnitySpikes/phase3-2021-mono-player.log
```

Expected 12 条 `PHASE3 <name>|OK|`：
- H1 `items>=6` 且 `open_ms < total_ms`；
- H2 `Cancelled/Aborted`；
- H3 first/idle 对应 Kind；
- H4 `status=500|body=...boom`；
- H5 `items>=6`；
- H6 error 有异常 / follow `items>=6`；
- H7 default `Tls` / accept `status=200`；
- H8 `status=200|body=proxied`；
- H9 `parse != main`、`skip=False`、`test=True`。

- [ ] **Step 5: 提交**

```bash
git add spikes/Phase3TestServer/Phase3TestServer.csproj spikes/Phase3TestServer/Program.cs scripts/assemble-phase3-unity.sh unity/Phase3Harness/Assets/Editor/Phase3Build.cs unity/Phase3Harness/Assets/Scripts/Phase3Runner.cs unity/Phase3Harness/README.md
git commit -m "test(phase3): Unity Mono harness H1-H9"
```

---

### Task 7: 三版本 Mono 矩阵 + 结果/证据/文档收尾

**Files:**
- Create: `docs/spike/phase3-results.md`
- Create: `docs/spike/evidence/phase3/<ver>-mono-results.txt`（3 份）
- Modify: `docs/05-版本与兼容基线.md`
- Modify: `README.md`
- Modify: `docs/SOP/Unity多版本通讯测试-SOP-2026-11-06.md`
- Modify: `unity/Phase3Harness/README.md`（若实机发现新步骤）

**Interfaces:**
- Consumes: Task 6 harness；三版本 Unity 编辑器。
- Produces: 三版本 Mono 原始证据 + Phase 3 验收结论。

- [ ] **Step 1: 建 6.3 / 6.6 工程并装配**

```bash
mkdir -p D:/UnitySpikes/Phase3-6003 D:/UnitySpikes/Phase3-6006
D:/Unity/Hub/Editor/6000.3.28f1/Editor/Unity.exe -createProject D:/UnitySpikes/Phase3-6003 -batchmode -quit -logFile D:/UnitySpikes/create-phase3-6003.log
D:/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe -createProject D:/UnitySpikes/Phase3-6006 -batchmode -quit -logFile D:/UnitySpikes/create-phase3-6006.log
bash scripts/assemble-phase3-unity.sh D:/UnitySpikes/Phase3-6003
bash scripts/assemble-phase3-unity.sh D:/UnitySpikes/Phase3-6006
```

Expected: 两个工程存在 `Assets/UnityLlmTransport/{Abstractions,Sse,HttpClient}`；装配输出两行。

- [ ] **Step 2: 三版本 Mono 构建与运行**

```bash
for row in "2021.3.45f1|D:/UnitySpikes/Phase3-2021|D:/Unity/Hub/Editor/2021.3.45f1/Editor/Unity.exe" \
           "6000.3.28f1|D:/UnitySpikes/Phase3-6003|D:/Unity/Hub/Editor/6000.3.28f1/Editor/Unity.exe" \
           "6000.6.8f1|D:/UnitySpikes/Phase3-6006|D:/Unity/Hub/Editor/6000.6.8f1/Editor/Unity.exe"; do
  ver="${row%%|*}"; rest="${row#*|}"; proj="${rest%%|*}"; editor="${rest#*|}"
  "$editor" -batchmode -quit -projectPath "$proj" -executeMethod Phase3Build.BuildMono -logFile "D:/UnitySpikes/phase3-$ver-mono.log"
  grep -n "PHASE3_BUILD mono Succeeded" "D:/UnitySpikes/phase3-$ver-mono.log" || (echo "BUILD FAILED $ver"; exit 1)
done
```

启动测试服务并逐版本运行：

```bash
dotnet run -c Release --project spikes/Phase3TestServer > /tmp/phase3-server.log 2>&1 &
SERVER_PID=$!
sleep 2
for ver in 2021.3.45f1c2 6000.3.28f1 6000.6.8f1; do
  D:/UnitySpikes/Builds/Phase3/$ver/mono/Phase3.exe -logFile D:/UnitySpikes/phase3-$ver-mono-player.log
  grep "PHASE3 " D:/UnitySpikes/phase3-$ver-mono-player.log > D:/UnitySpikes/phase3-$ver-mono-results.txt
done
kill $SERVER_PID || true
```

Expected：每版本 12 条 `PHASE3 <name>|OK|`（H1/H2/H3×2/H4/H5/H6×2/H7×2/H8/H9）。2021 构建目录 `2021.3.45f1c2`；若路径不存在以 `Application.unityVersion` 日志为准。

- [ ] **Step 3: 写结果文档与原始证据**

`docs/spike/phase3-results.md`：

```markdown
# Phase 3 · HttpClient 三版本 Mono 结果

> 日期 + 环境 + 命令见 `unity/Phase3Harness/README.md`。
> 原始日志：`D:/UnitySpikes/phase3-<ver>-mono-player.log`；证据副本在 `docs/spike/evidence/phase3/`。

## 矩阵

| 场景 | 2021.3.45f1 | 6000.3.28f1 | 6000.6.8f1 | 判据 |
|---|---|---|---|---|
| H1 流式 | | | | open_ms < total_ms；items≥6 |
| H2 取消 | | | | Cancelled/Aborted |
| H3 首字节超时 | | | | FirstByteTimeout |
| H3 空闲超时 | | | | IdleTimeout |
| H4 500 错误体 | | | | status=500；body 含 boom |
| H5 gzip | | | | items≥6 |
| H6 重定向 Error | | | | 有异常 |
| H6 重定向 Follow | | | | items≥6 |
| H7 TLS default | | | | Tls |
| H7 TLS accept | | | | status=200 |
| H8 显式代理 | | | | status=200；body=proxied |
| H9 headers/线程 | | | | parse ≠ main；skip=False；test=True |

## 结论

- 三版本 Mono 全过/失败项（照实填）；
- HttpClient 在 Unity Mono 下是否真流式（H1 open_ms vs total_ms）；
- TLS 默认/accept-all、显式 WebProxy 三版本一致性；
- gzip 自动解压后的 `Content-Encoding` 观察（记录，不改实现）；
- 已知遗留（TLS 外网依赖、HTTP/2 未声明、IL2CPP 未声明等）。
```

逐格粘贴 `grep` 原始行；将三份 `D:/UnitySpikes/phase3-<ver>-mono-results.txt` 复制到 `docs/spike/evidence/phase3/`（内容保持 12 行 OK + version/main_thread）。

- [ ] **Step 4: 更新文档**

`docs/05-版本与兼容基线.md` §5 的 HttpClient 行补充：

```
UnityLlmTransport.HttpClient     // HttpClient 实现（桌面/后端；显式代理/TLS/RequestFinalizer；三版本 Mono 已验证）
tests/Phase3TestSupport + unity/Phase3Harness  // 进程内 Kestrel 测试服务 + Unity Mono harness
```

`README.md` 状态更新为 `Phase 0/1/2/3 已完成（2026-11-07）；Phase 4 待开工`；目录树补 `Phase3Harness`/`Phase3TestServer`/`tests/UnityLlmTransport.HttpClient.Tests`。

`docs/SOP/Unity多版本通讯测试-SOP-2026-11-06.md` 追加：

```markdown
## Phase 3 harness（HttpClient）

1. `dotnet run -c Release --project spikes/Phase3TestServer`（HTTP 5201 / HTTPS 5233 / 代理 5234）
2. `bash scripts/assemble-phase3-unity.sh D:/UnitySpikes/Phase3-<ver>`
3. `-executeMethod Phase3Build.BuildMono`
4. `Builds/Phase3/<Application.unityVersion>/mono/Phase3.exe`
5. 判据：`PHASE3 <name>|OK|`；本阶段只跑 Mono（IL2CPP 不声明）
6. 服务 PID 级 kill；禁止 mass-kill dotnet
```

- [ ] **Step 5: 阶段门禁**

```bash
dotnet build UnityLlmTransport.sln -c Release
dotnet test tests/UnityLlmTransport.HttpClient.Tests/UnityLlmTransport.HttpClient.Tests.csproj --nologo
grep -rn "UnityEngine" src/UnityLlmTransport.HttpClient --include='*.cs' || echo "OK: HttpClient zero UnityEngine"
```

Expected: build 0 errors；`Passed!  - Failed: 0, Passed: 49`；`OK: HttpClient zero UnityEngine`。

- [ ] **Step 6: 提交**

```bash
git add docs/spike/phase3-results.md docs/spike/evidence/phase3 docs/05-版本与兼容基线.md README.md docs/SOP/Unity多版本通讯测试-SOP-2026-11-06.md unity/Phase3Harness/README.md
git commit -m "docs(phase3): 三版本 Mono 结果与收尾"
```

---

## 验收对照（Spec §9）

| Spec 验收项 | 落点 |
|---|---|
| 包 netstandard2.1 编译 0 错误，零 UnityEngine | Task 1 + Task 7 Step 5 |
| dotnet 测试全绿（流式/取消/超时/溢出/gzip/重定向/headers/错误/代理/TLS/注入/契约） | Task 4/5（49 项） |
| 三版本 Unity Mono H1–H9 | Task 6/7 + results.md + evidence |
| 与 UWR 同一 fixture 条目一致 | Task 5 `Same_Fixture_Produces_Same_Sse_Items_As_Mock` |
| Capabilities 与实测一致 | Task 4 `Capabilities_Are_Exact` |
| docs/05/SOP/README 更新 | Task 7 Step 4 |
| 不改 Abstractions/Sse/UWR/PiUnityAgent.Ai 形状 | 全部任务仅新增 HttpClient 包/测试/资产 |

## 执行注意

- Unity 构建可能因许可证/模块失败：按 SOP 排查；不得伪报结果。
- 测试服务端口冲突：自动化 5211/5243/5244，Unity 5201/5233/5234；启动前检查端口，PID 级终止。
- H7 TLS 使用本地自签服务，不依赖外网。
- `Phase3TestSupport` 的 ASP.NET Core 只用于测试，不进产品包。




