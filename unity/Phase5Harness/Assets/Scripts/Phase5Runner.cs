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
        // 会话契约：ReceiveAsync 每会话只允许一次枚举（Task 2 单枚举守卫），
        // echo 与 Close 必须从同一次枚举里先后取出；两次 ReceiveOne 会得 Protocol 错。
        string echo = null;
        WebSocketMessage close = null;
        await foreach (var message in session.ReceiveAsync(cts.Token))
        {
            if (message.Type == WebSocketMessageType.Close)
            {
                close = message;
                break;
            }
            echo = Encoding.UTF8.GetString(message.Payload.ToArray());
        }
        if (echo == null)
        {
            throw new InvalidOperationException("no echo message before close");
        }
        if (close == null)
        {
            throw new InvalidOperationException("no WebSocket close received");
        }
        var status = close.CloseStatus.HasValue ? close.CloseStatus.Value.ToString() : "none";
        await session.DisposeAsync();
        return "echo=" + echo + "|type=" + close.Type + "|close=" + status;
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
