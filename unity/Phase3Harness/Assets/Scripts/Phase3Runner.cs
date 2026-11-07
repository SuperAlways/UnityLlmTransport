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
