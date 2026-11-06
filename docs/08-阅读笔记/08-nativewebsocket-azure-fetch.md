# 08 · 阅读笔记：NativeWebSocket 与 Azure fetch-event-source

> 日期：2026-11-06 · 只读参考仓库 · 证据标准：源码 file:line > 文档 > 推测
> 仓库：`refs/07-endel-nativewebsocket`（@bf3280c, 2026-10-26）、`refs/06-azure-fetch-event-source`（@a052949, 2023-02-02, @microsoft/fetch-event-source 2.0.1）

---

## 1. NativeWebSocket（endel）

### 基本信息与许可证
- Apache-2.0（`LICENSE:1-2`，双重版权 Endel Dreyer 2019 + Jiri Hybek 2018）；⚠️ `src/NativeWebSocket/NativeWebSocket.csproj:12` 的 PackageLicenseExpression 却写 `MIT`——两许可证都 vendor 安全，但引用时以 LICENSE 文件（Apache-2.0）为准并在 NOTICE 注明。
- 目标框架 `netstandard2.0;net6.0`，LangVersion 7.3（`NativeWebSocket.csproj:6,11`）→ Unity 2021 netstandard2.1 profile 直接可用，无外部 DLL。
- 形态：`IWebSocket` 接口 + 原生/WebGL 两实现 + jslib 桥，共 4 个 .cs + 2 个 js 插件文件；零依赖。

### 架构地图（原生平台）
- `WebSocket` 包装 `System.Net.WebSockets.ClientWebSocket`（`src/NativeWebSocket/WebSocket.cs:24,210`），`ConnectAsync(uri, token)`（:228）；headers 走 `Options.SetRequestHeader`（:216），子协议 `Options.AddSubProtocol`（:224）。
- **主线程 dispatch**：构造时捕获 `SynchronizationContext`（:48），后台 Receive 循环只入队，经 sync context 派发回主线程（`EnqueueMessage` :97-108，`DispatchQueuedMessages` :137-170）。事件（OnOpen/OnClose）以 null 占位与消息同队列，保证到达顺序（:30-33 注释，2.0.7 修复了真实乱序 bug，见 `CHANGELOG.md` 2.0.7）。无 sync context 环境手动 `DispatchMessageQueue()`（:192-196）。
- **发送**：按 text/binary 各一条队列 + `isSending` 标志串行化（:40-43,286-294）；同步快路径避开 async 状态机（`SendAndDrainAsync` :397-432；net6 用 ValueTask :323-395）。⚠️ 排队时返回 `Task.CompletedTask`（:290-291）——调用方无法 await 到真正发出，fire-and-forget 语义。
- **接收**：单帧快路径直接 BlockCopy 免 MemoryStream（:483-486），多帧才累积（:491-504）；固定 64KB 缓冲（:467）。⚠️ 多帧累积无上限，恶意/超长消息可无界占内存。
- **关闭/取消**：`CancelConnection` = CancellationTokenSource.Cancel（:198-201）；收到对端 close 帧后用 `CloseOutputAsync` **回显同码**完成握手（`EchoCloseFrame` :536-551，2.0.5 修复，服务端不再把主动关闭误判成掉线）；close code 1000-4999 全保留不再折叠成 1004（`WebSocketTypes.cs:9-19`）。
- ⚠️ 错误分类弱：Receive 循环异常被静默吞掉只发 OnClose(Abnormal)（:508-511），OnError 仅连接失败时报且是 string（`IWebSocket.cs:7`）——不适合直接当 LLM 错误分类层。

### WebGL 分支 / IL2CPP·AOT
- WebGL 实现 `DllImport("__Internal")` 直呼 jslib（`integrations/Unity/WebSocketWebGL.cs:16-29`），浏览器 `close()` 只收 1000/3000-4999，取消时发 1000 但对 C# 侧仍报 Abnormal（:107-115,193，2.0.8 修复）。
- AOT 齐备：JS→C# 回调全部 `[MonoPInvokeCallback]`（`integrations/Unity/WebSocketFactoryWebGL.cs:73,84,98,110`），字节用 `Marshal.Copy`（:92）；jspre 处理 Unity 6 wasm `dynCall_*` 表（`NativeWebSocket.jspre:17-37`）；jslib 侧 `binaryType='arraybuffer'`（`NativeWebSocket.jslib:145`）。无反射，IL2CPP 裁剪友好（链接保留靠 DllImport+委托自然存活）。
- ⚠️ WebGL 无法设自定义 header（浏览器 WS API 只有 url+protocols）——需要 Authorization 的 WS 通道在 WebGL 得走 query/子协议；README 也警告勿裸拷源码，需走 UPM 的编译期条件编译变换（`README.md:14-18`）。
- 维护：活跃（2026-10-26 仍在发 2.0.8），changelog 记录 2.0.5-2.0.8 三次真实协议级 bug 修复，质量信号好。已进入 Colyseus 生态（PackageId `Colyseus.NativeWebSocket`）。

## 2. Azure fetch-event-source

### 基本信息与许可证
- MIT（`LICENSE:1`，Microsoft）；纯 TS 三文件（`fetch.ts`/`parse.ts`/`parse.spec.ts` 353 行测试）；上游最后 commit 2023-02-02，**实质停维护**（repo 已归档），但算法即 spec，不影响参考价值。

### SSE 解析算法（`src/parse.ts`，C# 移植核心）
- `getBytes`：`reader.read()` 循环、逐 chunk 回调（:22-28）——真流式，不等整响应。
- `getLines`：**字节级**增量分行（:43-109）——状态 = buffer+position+fieldLength+discardTrailingNewline，跨 chunk 保留半行并用 `subarray(lineStart)` 重定基免拷贝（:103-108）；\r\n 用 fallthrough+discardTrailingNewline 处理（:81-84）；首个冒号定 fieldLength（:75-79）。字节级切行天然 UTF-8 安全（多字节序列不含 0x0A/0x0D）。
- `getMessages`：空行 = 消息边界并回调（:129-132）；`fieldLength > 0` 天然排除注释行与无值行（:133）；`data` 多次出现以 `\n` 拼接（:144-147）；`id`/`retry` 旁路回调（:151-158）；field/value 分段解码，valueOffset 跳过 `: `（:136-138）。逐字段对照 WHATWG event-stream interpretation（:135 引 spec）。
- 复用接口形状：`getLines(getMessages(...))` 三层闭包组合（`src/fetch.ts:113`），分"字节→行→消息"三级，正好映射我们"传输→行缓冲→SSE 事件"分层。

### 编排（`src/fetch.ts`）与 EventSource 差异
- 任意 method/headers/body（`FetchEventSourceInit extends RequestInit` :8，README:5-13 列出浏览器 EventSource 的 GET-only/无 header/无重试控制三大限制）；自定义 `fetch` 可注入（:53,100）。
- `onopen` 钩子先验 content-type 再消费流（:111,149-154）——网关/代理错误在解析前被拦截，值得照搬。
- abort 两级：外部 signal 监听 → dispose+resolve（不抛错，:95-98）；每次 create 新 AbortController（:103）。
- 重试策略：`onerror` 返回间隔 ms 或 rethrow 判致命（:128-141）；`last-event-id` 头随重放更新/删除（:113-123）；页面隐藏自动断连重开（:75-84，游戏内不需要）。

## 3. 在 M1/M2 的定位

| | M1（Windows 桌面，UWR/HttpClient，阶段 0-4） | M2（WebGL，`.jslib` + fetch） |
|---|---|---|
| fetch-event-source | **核心参考**：parse.ts 状态机 → C# 移植为我们阶段 1 的纯 .NET SseParser（可直接照 parse.spec.ts 353 行补测试） | 同一 C# parser 复用；fetch.ts 的 onopen/onerror/onclose 回调形状作为 WebGL fetch 桥的输出接口 |
| NativeWebSocket | 阶段 5 前不进 M1（WebSocket 仅按 provider 需求）；若 provider 走 WS，桌面端直接 vendor 原生实现 | **首选候选**：原生/WebGL 双实现 + `IWebSocket` 已替我们做好平台开关；其 MonoPInvokeCallback/jslib 桥 = 自研 fetch 桥的 AOT 样板 |

## 4. 结论与评分

| 仓库 | vendor | wrap | reference | avoid | 判定 |
|---|---|---|---|---|---|
| 07 NativeWebSocket | **4** | 2 | **4** | 1 | **vendor 候选（挂起至阶段 5）**。Apache-2.0 + netstandard2.0 + 零依赖 + 活跃维护 + AOT 齐备，vendor 成本≈零；唯一未决项是 M1 是否真的需要 WS。若不 vendor，其 sync-context 派发/关停握手回显/关闭码保留三处也值得照抄。 |
| 06 fetch-event-source | 1 | 1 | **5** | 1 | **reference（算法移植）**。TS 源无法直接复用；但 parse.ts 是能找到的最干净的 WHATWG 兼容 SSE 状态机，MIT 允许任意移植；上游停维护对"只取算法"无影响。 |

**风险备注**：① NativeWebSocket 多帧接收无大小上限、发送排队不可 await、错误分类为 string——收编后需在其上补我们自己的错误分类与取消语义；② 两处 LICENSE 口径（Apache vs csproj MIT）vendor 时按 Apache-2.0 留 NOTICE；③ fetch-event-source 无 BOM 剥离/多字节跨界测试，C# 移植时需在阶段 1 单测补齐。
