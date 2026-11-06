# 阅读笔记 · 04 · com.openai.unity + OpenAI-DotNet（RageAgainstThePixel）

> 参考：`refs/03-com-openai-unity`（commit e79d18d, v8.8.9, 2026-01-25）、`refs/03b-openai-dotnet`（commit e238263, v8.8.8, 2025-12-13）。只读笔记，非实现。两仓库同作者（Stephen Hodgson），Unity 包是 dotnet 版的移植（结构几乎同构）：Unity 侧 Newtonsoft + UWR，dotnet 侧 System.Text.Json + HttpClient。SSE 底座细节已另有专篇（`03-com-utilities-rest.md`），本篇不重复。

## 1. TFM / Unity 版本 / 依赖 / 许可 / 活跃度

- **Unity 包**：UPM `com.openai.unity` v8.8.9，**`"unity": "2021.3"`**（package.json:7）、MIT（package.json:10；LICENSE.md）。直依赖 4 个（package.json:20-23）：`com.utilities.audio 3.0.3`、`encoder.wav 3.0.2`、`rest 5.1.1`、`websockets 2.0.0`；git-url 安装路线列满 **6 个 com.utilities.\***（README.md:50-55，rest 再传 async/extensions/Newtonsoft）。发 OpenUPM（package.json publishConfig）。Runtime asmdef 引 6 个 GUID（Runtime/OpenAI.asmdef:5-12）。**最新提交 2026-01-25**——活跃。
- **dotnet 版**：`<TargetFramework>net8.0</TargetFramework>` 单 TFM（OpenAI-DotNet.csproj:3）、MIT（:25）、v8.8.8（:32）。**不可直接进 Unity/netstandard2.1**，只能读逻辑。最新提交 2025-12-13——活跃。
- 体量：Unity 包 Runtime 一百多个 DTO 文件（Chat/Responses/Threads/Assistants/Audio/Batch/Realtime/WebSocket 全家桶），core 逻辑集中在少数 Endpoint/Extensions 文件。

## 2. SSE / 流式 API 形状

**两种宿主两种形状**：

- **Unity 包：纯回调**，无 IAsyncEnumerable。Chat：`StreamCompletionAsync(ChatRequest, Func<ChatResponse,Task> resultHandler, bool streamUsage, ct)`（Runtime/Chat/ChatEndpoint.cs:131-168），内部把 SSE 循环整段委托给 com.utilities.rest：`Rest.PostAsync(url, payload, async (sseResponse, ssEvent) => {...})`（:139-162），回调里只认 `ServerSentEventKind.Data`（:143），其余 event/comment/id/retry 行直接丢弃。**回调签名是"裸 wire 事件"，不带事件名**——chat completions 只有 data 行所以够用。
- **Unity 包 Responses：回调升级为 `(string eventName, IServerSentEvent typedEvent)`**（Runtime/Responses/ResponsesEndpoint.cs:65-104 → StreamResponseAsync :129-457）：按 `response.*` 事件名巨型 switch 分派，产出**强类型域事件**。分层即在此：wire `ServerSentEvent{Event,Value,Data}` → switch → `Response/IResponseItem/IResponseContent/ToolCall/Error`（都实现 `IServerSentEvent { string Object; string ToJsonString(); }`，与 03c 同接口）。
- **dotnet 版：回调 + IAsyncEnumerable 双轨**。回调版 StreamCompletionAsync（Chat/ChatEndpoint.cs:135-163，无 try/catch 吞异常——handler 抛异常即断流，与 Unity 包相反）；枚举版 `StreamCompletionEnumerableAsync` 带 `[EnumeratorCancellation]`（:179-247），自己 `StreamReader.ReadLineAsync(ct)` 逐行解析。真流式来自 `HttpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)`（Common/OpenAIBaseEndpoint.cs:100）。
- **dotnet 版 SSE 行解析是独立可读的参考件**：`BaseEndpointExtensions.StreamEventsAsync`（Extensions/BaseEndpointExtensions.cs:26-119）——用 Stack 把 `event:` 行与后续 `data:` 行配对（:95-101），`[DONE]`/`done` 停（:59-64），`TryParseServerSentEventLine` 处理 BOM/冒号/CRLF（:121-173）；finally 里把收到的全部事件交给错误检查（:115）。
- **Responses 事件全集**（Unity 侧证据，dotnet 同）：`response.created/queued/in_progress/completed/failed/incomplete`（ResponsesEndpoint.cs:155-175）、`output_item.added/done`（:196-208）、`content_part.added/done`（:176-195）、`output_text/audio/refusal/reasoning_text.delta|done`（:265-346）、`reasoning_summary_*`（:347-377）、`function_call_arguments.delta/done`（:209-224）、`custom_tool_call_input.*`（:225-240）、`mcp_call_arguments.*`（:378-393）、`code_interpreter_call_code.*`（:394-409）、`error`（:410-415）、未识别事件**原样透传**给用户回调（default，:432-437）。

## 3. tool_calls 按 index 拼装 / usage / 错误聚合

**拼装链（chat completions，Unity 侧）**：每 chunk 反序列化 `ChatResponse` → `chatResponse.AppendFrom(partialResponse)`（ChatEndpoint.cs:147-154），链路：

1. `ChatResponse.AppendFrom`（Chat/ChatResponse.cs:111-154）：Id 不一致即抛（:115-121，防串流）；Usage/Choices 分派。
2. **index 拼装核心在列表扩展**：`CollectionExtensions.AppendFrom<T>`（Extensions/CollectionExtensions.cs:9-28）——`otherItem.Index` 超出现有长度则 `new T(); self.Insert(index)`，否则 `self[index].AppendFrom(other)`。**通用"按 Index 覆盖/插入"一处实现，choices 与 tool_calls 共用**。
3. `Choice.AppendFrom`（Chat/Choice.cs:66-105）→ `Message.AppendFrom(Delta)`（Chat/Message.cs:212-252）：`content += delta`（:222），`toolCalls.AppendFrom`（:235-239）。
4. `ToolCall.AppendFrom`（Common/ToolCall.cs:59-89）：Id/Index/Type 整体替换，Function 嵌套追加（:86）→ `Function.AppendFrom`（Common/Function.cs:281-302）：**`argumentsString += other.Arguments`**（:295）——JSON 参数串逐段拼接，最后整体 parse。

**Responses 的 tool call 拼法不同**：按 `output_index` 定位 `response.Output[index]` 强转成 `FunctionToolCall`，校验 item_id 后覆写 `Delta`/`Arguments`（ResponsesEndpoint.cs:209-224），靠 `response.completed` 事件整包覆盖收尾——不做字符串累加，参数全量来自 done 帧（delta 只作展示）。

**usage**：`streamUsage: true` → 请求带 `StreamOptions`，最后一个 chunk（choices 为空数组）带全量 usage（ChatEndpoint.cs:60-64 文档 + :135-136）；聚合用 `Usage.AppendFrom`（Common/Usage.cs:48-74）——**非空字段覆盖而非累加**（累计口径另有 `operator+` :81）。注意 usage chunk 不走 choices 拼装，落到 `ChatResponse.Usage` 单独覆盖。

**错误**：三层——
- Unity Chat 流内回调整体 try/catch **只 `Debug.LogError` 不中断**（ChatEndpoint.cs:158-161）：用户 handler 抛异常、坏 chunk 都被吞，流继续；HTTP 非 2xx 由收尾 `response.Validate()` 抛 RestException（:163；RestException 定义见 03c Rest.cs:2080-2091）。
- Unity Responses 流：处理异常被 catch 转成 `@event="error"` + `Error` 域对象照常发给用户回调（:440-449）；SSE `error` 事件名也有 case（:410-415）。
- dotnet 版：回调路径无吞异常（异常向上断流），SSE `error` 事件 → `Error`（Responses/ResponsesEndpoint.cs:402-407）；收尾 `CheckResponseAsync(events)` 汇总已收事件用于报错（BaseEndpointExtensions.cs:115 + ResponseExtensions.cs:135+）。

## 4. 取消与 UWR Abort 桥接

- **Unity 包**：token 一路传进 `Rest.PostAsync` → `Rest.SendAsync`。桥接**不是** UWR 原生（`SendWebRequest()` 不收 token），而是**帧泵轮询**：SSE 场景下启动的 CallbackThread 循环里 `if (cancellationToken.IsCancellationRequested) { webRequest.Abort(); }`（03c Runtime/Rest.cs:1762-1765），之后 SendWebRequest 以 ConnectionError 结束 → `Response.Successful=false` → `Validate()` 抛 **RestException 而非 OperationCanceledException**（除 SendWebRequest 本体抛 TaskCanceled 的分支，Rest.cs:1818-1822）。**我们做取消映射时必须把 RestException 归一成统一的取消信号**。
- 取消语义细节：Abort 后 finally 段还在 `ThrowIfCancellationRequested` 排空 SSE 队列（Rest.cs:1843-1857）；粒度=帧（Abort 最多滞后一帧），事件泵另有一个主线程 ConcurrentQueue 消费者（Rest.cs:1776-1799），所以"收到 chunk"与"回调被调"之间隔一次主线程调度。
- **dotnet 包**：token 直通 `HttpClient.SendAsync(…, ResponseHeadersRead, ct)`（OpenAIBaseEndpoint.cs:100）+ 每行 `ThrowIfCancellationRequested`（BaseEndpointExtensions.cs:51、ChatEndpoint.cs:200）+ `ReadLineAsync(ct)`——教科书级协作式取消，抛 OperationCanceledException。这是我们要的语义，Unity 侧要自己补。

## 5. 只作参考的边界

1. **依赖绑架**：vendor Unity 包=拖 6 个 com.utilities.\* 包 + Newtonsoft（README.md:50-55、03c package.json:17-26），且其 SSE 读法、主线程泵、取消缺口都不合我们口径（03 篇 §4 已判）。
2. **OpenAI 专用**：端点/DTO/事件名全是 OpenAI wire 格式（含 Azure 变体，ChatEndpoint.cs:24 `IsAzureDeployment`），Realtime 走 WebSocket 另一套；换成 Claude/本地网关需换 wire 层——**只能抄"分层与聚合逻辑"，不能抄 DTO**。
3. **不能绑死**：streamUsage 的"末 chunk 才有 usage"、`[DONE]` 哨兵、Responses 的 `response.*` 事件名都是各家方言语义；我们传输层应把"事件名→域事件"的 switch 收敛在 provider 适配器内，不进 core。
4. 附带坑：Unity Responses 流结束后还有 `WaitForStatusChangeAsync(timeout:-1)` **每 500ms 轮询一次 GetModelResponse**（ResponsesEndpoint.cs:454 + ResponsesExtensions.cs:14-28）——后台响应才需要，前台流纯多余 HTTP；Unity Chat 流吞异常的宽容策略不可取（dotnet 版做法才是对的）。

## 6. 结论

**reference**（不 vendor / 不 wrap），**评分 4/5**。

- 价值最高三件：① Responses"事件名 switch → 强类型域事件"两层分发（事件类型分层范本）；② `CollectionExtensions.AppendFrom` 的按 Index 插入/覆盖 + Function.arguments 串接——tool_calls 增量拼装一页讲清；③ dotnet 版 `StreamEventsAsync` 的 Stack 配对行解析 + `ResponseHeadersRead` + 全程 token 语义——是我们 core 解析器与取消语义的现成对答案。
- 扣分：Unity 包流式实质仍寄居 com.utilities.rest（帧泵/O(n²)/取消缺口，见 03 篇）、吞异常、流后轮询；DTO 全家桶 OpenAI 绑死。**抄逻辑、不抄代码、不进依赖树。**
