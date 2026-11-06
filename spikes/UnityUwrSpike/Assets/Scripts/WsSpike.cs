// Phase 0 Task 5 — Unity ClientWebSocket spike（brief Step 5.2 原文）
// 目标平台: Unity 2021.3（Mono；IL2CPP/WebGL 另列矩阵格，未实测）
// 注意: Run 是 async Task，需从 MonoBehaviour 入口驱动（调用法见 README-WS.md）
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public sealed class WsSpike : MonoBehaviour
{
    public async Task Run(string url)
    {
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new System.Uri(url), CancellationToken.None);
        var bytes = Encoding.UTF8.GetBytes("hello");
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        var buffer = new byte[4096];
        var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
        Debug.Log($"recv type={result.MessageType} bytes={result.Count} " +
                  $"text={Encoding.UTF8.GetString(buffer, 0, result.Count)}");
    }
}
