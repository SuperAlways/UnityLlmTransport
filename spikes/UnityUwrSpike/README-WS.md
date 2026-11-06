# Unity ClientWebSocket Spike（Phase 0 · Task 5）

回答一个问题：**Unity 2021.3 的 `System.Net.WebSockets.ClientWebSocket` 能不能连本地 ws echo 服务并完成一发一收？**
（连接/收发/收到服务端 Close 的行为、错误分类、IL2CPP/WebGL 支持等级。）

本目录**只提交资产**（脚本 + 本 README），不提交 Unity 工程。Unity 工程由人手动创建/复用（见下）。
**本次（Task 5）只完成服务端 + .NET 控制台客户端实测；Unity 实机结果回填到 `docs/spike/websocket-matrix.md`，未跑的格子一律标 pending，不编造。**

## 资产

| 文件 | 作用 |
|---|---|
| `Assets/Scripts/WsSpike.cs` | `MonoBehaviour`：`Run(url)` 连 ws → 发 `"hello"` → 收一条 → `Debug.Log($"recv type=... bytes=... text=...")` |

代码与 brief（`.superpowers/sdd/phase0-task-5-brief.md` Step 5.2）逐字一致。
**`Run` 是 `async Task` 不是协程**——需要 MonoBehaviour 入口驱动，两种接法：

```csharp
// A. 丢线程池（简单，但 Debug.Log 在后台线程打——2021.3 的 Debug.Log 本身线程安全，Console 能看到）
private void Start() => _ = Run("ws://127.0.0.1:5200/ws/");

// B. 包协程保持主线程（推荐）
private IEnumerator Start()
{
    var task = Run("ws://127.0.0.1:5200/ws/");
    yield return new WaitUntil(() => task.IsCompleted);
    if (task.IsFaulted) Debug.LogException(task.Exception);
}
```

## 前置：本地 ws echo 服务（Task 5 产物）

服务在 `spikes/WsTestServer/`（`HttpListener`，.NET 10），监听 `ws://127.0.0.1:5200/ws/`：

- 每个连接收**一条**文本消息 → 回 `echo:<原文>` → **服务端主动 `NormalClosure("bye")` 关闭**（所以客户端第二次 `ReceiveAsync` 会收到 Close 帧，这是矩阵 Close 格的观测点）；
- 非 WebSocket 请求 → HTTP 400；
- 单帧 4096 字节缓冲（长消息会截断——spike 不测分片）；
- ⚠️ **brief 原样服务端代码有一个实测崩溃坑**：客户端收完不回 close 握手直接断（brief 客户端恰好如此）→ 服务端 `CloseAsync` 抛未捕获 `WebSocketException` → **进程退出**（.NET 10 两轮复现，详见 `docs/spike/websocket-matrix.md` B-1）。**提交版已加 per-connection try/catch**，矩阵会话中服务可存活——Unity 手动测试请用提交版跑 `dotnet run -c Release`。

启动（.NET 10 SDK）：

```bash
cd spikes/WsTestServer
dotnet run -c Release
```

## Unity 工程创建（人手动，一次性）

可复用 Task 3 建好的 `spikes/UnityUwrSpike` Unity 工程（若还在）：把本目录新增的 `Assets/Scripts/WsSpike.cs` 放进工程即可，切回 Unity 等编译。
首次建工程步骤同 `README.md`（移走 Assets → Hub 建 2021.3 工程 → 移回）。

## 手动执行清单（brief Step 5.3 矩阵 Unity 侧）

1. 场景建空 GameObject 挂 `WsSpike`（或临时场景直接挂）；启动 WsTestServer，进 Play；
2. **本地 ws**：`Run("ws://127.0.0.1:5200/ws/")`，记录 Console：`recv type=... bytes=... text=...`（期望 `type=Text bytes=10 text=echo:hello`，.NET 10 控制台侧实测口径），以及是否随后收到 `type=Close`；
3. **错误端口**：URL 改 `ws://127.0.0.1:5299/ws/`（服务没起），记录异常类型/消息（`WebSocketException`/`HttpRequestException`？耗时？）；
4. **非 ws 端点**：URL 指向 `http://127.0.0.1:5199/sse`（Task 2 SSE 服务，不 upgrade），记录错误分类；
5. **Close/Abort**：在收发后调 `ws.CloseAsync(...)` 与异常路径 `ws.Abort()` 各试一次，记录行为差异；
6. **系统代理**：系统代理开/关各跑一遍第 2 步（对照矩阵代理格——ClientWebSocket 默认 `UseProxy` 行为待实测）；
7. **IL2CPP**：切 IL2CPP 构建跑第 2 步，记录能否编译/能否连（已知文献警告 IL2CPP 不支持 `System.Net.WebSockets`，以实测为准）；
8. **WebGL**：切 WebGL 构建跑第 2 步，记录编译期失败信息（浏览器无原始 socket，ClientWebSocket 预期不可用——实测确认）。

每格结果回填 `docs/spike/websocket-matrix.md`（固定表头：栈 | 网络 | 代理 | TLS | 结果 | 错误 | 证据）。

## Expected（brief 判据）

- 本地 ws 成功：`recv type=Text bytes=10 text=echo:hello`（"echo:hello" 共 10 字节）；
- 远程 wss 成功/失败原因明确（TLS/代理/握手/超时）；
- 本地与远程分开记录。

## 实测数据落盘

跑完后把实测数据写入 `docs/spike/websocket-matrix.md`（brief Step 5.4）。**Unity 侧当前全部 pending**——.NET 控制台侧的本地 ws 实测已先行完成（见矩阵）。
