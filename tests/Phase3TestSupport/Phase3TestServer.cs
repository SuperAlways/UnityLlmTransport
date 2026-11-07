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
        await _app.DisposeAsync().ConfigureAwait(false);
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
                string? requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                string? line;
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
