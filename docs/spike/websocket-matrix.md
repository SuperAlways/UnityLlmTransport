# WebSocket 本地/远程矩阵（Phase 0 Task 5）

> Spike 结论先行：**本地 ws echo 全链路成立**（.NET 10 `ClientWebSocket` ↔ `HttpListener` echo 服务：连接/收发/UTF-8 回环/Close 帧/400 分类全绿）；**brief 原样服务端代码有一个实测崩溃坑**——客户端不完成 close 握手就断开时服务端进程整体退出（已在提交版用 per-connection try/catch 治掉，证据保留）。
> **Unity 侧已补测：2021.3.45f1 / 6000.3.28f1 / 6000.6.8f1 的 Mono 与 IL2CPP 都能连本地 ws、收到 `echo:hello`、收 Close/回握手、Abort，并能连远程 `wss://`（Postman echo）；关代理 / WebGL 仍 pending。**

## 环境（实测时点）

- OS Windows 11；.NET SDK `10.0.301`（裸 `dotnet`）
- 系统代理**开**（同 Task 4：WinINET `ProxyServer=127.0.0.1:7897`，`ProxyOverride` 含 `127.*;localhost;<local>`）
- 服务端：`spikes/WsTestServer`（`HttpListener`，`ws://127.0.0.1:5200/ws/`，**免 URLACL 免提权直接可跑**，http.sys 内核监听 netstat 显示 PID 4 属正常）
- 客户端（本矩阵实测侧）：仓库外临时 .NET 10 控制台 `ClientWebSocket` 探针（`%TEMP%\ws-echo-client`，一次性，不入仓库）
- Unity 侧接线：`Phase0Runner`（三版本 Windows Player 自动跑）——Mono 与 IL2CPP 各实机一轮，C 表已按格回填

## 复现命令

```bash
# 终端 A：本地 ws echo
dotnet run --project spikes/WsTestServer -c Release

# 终端 B：.NET 探针（临时件复刻，逻辑=连→发→收 echo→收 Close→可选回握手/直接 Dispose）
# 本仓库不带探针源码；矩阵每格行为见下表"证据"列，五轮脚本已原样记录在 Task 5 报告
curl -s -i --noproxy '*' http://127.0.0.1:5200/ws/   # 非 WebSocket 请求 → 400
```

## 矩阵

固定表头：栈 | 网络 | 代理 | TLS | 结果 | 错误 | 证据

### A. .NET 10 控制台 `ClientWebSocket` × 本地 ws（已实测）

| 栈 | 网络 | 代理 | TLS | 结果 | 错误 | 证据 |
|---|---|---|---|---|---|---|
| ClientWebSocket(.NET10) | ws://127.0.0.1:5200/ws/ | system-proxy 开 | 无 | **OK** | – | 发 `hello` → `type=Text bytes=10 text=echo:hello`，connect=122ms（首连）/0-4ms（热连） |
| ClientWebSocket(.NET10) | 同上，收完即 Dispose（brief Step 5.2 客户端语义） | system-proxy 开 | 无 | **echo OK，但触发服务端崩溃（见 B-1）** | – | 客户端侧一切正常且收到 `type=Close closeStatus=NormalClosure closeDesc='bye'`；服务端 `CloseAsync` 抛未捕获异常**进程退出** |
| ClientWebSocket(.NET10) | 同上，UTF-8 中文 `你好，世界` | system-proxy 开 | 无 | **OK** | – | 回 `bytes=20 text=echo:你好，世界`（15B 原文+5B 前缀，UTF-8 无损回环） |
| ClientWebSocket(.NET10) | 同上，收 Close 后回 `CloseOutputAsync(NormalClosure)`（完整握手） | system-proxy 开 | 无 | **OK** | – | 客户端 `state=Closed`（握手闭环），服务端无异常日志 |
| ClientWebSocket(.NET10) | 同上，abrupt 两连发后第 3 连（提交版 try/catch 服务端） | system-proxy 开 | 无 | **OK** | – | 前 2 连 abrupt → 服务端日志 2 行 `conn dropped: WebSocketException: The remote party closed...`，第 3 连 `connect=0ms` 正常 → 服务存活 |
| ClientWebSocket(.NET10) | `ws://127.0.0.1:5299/ws/`（错误端口） | default options | 无 | **FAIL（可分类）** | `WebSocketException: Unable to connect... << HttpRequestException: 由于目标计算机积极拒绝 (127.0.0.1:5299)` | 2037-2151ms；挂 `Options.Proxy=null` 同样 2045ms/同错误 → ~2s 延迟与代理无关（loopback 拒连固有），错误分类=连接拒绝 |
| HttpListener 服务端 | 非 WebSocket GET（curl 明文） | – | 无 | **400** | – | `HTTP/1.1 400 Bad Request` + `Server: Microsoft-HTTPAPI/2.0`（http.sys 直拒，不崩服务） |
| ClientWebSocket(.NET10) | ws:// 好端口 × 系统代理 on | system-proxy 开 | 无 | **OK** | – | 好端口 7ms 连上；Refused 格 on/off 代理无差别 → 本格无法区分"走了代理"还是"bypass 直连"，按 Task 4 的 WinINET `127.*` bypass 判定推为直连（推定，非铁证） |

### B. brief 原样代码的实测坑（已复现两轮，写死免重查）

| 格 | 结果 | 错误 | 证据 |
|---|---|---|---|
| B-1 服务端 brief Step 5.1 原样 × 客户端 abrupt dispose | **服务端进程崩溃退出** | `WebSocketException: The remote party closed the WebSocket connection without completing the close handshake.`（客户端已收到 Close 帧才断）或 `WebSocketException: An internal WebSocket error occurred. ---> HttpListenerException(995): 由于线程退出或应用程序请求，已中止 I/O 操作。`（echo 后握手在途即断） | 两种签名均抛在服务端 `ws.CloseAsync`（brief 原样版主循环里那一行，即本仓库提交版 try/catch 所包住的调用），顶层无捕获 → 整进程退出；之后任何连接 `curl exit=7`（http_code=000，连接拒绝）。**根因：`CloseAsync` 在等服务端 close-ack 时客户端直接 Dispose 掐线；brief Step 5.2 的客户端恰好每次都这么干** |
| B-2 提交版修复 | per-connection `try/catch` 吞坏连接，服务存活 | – | A 表第 5 格：两连 abrupt 后服务照常服务；verbatim 版崩溃证据完整保留于本节 + Task 5 报告 |

### C. Unity `ClientWebSocket`（本地/远程/Close/Abort 已实测；仅关代理与 WebGL pending）

| 栈 | 网络 | 代理 | TLS | 结果 | 错误 | 证据 |
|---|---|---|---|---|---|---|
| ClientWebSocket(Unity 2021.3.45f1c2) | ws://127.0.0.1:5200/ws/ | 系统代理开 | 无 | **OK（Mono + IL2CPP）** | – | `type=Text bytes=10 text=echo:hello` |
| ClientWebSocket(Unity 6000.3.28f1) | ws://127.0.0.1:5200/ws/ | 系统代理开 | 无 | **OK（Mono + IL2CPP）** | – | 同上 |
| ClientWebSocket(Unity 6000.6.8f1) | ws://127.0.0.1:5200/ws/ | 系统代理开 | 无 | **OK（Mono + IL2CPP）** | – | 同上 |
| ClientWebSocket(Unity 三版本) | ws://127.0.0.1:5299/ws/（错误端口） | 系统代理开 | 无 | **FAIL（可分类）** | `WebSocketException: Unable to connect to the remote server` | 三版本一致 |
| ClientWebSocket(Unity 三版本) | ws://127.0.0.1:5199/sse（非 ws 端点） | 系统代理开 | 无 | **FAIL（可分类）** | `WebSocketException: Unable to connect to the remote server` | HTTP 状态未暴露，归为连接/握手失败 |
| ClientWebSocket(Unity 三版本) | wss://ws.postman-echo.com/raw | 系统代理开 | 系统信任 CA | **OK** | – | `type=Text bytes=5 text=hello` |
| ClientWebSocket(Unity 三版本) | 本地 ws | system-proxy 开/关 | – | **pending（关代理）** | – | 当前只测了系统代理开 |
| ClientWebSocket(Unity 三版本) | 本地 ws | – | 无 | Close/Abort | **OK** | 收 `NormalClosure/bye`，回握手 `CloseSent`；`Abort` 后 `state=Aborted` |
| WebGL | 本地 ws | – | 无 | **pending** | – | M2，未测 |
| WebGL 构建 | 本地 ws | – | 无 | **pending** | – | 预期失败，待实测确认 |

## 观察

1. **brief 服务端代码自带一个"自杀式"闭环坑**：brief 的服务端 + brief 的客户端一起跑，第一次连接测完服务端就死（B-1）。这不是玄学——`HttpListener.AcceptWebSocketAsync` 拿到的 `WebSocket.CloseAsync` 会等对端 close-ack，客户端（brief 客户端收完一条就 `using` Dispose）不回握手直接掐线 → 服务端 `WebSocketException` 未捕获 → 进程退出。**任何"客户端放弃连接是常态"的传输层设计都必须在服务端 per-connection 兜底**；对本项目 S3 的启示：LLM 网关侧（若有 ws 入口）同样要兜。
2. **Close 帧本身观测良好**：服务端 `NormalClosure("bye")` 在客户端 `ReceiveAsync` 处以 `type=Close closeStatus=NormalClosure closeDesc='bye'` 呈现；客户端回 `CloseOutputAsync` 后 `state=Closed`（完整握手闭环）。矩阵 Close 格的观测点就绪。
3. **UTF-8 无损**：`你好，世界` 15 字节回环成 20 字节 `echo:你好，世界`，中文无 mojibake。
4. **错误可分类**：连接拒绝 = `WebSocketException << HttpRequestException (SocketException 积极拒绝)`；非 ws 端点 = HTTP 400。失败归因四分法（TLS/代理/握手/超时）里"握手/连接"档已有基准样本；TLS/代理/超时三档等远程 wss 格回填。
5. **~2s 的 loopback 拒连延迟**：错误端口从 ConnectAsync 抛出到失败稳定落在 ~2s（2037/2045/2151ms，有无显式禁代理无差别）——不是代理差异，像 loopback 拒连的重试/backoff 固有延迟。Unity 侧若也见 ~2s 才报错，可对照此基准。
6. **HttpListener + ws 免提权可用**：`http://127.0.0.1:5200/ws/` 前缀无需 netsh URLACL 直接 `listener.Start()` 成功（与 Task 2 SSE 同机同环境一致）；http.sys 内核层监听（netstat PID=4）属正常现象。

## 待办（Unity 手动部分，brief Step 5.3；执行步骤见 `spikes/UnityUwrSpike/README-WS.md`）

- [x] 三版本 Mono/IL2CPP × 本地 ws：`recv type=Text bytes=10 text=echo:hello`
- [x] 三版本 × Close 帧观测 / 回握手 / Abort
- [x] 三版本 × 远程 wss（Postman echo）
- [ ] 系统代理关闭后重测（当前只测开）
- [ ] WebGL（M2）
- [x] 每格回填本矩阵
