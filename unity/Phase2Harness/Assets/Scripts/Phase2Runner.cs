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

public sealed class Phase2Runner : MonoBehaviour
{
    private readonly StringBuilder _results = new StringBuilder();
    private UnityCoroutineHost _host;
    private UnityWebRequestTransport _transport;
    private int _mainThreadId;

    private IEnumerator Start()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        _results.AppendLine("unity_version=" + Application.unityVersion);
        _results.AppendLine("main_thread=" + _mainThreadId);

        var hostObject = new GameObject("Phase2Host");
        _host = hostObject.AddComponent<UnityCoroutineHost>();
        yield return null;

        _transport = new UnityWebRequestTransport(_host, new UnityWebRequestTransportOptions());
        yield return Run("U1_stream", ScenarioU1);
        yield return Run("U2_snapshot", ScenarioU2);
        yield return Run("U3_cancel", ScenarioU3);
        yield return Run("U4_first_byte_timeout", ScenarioU4FirstByte);
        yield return Run("U4_idle_timeout", ScenarioU4Idle);
        yield return Run("U5_overflow", ScenarioU5);
        yield return Run("U6_error_body", ScenarioU6);
        yield return Run("U7_tls_accept", ScenarioU7TlsAccept);
        yield return Run("U7_tls_default", ScenarioU7TlsDefault);
        yield return Run("U7_explicit_proxy", ScenarioU7Proxy);
        yield return Run("U8_gzip", ScenarioU8);
        yield return Run("U9_threads", ScenarioU9);
        yield return Run("U10_redirect_error", ScenarioU10Error);
        yield return Run("U10_redirect_follow", ScenarioU10Follow);
        yield return Run("U11_abort_residual", ScenarioU11);

        var path = Path.Combine(Application.persistentDataPath, "phase2-results.txt");
        File.WriteAllText(path, _results.ToString());
        UnityEngine.Debug.Log("PHASE2_RESULTS_PATH=" + path);
        foreach (var line in _results.ToString().Split('\n'))
        {
            UnityEngine.Debug.Log("PHASE2 " + line);
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

    private async Task<string> ScenarioU1()
    {
        var sw = Stopwatch.StartNew();
        var session = await _transport.OpenAsync(Request("/sse"), new TransportOptions(), CancellationToken.None);
        long openMs = sw.ElapsedMilliseconds;
        var parse = await ParseAsync(session.Body, sw);
        await session.DisposeAsync();
        return "status=" + session.StatusCode +
               "|open_ms=" + openMs +
               "|items=" + parse.Items.Count +
               "|first_ms=" + parse.FirstItemMs +
               "|total_ms=" + sw.ElapsedMilliseconds +
               "|data=" + string.Join("", parse.Items);
    }

    private async Task<string> ScenarioU2()
    {
        var session = await _transport.OpenAsync(Request("/sse"), new TransportOptions(), CancellationToken.None);
        bool hasContentType = session.Headers.TryGetValue("content-type", out var contentType) &&
                              contentType != null &&
                              contentType.Contains("text/event-stream");
        var buffer = new byte[64];
        await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
        await session.DisposeAsync();
        return "status=" + session.StatusCode + "|content_type=" + hasContentType;
    }

    private async Task<string> ScenarioU3()
    {
        using var cts = new CancellationTokenSource();
        var session = await _transport.OpenAsync(Request("/sse"), new TransportOptions(), cts.Token);
        var parseTask = Task.Run(() => ParseAsync(session.Body, Stopwatch.StartNew()));
        await Task.Delay(150);
        cts.Cancel();

        Exception error = null;
        try
        {
            await parseTask;
        }
        catch (Exception ex)
        {
            error = ex;
        }
        await session.DisposeAsync();
        return "cancel_error=" + Describe(error);
    }

    private async Task<string> ScenarioU4FirstByte()
    {
        var options = new TransportOptions { FirstByteTimeout = TimeSpan.FromMilliseconds(100) };
        try
        {
            await _transport.OpenAsync(Request("/sse-slow"), options, CancellationToken.None);
            return "FAIL_no_timeout";
        }
        catch (TransportException ex)
        {
            return "kind=" + ex.Kind;
        }
    }

    private async Task<string> ScenarioU4Idle()
    {
        var transport = new TimeoutTransport(_transport);
        var options = new TransportOptions { IdleTimeout = TimeSpan.FromMilliseconds(150) };
        var session = await transport.OpenAsync(Request("/sse-idle"), options, CancellationToken.None);
        var buffer = new byte[64];
        try
        {
            await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
            try
            {
                await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
                return "FAIL_no_idle_timeout";
            }
            catch (TransportException ex)
            {
                return "kind=" + ex.Kind;
            }
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private async Task<string> ScenarioU5()
    {
        var transport = new UnityWebRequestTransport(_host, new UnityWebRequestTransportOptions
        {
            MaxBufferedBytes = 64 * 1024
        });
        var session = await transport.OpenAsync(Request("/sse-big"), new TransportOptions(), CancellationToken.None);
        await Task.Delay(400);

        var buffer = new byte[4096];
        try
        {
            while (await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None) > 0)
            {
            }
            return "FAIL_no_overflow";
        }
        catch (TransportException ex)
        {
            return "kind=" + ex.Kind + "|msg=" + Trunc(ex.Message, 60);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }

    private async Task<string> ScenarioU6()
    {
        var session = await _transport.OpenAsync(Request("/sse-error"), new TransportOptions(), CancellationToken.None);
        var text = await ReadAllTextAsync(session.Body, CancellationToken.None);
        await session.DisposeAsync();
        return "status=" + session.StatusCode + "|body=" + Trunc(text, 60);
    }

    private async Task<string> ScenarioU7TlsAccept()
    {
        var options = new TransportOptions
        {
            Tls = new TlsOptions { ValidateCertificate = false }
        };
        var session = await _transport.OpenAsync(HttpsRequest("https://self-signed.badssl.com/"), options, CancellationToken.None);
        var buffer = new byte[64];
        await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
        int status = session.StatusCode;
        await session.DisposeAsync();
        return "status=" + status;
    }

    private async Task<string> ScenarioU7TlsDefault()
    {
        try
        {
            await _transport.OpenAsync(HttpsRequest("https://self-signed.badssl.com/"), new TransportOptions(), CancellationToken.None);
            return "FAIL_no_tls_error";
        }
        catch (TransportException ex)
        {
            return "kind=" + ex.Kind;
        }
    }

    private async Task<string> ScenarioU7Proxy()
    {
        var options = new TransportOptions
        {
            Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" }
        };
        try
        {
            await _transport.OpenAsync(Request("/sse"), options, CancellationToken.None);
            return "FAIL_no_unsupported";
        }
        catch (TransportException ex)
        {
            return "kind=" + ex.Kind;
        }
    }

    private async Task<string> ScenarioU8()
    {
        var sw = Stopwatch.StartNew();
        var session = await _transport.OpenAsync(Request("/sse-gzip"), new TransportOptions(), CancellationToken.None);
        var parse = await ParseAsync(session.Body, sw);
        await session.DisposeAsync();
        return "items=" + parse.Items.Count +
               "|first_ms=" + parse.FirstItemMs +
               "|data=" + string.Join("", parse.Items);
    }

    private async Task<string> ScenarioU9()
    {
        var observer = new ThreadRecordingObserver();
        var transport = new ObserverTransport(_transport, observer);
        var session = await transport.OpenAsync(Request("/sse"), new TransportOptions(), CancellationToken.None);

        int parseThread = -1;
        await Task.Run(async () =>
        {
            parseThread = Thread.CurrentThread.ManagedThreadId;
            await ParseAsync(session.Body, Stopwatch.StartNew());
        });

        await session.DisposeAsync();
        return "main=" + _mainThreadId +
               "|parse=" + parseThread +
               "|observer=" + observer.LastThreadId +
               "|chunks=" + observer.Chunks;
    }

    private async Task<string> ScenarioU10Error()
    {
        var options = new TransportOptions { Redirects = RedirectPolicy.Error };
        try
        {
            await _transport.OpenAsync(Request("/redirect"), options, CancellationToken.None);
            return "FAIL_no_redirect_error";
        }
        catch (TransportException ex)
        {
            return "kind=" + ex.Kind;
        }
    }

    private async Task<string> ScenarioU10Follow()
    {
        var sw = Stopwatch.StartNew();
        var options = new TransportOptions { Redirects = RedirectPolicy.Follow };
        var session = await _transport.OpenAsync(Request("/redirect"), options, CancellationToken.None);
        var parse = await ParseAsync(session.Body, sw);
        await session.DisposeAsync();
        return "status=" + session.StatusCode + "|items=" + parse.Items.Count;
    }

    private async Task<string> ScenarioU11()
    {
        var observer = new ThreadRecordingObserver();
        var transport = new ObserverTransport(_transport, observer);
        var session = await transport.OpenAsync(Request("/sse"), new TransportOptions(), CancellationToken.None);

        var buffer = new byte[64];
        await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
        session.Abort();
        int atAbort = observer.Chunks;
        await Task.Delay(1000);
        int after = observer.Chunks;
        try
        {
            await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None);
        }
        catch (Exception)
        {
        }
        await session.DisposeAsync();
        return "chunks_at_abort=" + atAbort + "|after_1s=" + after;
    }

    private static async Task<string> ReadAllTextAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256];
        var builder = new StringBuilder();
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
        }
        return builder.ToString();
    }

    private static TransportRequest HttpsRequest(string url)
    {
        return new TransportRequest("GET", url, new TransportHeaders(), ReadOnlyMemory<byte>.Empty);
    }

    private sealed class ThreadRecordingObserver : ITransportObserver
    {
        public int LastThreadId;
        public int Chunks;

        public ValueTask OnResponseAsync(TransportResponseInfo response, CancellationToken ct)
        {
            return default;
        }

        public ValueTask OnBodyChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct)
        {
            Chunks++;
            LastThreadId = Thread.CurrentThread.ManagedThreadId;
            return default;
        }
    }

    private static async Task<ParseResult> ParseAsync(Stream stream, Stopwatch sw)
    {
        var items = new List<string>();
        long firstMs = -1;
        await foreach (var item in SseParser.Create(stream).EnumerateAsync())
        {
            if (firstMs < 0)
            {
                firstMs = sw.ElapsedMilliseconds;
            }
            items.Add(item.Data);
        }
        return new ParseResult(items, firstMs);
    }

    private static TransportRequest Request(string path)
    {
        return new TransportRequest(
            "POST",
            "http://127.0.0.1:5199" + path,
            new TransportHeaders
            {
                ["accept"] = "text/event-stream",
                ["content-type"] = "application/json"
            },
            new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{}")));
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

    private sealed class ParseResult
    {
        public ParseResult(List<string> items, long firstItemMs)
        {
            Items = items;
            FirstItemMs = firstItemMs;
        }

        public List<string> Items { get; }
        public long FirstItemMs { get; }
    }
}
