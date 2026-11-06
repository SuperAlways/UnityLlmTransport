# Phase 0 Spike 设计 spec · Unity 通讯事实确认（2026-11-06）

> **状态**：设计稿，按推荐方案直接进入 plan。
> **阶段**：Phase 0（Spike）。
> **输入**：`docs/00-核心需求.md`、`03-pi通讯需求矩阵.md`、`04-Unity-AI-mod通讯问题清单.md`、`09-参考项目评估矩阵.md`、`10-架构设计.md`、`11-开发阶段划分.md`。
> **目标**：用最小实验确认 Unity/2.1 的通讯事实，决定后续实现路线；**不写生产代码**。
> **时间盒**：2–3 天；每项实验有明确成功/失败判据。

---

## 1. 背景

架构已定：`IStreamingHttpTransport` + UWR/HttpClient/WebSocket 适配器 + vendor SseParser。但这些决定依赖若干**未验证的 Unity 事实**：

- UWR 的 `DownloadHandlerScript` 是否逐帧增量回调？
- `DownloadHandlerScript` 收到的数据是否已解 gzip？
- `Abort()` 后是否还有残留回调？
- 连接失败时 `result` / `responseCode` / `error` 的实际组合？
- Unity 的 UWR、HttpClient、ClientWebSocket 在代理/TLS/本地/远程下的差异？
- `System.Net.ServerSentEvents` 能否在 netstandard2.1 编译？
- IL2CPP 是否会剥离 `DownloadHandlerScript` 的虚方法？

Phase 0 只回答这些事实，不做架构扩展。

---

## 2. 问题与假设

| ID | 假设 | 验证方式 |
|---|---|---|
| H1 | `SseParser` 可 vendor 到 netstandard2.1 | 编译 + 关键边界测试 |
| H2 | UWR + `DownloadHandlerScript` 会逐帧增量回调 | Unity 实机/编辑器计数 + 时间戳 |
| H3 | `ReceiveData` 收到的数据已由 Unity 解 gzip | gzip SSE 测试 + 字节对比 |
| H4 | `Abort()` 能在一帧内停止连接，残留回调可丢弃 | 取消实验 + 计数 |
| H5 | `responseCode=0` 时 `result`/`error` 足以分类 | 断网/错端口实验 |
| H6 | UWR 只支持系统代理；HttpClient 可显式代理 | 代理矩阵 |
| H7 | 本地 `ws://` 可用；远程 `wss://` 需单独验证 | WebSocket 矩阵 |
| H8 | IL2CPP 可用 `link.xml` 保住 handler 重写 | IL2CPP 构建/运行 |
| H9 | `ChannelReadStream` 能把 UWR 回调桥成 SseParser 的 `Stream` | 最小桥接实验 |

---

## 3. 实验矩阵

### S0-1 · SseParser 在 netstandard2.1 编译

**做法**

- 在 `spikes/SseCompile/` 建 netstandard2.1 class library；
- 从 `refs/01-.../src/libraries/System.Net.ServerSentEvents` 拷贝：
  - `SseParser*.cs`、`SseItem.cs`、`ArrayBuffer.cs`（sparse checkout 未含，需补）、Helpers；
- 降级 C# 11/12 语法（`u8` 字面量、集合表达式）；
- 补 `IsExternalInit`；
- 跑关键边界测试（或移植官方测试子集）。

**成功判据**

- `dotnet build` 0 错误；
- 关键测试通过：CR/LF/CRLF、多行 data、注释、`[DONE]`、跨 chunk、EOF 半截；
- 记录：需要的语法改动、依赖、无法移植的 API。

**产出**

- `docs/spike/sse-compile.md`；
- `spikes/SseCompile/`（不合并生产）。

---

### S0-2 · UWR 真流式 / gzip / Abort

**做法**

- Unity 2021.3 最小项目 `spikes/UnityUwrSpike/`：
  - 一个 MonoBehaviour，`UnityWebRequest.Post` + `StreamingDownloadHandler : DownloadHandlerScript`；
  - `ReceiveData` 记录：调用次数、字节数、时间戳、首字节延迟；
  - 本地 SSE 服务（`spikes/SseTestServer/`，.NET 控制台，支持普通/gzip/慢速/异常/长流）；
  - `Abort()` 后继续记录 1–2 秒回调；
  - 错误端口/断网，记录 `result`、`responseCode`、`error`。

**成功判据**

- 流式：多次 `ReceiveData`，首块早于请求完成；
- gzip：字节可直接按 UTF-8/SSE 解析，或明确需要解压；
- Abort：连接停止，残留回调可被状态位丢弃；
- 错误：能区分 Connect / HttpStatus / Protocol。

**产出**

- `docs/spike/uwr-streaming.md` + 原始日志/fixture。

---

### S0-3 · 代理 / TLS 矩阵

**做法**

- 三个栈：UWR、HttpClient、ClientWebSocket；
- 网络：本地 / 远程；
- 代理：无 / 系统 / 显式（HttpClient 的 `WebProxy`；UWR 只测系统）；
- TLS：正常证书 / 自签 / 错误证书；
- 记录：连接结果、错误类型、`result`/`responseCode`、耗时。

**成功判据**

- 明确每个栈的支持矩阵；
- 明确“本地通、远程不通”的具体原因归属（TLS/代理/DNS/超时）；
- 明确 UWR 显式代理不可行的结论（或例外）。

**产出**

- `docs/spike/proxy-tls-matrix.md`。

---

### S0-4 · WebSocket 本地/远程

**做法**

- 复用 `说书人测试/…/PerceptionWsClient.cs` 的最小结构（只读参考，模式可借用）；
- 本地 `ws://127.0.0.1` + 简单 echo 服务；
- 远程 `wss://`（选择一个稳定的公共 echo 或自建自签 wss）；
- 测试：连接、发送/接收、Close、取消、代理、IL2CPP。

**成功判据**

- 本地 ws：可用；
- 远程 wss：成功/失败及错误类型（TLS/代理/握手/超时）；
- 明确支持等级：本地/远程/IL2CPP/WebGL 分开。

**产出**

- `docs/spike/websocket-matrix.md`。

---

### S0-5 · IL2CPP / link.xml

**做法**

- 用 S0-2 的 Unity 项目做 IL2CPP 构建；
- 加 `link.xml` / `[Preserve]` 保护 `StreamingDownloadHandler.ReceiveData/CompleteContent`；
- 运行同样的 SSE 测试。

**成功判据**

- 重写方法未被剥离；
- 流式/Abort 行为与 Mono 一致，或记录差异。

**产出**

- `docs/spike/il2cpp.md`。

---

### S0-6 · ChannelReadStream 桥接

**做法**

- 用 S0-2 的 `ReceiveData` 写入有界 `Channel<byte[]>`；
- 实现最小 `ChannelReadStream : Stream`；
- 喂给 vendor `SseParser`；
- 测试跨 chunk、gzip、Abort。

**成功判据**

- SseParser 能消费 ChannelReadStream；
- 取消/Abort 能让 `ReadAsync` 正常结束；
- 无死锁/无主线程阻塞。

**产出**

- `docs/spike/channel-stream.md` + 最小代码。

---

## 4. 环境

**Unity 版本轴（强制）**：

| 版本 | changeset | 角色 |
|---|---|---|
| 2021.3.45f1 | `0da89fac8e79` | 主目标 / 游戏锁定基线 |
| 6000.3.28f1（6.3 LTS） | `8f082d2fe476` | HTTP/2 前向验证 |
| 6000.6.8f1（6.6） | `237b6593792b` | 最新运行时验证 |

- Unity：上述三版本（Mono 优先；IL2CPP 按版本冒烟）；
- .NET：`dotnet` 10 SDK；测试工程 `net10.0`；spike 关键库 `netstandard2.1`；
- 本地服务：.NET 控制台（SSE/echo/wss）；
- 代理：系统代理 + `mitmproxy`/自建代理（按可用性）；
- 证书：自签本地 HTTPS/WSS；
- 远程端点：稳定公共 HTTPS/SSE/WSS 或自建（记录域名/时间）。

**版本轴规则**：S0-2/S0-3/S0-4/S0-5 在三个版本各跑一次；同一资产、同一测试代码，**不写版本分支**。结果按版本记录到 `docs/spike/`。

---

## 5. 数据记录格式

每个实验统一记录：

| 字段 | 内容 |
|---|---|
| 实验 ID | S0-x |
| 环境 | Unity 版本/运行时/OS/代理 |
| 输入 | URL/headers/body/流类型 |
| 结果 | 成功/失败 |
| 关键数据 | 首字节、chunk 数、总时长、回调次数 |
| 错误 | `result`、`responseCode`、`error`、异常类型 |
| 证据 | 日志/fixture/截图路径 |
| 结论 | 支持/不支持/需进一步验证 |

---

## 6. 决策规则

| 结果 | 决策 |
|---|---|
| S0-1 编译失败且无法小改 | 自写最小 SSE parser；保留 spec 测试 |
| S0-2 非逐帧/Abort 不可靠 | 评估 HttpClient/RawTcp 作为 Unity 主路径 |
| gzip 未自动解压 | 传输层/核心层加解压步骤 |
| UWR 显式代理不可行 | 显式代理只走 HttpClient；能力声明 |
| 远程 wss 失败 | WebSocket 仅本地/实验；M1 不承诺 |
| IL2CPP 剥离 | 必须 `link.xml`；否则只声明 Mono |
| ChannelReadStream 死锁 | 改用手写 parser/同步队列，记录偏离 |

---

## 7. 交付物

```
docs/spike/
  sse-compile.md
  uwr-streaming.md
  proxy-tls-matrix.md
  websocket-matrix.md
  il2cpp.md
  channel-stream.md
  phase0-summary.md      // 汇总 + go/no-go
spikes/                  // 一次性项目，不进生产
```

`phase0-summary.md` 必须给出：

- UWR 是否主路径；
- HttpClient 是否/何时提供；
- WebSocket 支持等级；
- gzip/IL2CPP/代理结论；
- 对 Phase 1/2 的具体修订。

---

## 8. 非目标

- 不写生产代码；
- 不做完整 auth/Deferred/图片/分类；
- 不实现完整 WebSocket 传输；
- 不做 WebGL（只记录限制）；
- 不优化性能，只确认事实。

---

## 9. 风险

1. Unity 项目/IL2CPP 环境不可用 → 先做 Editor Mono，标记未验证；
2. 远程端点不稳定 → 用本地服务 + 记录时间；
3. 代理环境差异 → 明确记录系统代理/显式代理配置；
4. 自签证书配置不一致 → 记录证书/信任方式；
5. 外部服务/网络受限 → 不把失败直接归因于 Unity。

---

## 10. 验收标准

- [ ] S0-1 编译结论 + 测试结果；
- [ ] S0-2 逐块/首字节/Abort/gzip/错误分类结论（2021.3.45f1 / 6000.3.28f1 / 6000.6.8f1）；
- [ ] S0-3 代理/TLS 矩阵（三版本）；
- [ ] S0-4 本地 ws / 远程 wss 支持等级（三版本）；
- [ ] S0-5 IL2CPP 结论（三版本；按声明支持）；
- [ ] S0-6 ChannelReadStream 可行性；
- [ ] `phase0-summary.md` 给出 go/no-go 与 Phase 1/2 修订；
- [ ] 无生产代码合并。

---

## 11. 对后续阶段的影响

- Phase 1 的接口不变；若 S0 发现 `Stream` 桥不可行，只改 `ChannelReadStream` 实现，不动 Abstractions；
- Phase 2 的 UWR 实现以 S0-2/S0-6 为蓝本；
- Phase 3 的 HttpClient 以 S0-3 为能力边界；
- Phase 5 的 WebSocket 以 S0-4 为支持等级。
