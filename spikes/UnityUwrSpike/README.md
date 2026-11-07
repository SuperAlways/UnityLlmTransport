# Unity UWR 流式 Spike（Phase 0 · Task 3）

回答一个问题：**Unity 2021.3 的 `UnityWebRequest` + `DownloadHandlerScript` 能不能拿到逐块流式回包？**
（首块时间 vs 总时长、`ReceiveData` 回调次数、Abort 行为、错误分类。）

本目录**只提交资产**（脚本 + link.xml + 本 README），不提交 Unity 工程。Unity 工程建在仓库外、由人手动创建（见下）。

## 资产

| 文件 | 作用 |
|---|---|
| `Assets/Scripts/StreamingDownloadHandler.cs` | `DownloadHandlerScript` 子类，记录 `ReceiveData` 每次回调的时间戳（相对启动）与字节数 |
| `Assets/Scripts/UwrSpike.cs` | `MonoBehaviour` runner：POST `{}` + `Accept: text/event-stream` 到本地 SSE 服务，结束时把 `result`/`code`/`error`/回调数/逐块时间打到 Console；`AbortNow()` 可在运行中调用 |
| `Assets/Scripts/Phase0Runner.cs` | 三版本自动矩阵 runner：UWR/SSE/Abort/gzip + WS（本地/错误/Close/Abort/远程 wss）+ TLS 三格，写 `phase0-results.txt` 后自动退出 |
| `Assets/Editor/Phase0Build.cs` | batchmode 构建入口：创建场景 + `BuildMono` / `BuildIl2cpp` |

`StreamingDownloadHandler.cs` / `UwrSpike.cs` 与 brief（`.superpowers/sdd/phase0-task-3-brief.md` Step 3.1/3.2）逐字一致；`Phase0Runner.cs` / `Phase0Build.cs` 来自多版本扩展轮（SOP §2，先在仓库外工程 `D:\UnitySpikes\Uwr2021` 实跑验证后回收入库，未改逻辑）。

## 前置：本地 SSE 测试服务（Task 2 产物）

服务在 `spikes/SseTestServer/`，监听 `http://127.0.0.1:5199/`，三个端点：

| 端点 | 行为 |
|---|---|
| `/sse` | 正常 SSE：5 个 `data:` 块（"你好，世界"逐字），每块间隔 100ms，末尾 `data: [DONE]` |
| `/sse-error` | HTTP 500 + 错误体 `{"error":"boom"}` |
| `/sse-gzip` | **Task 3 新增**：与 `/sse` 相同的 5 个块，但整条响应 gzip 压缩（`Content-Encoding: gzip`）。用于验证 gzip 下 UWR 是否仍逐块回调（注意：UWR 不自动解 gzip，handler 记到的是压缩字节，看的是**时序**不是内容） |

启动（.NET 10 SDK）：

```bash
cd spikes/SseTestServer
dotnet run -c Release
```

## Unity 工程创建（人手动，一次性）

Unity Hub 不允许在非空目录建工程，所以先把资产挪出去再建：

1. 把本目录的 `Assets/` 临时移走（比如移到 `%TEMP%\UnityUwrSpikeAssets`）；
2. Unity Hub → New project → **2021.3 LTS** → 3D 模板 → 项目路径选 `spikes/UnityUwrSpike`；
3. 把第 1 步移走的 `Assets/Scripts/` 原样移回来（Library/ProjectSettings 等已生成的 Unity 文件不动，仓库 `.gitignore` 已忽略它们）；
4. 切回 Unity 等脚本编译通过。

## 手动执行清单（brief Step 3.3）

1. 新建场景，创建空 GameObject，挂 `UwrSpike` 组件；
2. 启动 Task 2 服务（上面的 `dotnet run`），进 Play；
3. 记录 Console：`result` / `code` / `error` / `callbacks`（回调次数）/ `first`（首块时间）/ 逐块 `chunk t=... bytes=...`，以及总时长；
4. 运行中调用 `AbortNow()`（可加个临时按钮/按键），继续观察 2 秒，记录是否还有新增 chunk；
5. 把 URL 改成错误端口（如 `http://127.0.0.1:5198/sse`），记录 `result` / `code` / `error`；
6. 把 URL 改成 `http://127.0.0.1:5199/sse-error`，记录错误体是否到达 handler；
7. 把 URL 改成 `http://127.0.0.1:5199/sse-gzip`，记录回调次数与逐块时序（对照 `/sse`）。

## Expected（brief 判据）

- `callbacks > 1` 且首块时间明显小于总时长 → 逐块流式成立；
- Abort 后 2 秒内无新增有效 chunk（或记录残留数）；
- 错误端口：`result=ConnectionError`、`code=0`、`error` 非空；
- `/sse-error`：`code=500`，错误体可从 handler/下载器读到。

## 实测数据落盘

实测结果已写入 `docs/spike/uwr-streaming.md`（brief Step 3.4）与 `docs/spike/websocket-matrix.md` / `il2cpp.md` / `proxy-tls-matrix.md`；
原始运行结果留档在 `docs/spike/evidence/<版本>-results.txt`。

`Phase0Runner.cs` / `Phase0Build.cs` 已随仓库版本化（`Assets/Scripts/`、`Assets/Editor/`）：Unity 工程建在仓库外（如 `D:\UnitySpikes\Uwr2021`，Hub 不允许在非空目录建工程），建好后把这两个文件连同 `Assets/Scripts/`、`Assets/link.xml` 拷进去即可跑全自动矩阵（SOP 见 `docs/SOP/Unity多版本通讯测试-SOP-2026-11-06.md`）。
