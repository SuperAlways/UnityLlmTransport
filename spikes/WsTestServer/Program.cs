// Phase 0 Task 5 — 本地 ws echo 服务（brief Step 5.1 原文 + 两处已注释的功能性扩展）
// 用法: dotnet run --project spikes/WsTestServer
// 行为: 每个连接收一条文本消息 → 回 "echo:<原文>" → 服务端主动 NormalClosure 关闭
//
// 与 brief 的差异（仅两处，实测驱动）:
//   ① brief 原样代码在【客户端不完成 close 握手就断开】时，服务端主循环里的
//   ws.CloseAsync 抛未捕获 WebSocketException → 整个进程退出（.NET 10 实测两轮复现，
//   两种签名: ①"The remote party closed the WebSocket connection without completing
//   the close handshake." ②"An internal WebSocket error occurred ---> HttpListenerException(995)"）。
//   brief Step 5.2 的客户端（收完一条即 using Dispose，不回握手）恰好每次都触发它 →
//   Unity 手动矩阵每测一格就得重启服务。此处 per-connection try/catch 吞掉坏连接，
//   让服务在矩阵会话中存活；verbatim 版的崩溃证据完整保留在
//   docs/spike/websocket-matrix.md 与 .superpowers/sdd/phase0-task-5-report.md。
//   ②（2026-11-07，Phase 5 四版本矩阵实测驱动）连接处理从主循环内联 await 改为 per-connection
//   fire-and-forget 任务 + CloseAsync 带 5s 超时：原写法是单线程串行处理，一旦客户端连接后既不读也不关
//   （Phase 5 诊断中 Unity 2022.3 IL2CPP 迭代器缺陷导致的泄漏会话），CloseAsync(None) 永久挂起 →
//   accept 循环卡死 → 后续连接全被 http.sys 排队，客户端表现为 Connection reset / 握手悬挂，
//   整个矩阵被一个坏连接连带污染。改后坏连接只拖死自己，判据行为不变。
using System.Net;
using System.Net.WebSockets;
using System.Text;

var listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:5200/ws/");
listener.Start();
Console.WriteLine("WS echo: ws://127.0.0.1:5200/ws/");

while (true)
{
    var ctx = await listener.GetContextAsync();
    if (!ctx.Request.IsWebSocketRequest) { ctx.Response.StatusCode = 400; ctx.Response.Close(); continue; }
    _ = HandleConnectionAsync(ctx);
}

async Task HandleConnectionAsync(HttpListenerContext ctx)
{
    try
    {
        var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
        var reply = Encoding.UTF8.GetBytes("echo:" + text);
        await ws.SendAsync(reply, WebSocketMessageType.Text, true, CancellationToken.None);
        using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", closeCts.Token);
    }
    catch (Exception e)
    {
        Console.WriteLine($"conn dropped: {e.GetType().Name}: {e.Message}");
    }
}
