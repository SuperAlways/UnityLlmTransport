using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public sealed class Phase0Runner : MonoBehaviour
{
    private readonly StringBuilder _results = new StringBuilder();

    private IEnumerator Start()
    {
        _results.AppendLine("unity_version=" + Application.unityVersion);
        yield return RunUwr("sse", "http://127.0.0.1:5199/sse", false, 0);
        yield return RunUwr("sse-error", "http://127.0.0.1:5199/sse-error", true, 30);
        yield return RunUwr("wrong-port", "http://127.0.0.1:5198/sse", false, 30);
        yield return RunUwr("sse-gzip", "http://127.0.0.1:5199/sse-gzip", false, 0);
        yield return RunAbort();
        yield return RunWs("ws-local", "ws://127.0.0.1:5200/ws/");
        yield return RunWs("ws-wrong-port", "ws://127.0.0.1:5299/ws/");
        yield return RunWs("ws-non-ws", "ws://127.0.0.1:5199/sse");
        yield return RunWsClose();
        yield return RunWsAbort();
        yield return RunWs("ws-remote", "wss://ws.postman-echo.com/raw");
        yield return RunHttps("tls-normal", "https://example.com/", false);
        yield return RunHttps("tls-selfsigned-accept", "https://self-signed.badssl.com/", true);
        yield return RunHttps("tls-selfsigned-default", "https://self-signed.badssl.com/", false);

        var path = Path.Combine(Application.persistentDataPath, "phase0-results.txt");
        File.WriteAllText(path, _results.ToString());
        Debug.Log("PHASE0_RESULTS_PATH=" + path);
        foreach (var line in _results.ToString().Split('\n'))
            Debug.Log("PHASE0 " + line);

        yield return new WaitForSeconds(1f);
        Application.Quit();
    }

    private IEnumerator RunUwr(string name, string url, bool readErrorBody, int timeout)
    {
        var request = new UnityWebRequest(url, "POST");
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
        request.SetRequestHeader("Content-Type", "application/json");
        request.SetRequestHeader("Accept", "text/event-stream");
        request.timeout = timeout;

        StreamingDownloadHandler streaming = null;
        DownloadHandlerBuffer buffer = null;
        if (readErrorBody)
        {
            buffer = new DownloadHandlerBuffer();
            request.downloadHandler = buffer;
        }
        else
        {
            streaming = new StreamingDownloadHandler();
            request.downloadHandler = streaming;
        }

        var started = Time.realtimeSinceStartupAsDouble;
        yield return request.SendWebRequest();
        var elapsed = Time.realtimeSinceStartupAsDouble - started;

        var body = buffer != null ? Truncate(buffer.text, 160) : "";
        var line = $"uwr|{name}|result={request.result}|code={request.responseCode}|" +
                   $"error={Truncate(request.error, 80)}|callbacks={(streaming != null ? streaming.CallbackCount : -1)}|" +
                   $"first={(streaming != null && streaming.Chunks.Count > 0 ? streaming.Chunks[0].t : -1):F3}|elapsed={elapsed:F3}|body={body}";
        _results.AppendLine(line);
        request.Dispose();
    }

    private IEnumerator RunAbort()
    {
        var request = new UnityWebRequest("http://127.0.0.1:5199/sse", "POST");
        request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
        request.SetRequestHeader("Accept", "text/event-stream");
        request.timeout = 0;
        var handler = new StreamingDownloadHandler();
        request.downloadHandler = handler;

        request.SendWebRequest();
        yield return new WaitForSeconds(0.15f);
        request.Abort();
        var atAbort = handler.CallbackCount;
        yield return new WaitForSeconds(1f);
        var after = handler.CallbackCount;

        _results.AppendLine($"uwr|abort|result={request.result}|code={request.responseCode}|" +
                            $"error={Truncate(request.error, 80)}|at_abort={atAbort}|after_1s={after}|callbacks={after}");
        request.Dispose();
    }

    private IEnumerator RunWs(string name, string url)
    {
        var task = RunWsTask(url);
        yield return new WaitUntil(() => task.IsCompleted);

        string detail;
        if (task.IsFaulted)
        {
            var e = task.Exception?.InnerException ?? task.Exception;
            detail = $"error={e?.GetType().Name}: {Truncate(e?.Message, 120)}";
        }
        else if (task.IsCanceled)
        {
            detail = "cancelled";
        }
        else
        {
            detail = task.Result;
        }

        _results.AppendLine($"ws|{name}|{detail}");
    }

    private async Task<string> RunWsTask(string url)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), timeout.Token);
        var payload = Encoding.UTF8.GetBytes("hello");
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, timeout.Token);
        return $"type={result.MessageType}|bytes={result.Count}|text={Encoding.UTF8.GetString(buffer, 0, result.Count)}";
    }

    private IEnumerator RunWsClose()
    {
        var task = RunWsCloseTask();
        yield return new WaitUntil(() => task.IsCompleted);
        _results.AppendLine("ws|ws-close|" + (task.IsFaulted ? ErrorText(task) : task.Result));
    }

    private async Task<string> RunWsCloseTask()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:5200/ws/"), timeout.Token);
        var payload = Encoding.UTF8.GetBytes("hello");
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);

        var buffer = new byte[4096];
        var text = new StringBuilder();
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) break;
            text.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
        } while (!result.EndOfMessage);

        var close = await ws.ReceiveAsync(buffer, timeout.Token);
        var closeText = close.MessageType == WebSocketMessageType.Close
            ? $"closeStatus={close.CloseStatus}|desc={close.CloseStatusDescription}"
            : $"unexpected={close.MessageType}";
        if (close.MessageType == WebSocketMessageType.Close && ws.State == WebSocketState.CloseReceived)
            await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client bye", timeout.Token);
        return $"echo={text}|{closeText}|finalState={ws.State}";
    }

    private IEnumerator RunWsAbort()
    {
        var task = RunWsAbortTask();
        yield return new WaitUntil(() => task.IsCompleted);
        _results.AppendLine("ws|ws-abort|" + (task.IsFaulted ? ErrorText(task) : task.Result));
    }

    private async Task<string> RunWsAbortTask()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:5200/ws/"), timeout.Token);
        var payload = Encoding.UTF8.GetBytes("hello");
        await ws.SendAsync(payload, WebSocketMessageType.Text, true, timeout.Token);
        var buffer = new byte[4096];
        await ws.ReceiveAsync(buffer, timeout.Token);
        ws.Abort();
        return $"abortedState={ws.State}";
    }

    private IEnumerator RunHttps(string name, string url, bool acceptSelfSigned)
    {
        var request = UnityWebRequest.Get(url);
        request.timeout = 20;
        request.downloadHandler = new DownloadHandlerBuffer();
        if (acceptSelfSigned) request.certificateHandler = new AcceptAllCertificateHandler();

        var started = Time.realtimeSinceStartupAsDouble;
        yield return request.SendWebRequest();
        var elapsed = Time.realtimeSinceStartupAsDouble - started;
        _results.AppendLine($"tls|{name}|result={request.result}|code={request.responseCode}|" +
                            $"error={Truncate(request.error, 100)}|elapsed={elapsed:F3}");
        request.Dispose();
    }

    private static string ErrorText(Task task)
    {
        var e = task.Exception?.InnerException ?? task.Exception;
        return $"error={e?.GetType().Name}: {Truncate(e?.Message, 120)}";
    }

    private static string Truncate(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var s = value.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }
}

public sealed class AcceptAllCertificateHandler : CertificateHandler
{
    protected override bool ValidateCertificate(byte[] certificateData) => true;
}
