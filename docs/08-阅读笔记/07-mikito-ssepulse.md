# 07 · mikito unity-chat-gpt-sample + SsePulse.Client 阅读笔记

> T2 略读卡。日期 2026-11-06。证据标准：file:line > 文档 > 推测（推测已标注）。

## Repo A · mikito/unity-chat-gpt-sample（stream 分支）

**基本信息**：commit `6d51af2` 2023-03-17；Unity **2021.3.15f1**（ProjectSettings/ProjectVersion.txt:2）；依赖 UniTask（Packages/manifest.json:3）；代码仅 3 个 .cs（API 165 行 + Chat 114 行 + View 13 行）；**无 LICENSE**（仓库根只有 .gitignore/Assets/Packages/ProjectSettings/README.md）。

**⚠️ 事实订正（对本任务前提）**：stream 分支**完全没用 UWR / DownloadHandlerScript**——`git branch -a` 仅 `* stream`+`origin/stream`，全仓 grep `UnityWebRequest|DownloadHandlerScript` 零命中。它走 **HttpClient 流式**：`OpenAIChatCompletionAPI.cs:69` `SendAsync(request, HttpCompletionOption.ResponseHeadersRead)` → `:73` `ReadAsStreamAsync` → `:79` `StreamReader.ReadLineAsync()` 循环。UWR/DownloadHandlerScript 版在 master（Coroutine 版），本 checkout 未取到，**未验证**（README.md:17 只给链接）。"UWR 最小闭环"结论不能引此仓。

**流式路径**：POST JSON（OpenAIChatCompletionAPI.cs:65-69）→ ResponseHeadersRead（:69）→ ReadLineAsync 逐行（:79）→ `line.Substring(6)` 硬切 `"data: "` 前缀（:83）→ `[DONE]` 退出（:84）→ Newtonsoft 每片反序列化（:85）。主线程 UI：`Chat.cs:69-84` 在 MonoBehaviour 里 `await foreach`，UniTask 续体回 Unity 同步上下文 → `view.Content`/`scrollRect` 逐 chunk 更新，**无节流**。

**取消/超时/错误**：取消=仅 `GetCancellationTokenOnDestroy()`（Chat.cs:53）+ 行间 `ThrowIfCancellationRequested`（API:81）；**无超时配置**（API 文件 grep `Timeout` 零命中，吃 HttpClient 默认 100s）；**无重试/重连/续传**；错误=裸 `WebException` 带状态码+原始 body 字符串（API:52,92），无错误体类型化。

**缺什么**：① SSE 文法不解析——`Substring(6)` 假定每行都 `data: ` 开头，遇 `event:`/`id:`/`:` 注释行直接产出垃圾 chunk 抛 JsonReaderException（OpenAI 恰好不发，但 OpenAI 兼容端的心跳/注释行会炸流）；② NRE 风险：`Chat.cs:73-79` `chunk.choices[0].delta.role` 不判空（choices 空数组/delta 缺字段即崩），`finish_reason` 声明成 `object`（API:152）；③ **无任何测试**（Assets 下无 Tests，test-framework 包空挂 manifest.json:9）；④ API key 存 Inspector 序列化字段（Chat.cs:15）。UTF-8 多字节由 StreamReader 内部缓冲兜住，无增量解码问题。

**许可证风险**：无 LICENSE=默认保留所有权利。**只读思路，禁止复制任何代码**（与 docs/07 方法 §4 一致）。

## Repo B · claudiom248/SsePulse.Client

**基本信息**：commit `330a1e2` 2026-09-25；**MIT**（LICENSE:1-2）；**net8.0**（src/SsePulse.Client/SsePulse.Client.csproj:4），文档明示 .NET 8 最低（docs/docs/platform-availability.md:3-11）；核心 ~2.8k 行 + **28 个测试文件**（tests/SsePulse.Client.Tests）；解析用 `System.Net.ServerSentEvents` NuGet 10.0.12（Directory.Packages.props:17）。

**可参考的 API 形状**：
1. **流式 handler 注册**：`On/OnItem/OnItem<T>` 同步+异步全谱（SseSource.Handlers.cs:98-120、SseSource.AsyncHandlers.cs:45-111），事件名可从 `TEventData` 类型名按命名策略推导（AsyncHandlers.cs:47-49）或 `MapEventNameAttribute` 显式指定——注册期校验 AssertNotDisposed/NotStarted（Handlers.cs:100-101）。
2. **强类型反序列化**：`JsonTypeInfo<T>` 缓存后复用（Internal/SseEventDataHandler.cs:48-49），AOT/源生成兼容；反序列化失败走 OnError 回调而非炸流（StreamConsumer.cs:155-163）。
3. **重试/重连**：`RetryOptions` 固定/指数+封顶+full jitter（RetryOptions.cs:53-83，默认 5 次 1s→30s）；瞬态判定三分（状态码白名单/SocketError/Timeout，SseConnection.cs:70-93）；流中断 `ResponseAbortedException` → `RestartOnConnectionAbort` 循环重建（SseSource.cs:149-159）。
4. **其余形状**：连接生命周期四回调 Established/Closed/Lost/Error（Handlers.cs:23-88）；parser→消费者用**有界 Channel** 解耦+并行度可配（StreamConsumer.cs:41-47）；handler 失败可配 `StopSource`/吞掉继续（StreamConsumer.cs:159-163）；**Last-Event-ID 只提交连续前缀**——乱序 handler 完成不推进 ID（CommitAsync，StreamConsumer.cs:180-213 + EventIdCommitTracker）。

**Unity 2021 为何不可用**：① TFM net8.0，Unity 2021 只到 netstandard2.1；依赖链全是 net8 时代——`HttpIOException.HttpRequestError`（StreamConsumer.cs:66，.NET 8 独有）、`Random.Shared`（RetryOptions.cs:70，.NET 6+）、`ArgumentException.ThrowIfNegative`（RetryOptions.cs:132）、`field` 关键字（SseSource.Handlers.cs:31，C# 14，Unity 2021 封顶 C# 9）；② 全程 `Microsoft.Extensions.Logging` DI（StreamConsumer.cs:5）；③ **消费全在线程池**：`ConfigureAwait(false)`+`Task.Run` worker（StreamConsumer.cs:51），回调不回主线程，Unity UI 无内建 dispatch；④ **形状错位（最致命）**：它是 GET 长连订阅客户端——`SseConnection.cs:107` 硬编码 `HttpMethod.Get`+`Accept: text/event-stream`，IRequestMutator 只改头不改方法体；LLM 补全是 **POST+请求体+响应流**，一次一生命周期，SsePulse 没有请求体概念。

## 对我们架构的启示

**可吸收**：① mikito 证明 Unity 2021 + UniTask + HttpClient 真"逐行流式"闭环可行，且 cancel 经 token 贯通到底——但我们要的是 UWR 路线（WebGL/IL2CPP 强制 UWR），此仓只给"消费端主线程逐片刷新"的形状参考；② SsePulse 的**有界队列隔离 IO 线程与消费端**、**handler 失败隔离策略**、**jitter 退避**、**连续前缀提交**（对应我们 seq/FIFO 乱序纪律）、**生命周期四回调**都值得移植为本体层接口形状；③ 反序列化失败不炸流、走 OnError——正确方向。

**明确不可用**：① mikito 无 LICENSE → 零代码复用；且 `Substring(6)` 这类裸前缀切割正是我们要避免的反面教材（SSE 文法解析必须走完整 `field: value` 状态机）；② SsePulse TFM/线程模型/GET 订阅形状三重错位 → 不可 wrap 不可 vendor，只能抄"选项形状"；③ 两仓都没有分层超时/取消后的残留回调治理/mock 录制回放——仍是我们自己的空白。

## 结论

| 仓库 | 处置 | 分数 (1-5) | 理由 |
|---|---|---|---|
| mikito stream | **reference**（仅思路） | **2/5** | Unity 2021 真流式闭环可证，但 HttpClient 路线非我们主线、无 LICENSE、无测试、SSE 解析错误示范；且"UWR+DownloadHandlerScript"实为此分支不实传闻 |
| SsePulse.Client | **reference**（仅形状） | **3/5** | MIT+测试齐+工程化程度高，注册/重试/背压形状优秀；但 net8 TFM+线程池消费+GET 订阅形状不可移植，代码本体零可用 |
