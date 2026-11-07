// Phase 0 Task 4 — HttpClient 代理/TLS 探针（brief Step 4.1 扩展：三种 handler × 两个端点）
// 用法: dotnet run --project spikes/ProxyTlsSpike [额外URL...]
// 前置: Task 2 的 SseTestServer 在 http://127.0.0.1:5199/ 监听（/sse）
//       环境变量 PROXY_TLS_SPIKE_ALLOW_UNTRUSTED=1 时打开 DangerousAcceptAnyServerCertificateValidator（自签 TLS 对照格）
using System.Diagnostics;
using System.Net;
using System.Net.Http;

var allowUntrusted = Environment.GetEnvironmentVariable("PROXY_TLS_SPIKE_ALLOW_UNTRUSTED") == "1";

var urls = new List<(string Name, string Url)>
{
    ("local-sse", "http://127.0.0.1:5199/sse"),
    ("remote-https", "https://example.com/"),
};
// 额外判别端点：args 追加，用于证明代理真的被走（如只在走代理时可达的站点）/ 自签 TLS 端点
urls.AddRange(args.Select((a, i) => ($"arg{i}", a)));

// 判别诊断：打印 .NET 解析出的系统代理（含 bypass），只读不联网
var systemProxy = HttpClient.DefaultProxy;
foreach (var (_, url) in urls)
{
    Console.WriteLine($"system-proxy-resolution | {url} -> {systemProxy.GetProxy(new Uri(url))?.ToString() ?? "(direct)"}");
}

HttpClientHandler MakeHandler(bool useProxy, WebProxy? proxy) => new()
{
    UseProxy = useProxy,
    Proxy = proxy,
    ServerCertificateCustomValidationCallback = allowUntrusted
        ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        : null,
};

var cases = new (string Name, HttpClientHandler Handler)[]
{
    ("no-proxy", MakeHandler(useProxy: false, proxy: null)),
    ("system-proxy", MakeHandler(useProxy: true, proxy: null)),
    ("explicit-proxy", MakeHandler(useProxy: true, proxy: new WebProxy("http://127.0.0.1:7897"))),
};
Console.WriteLine($"tls-mode | {(allowUntrusted ? "accept-any-callback" : "system-trust")}");

foreach (var (caseName, handler) in cases)
{
    using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
    foreach (var (urlName, url) in urls)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            Console.WriteLine(
                $"{caseName} | {urlName} | HTTP {(int)resp.StatusCode} | {sw.ElapsedMilliseconds}ms | chars={body.Length}");
        }
        catch (Exception e)
        {
            sw.Stop();
            var inner = e.InnerException is null ? "" : $" << {e.InnerException.GetType().Name}: {e.InnerException.Message}";
            Console.WriteLine(
                $"{caseName} | {urlName} | FAIL {e.GetType().Name}: {e.Message}{inner} | {sw.ElapsedMilliseconds}ms");
        }
    }
}
