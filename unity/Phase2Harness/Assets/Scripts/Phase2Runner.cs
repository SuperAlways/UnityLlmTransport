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
