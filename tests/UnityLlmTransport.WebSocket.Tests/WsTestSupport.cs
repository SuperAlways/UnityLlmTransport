using System;
using System.IO;
using System.Linq;
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
using Xunit;

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
            HttpBase = "ws://127.0.0.1:" + httpPort;
            HttpsBase = "wss://127.0.0.1:" + httpsPort;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
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
                        await SendAsync(socket, Encoding.UTF8.GetBytes("hel"), System.Net.WebSockets.WebSocketMessageType.Text, false);
                        await SendAsync(socket, Encoding.UTF8.GetBytes("lo"), System.Net.WebSockets.WebSocketMessageType.Text, true);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    case "/ws/close":
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    case "/ws/idle":
                        await Task.Delay(500);
                        await SendAsync(socket, Encoding.UTF8.GetBytes("late"), System.Net.WebSockets.WebSocketMessageType.Text, true);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    case "/ws/drop":
                        // 先让客户端完成 101 握手读取，再掐断连接——否则掐断会竞速握手本身。
                        await Task.Delay(100);
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
                        await SendAsync(socket, Encoding.UTF8.GetBytes(info), System.Net.WebSockets.WebSocketMessageType.Text, true);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    case "/ws/huge":
                        var hugeChunk = new byte[64 * 1024];
                        for (int i = 0; i < 257; i++)
                        {
                            await SendAsync(socket, hugeChunk, System.Net.WebSockets.WebSocketMessageType.Binary, false);
                        }
                        await SendAsync(socket, new byte[1], System.Net.WebSockets.WebSocketMessageType.Binary, true);
                        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                        break;
                    case "/ws/slow":
                        await SendAsync(socket, Encoding.UTF8.GetBytes("slow"), System.Net.WebSockets.WebSocketMessageType.Text, true);
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
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    return;
                }
                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text)
                {
                    var text = "echo:" + Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await SendAsync(socket, Encoding.UTF8.GetBytes(text), System.Net.WebSockets.WebSocketMessageType.Text, result.EndOfMessage);
                }
                else
                {
                    await SendAsync(socket, buffer.AsSpan(0, result.Count).ToArray(), System.Net.WebSockets.WebSocketMessageType.Binary, result.EndOfMessage);
                }
            }
        }

        private static Task SendAsync(WebSocket socket, byte[] payload, System.Net.WebSockets.WebSocketMessageType type, bool endOfMessage)
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
