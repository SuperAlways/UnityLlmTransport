# 09 · 反例阅读笔记：TaiwuEncyclopedia LLM 传输层（yaolao）

> 阅读对象：`D:/shuoshu/taiwuask/TaiWuYaoLao/mods/TaiwuEncyclopedia/`
> 重点文件：`src/Frontend/Networking/LlmTransportHost.cs`、`src/Core/Agent/ILlmClient.cs`、`src/Core/Llm/ChatResponseParser.cs`、`src/Core/Llm/ApiRetryPolicy.cs`
> 定位：**反例**。以下 file:line 均相对上述仓库根。
> 日期：2026-11-05

## 1. 分层架构评价（Core 接口 + Frontend UWR 实现）

形状是对的，兑现是打折的。

- 对：`ILlmClient` 定义在 Core（`src/Core/Agent/ILlmClient.cs:12-22`），UWR 实现在 Frontend（`LlmTransportHost.cs:18`），消费方 `AgentRunner`/`AgentLoop` 只注入接口（`AgentRunner.cs:26,51`）。JSON 构建/解析是 Core 纯函数（`ChatResponseParser.cs:13-114`），无 Unity 依赖。
- 裂缝 1：实现类是 **MonoBehaviour 单例**（`LlmTransportHost.cs:18-34`）——接口抽象了"传什么"，没抽象"怎么调度"；协程/TCS 语义全锁死在 MonoBehaviour 里，Core 没法注入假实现测等价行为。整个 mod **没有测试工程**（仅 Frontend/Core/Backend 三个 csproj）。
- 裂缝 2：接口只覆盖一半用途——`FetchModels`/`TestConnection` 是裸 `IEnumerator` 直接挂在具体类上（`LlmTransportHost.cs:244,293`），UI 面板被迫引用具体类型。
- 裂缝 3：分层没换来决策下沉——`ApiRetryPolicy.Evaluate` 纯决策引擎（重试/失败/熔断）**全仓零调用**（grep 证实只有 `GetDelay`/`ClassifyStatus`/`IsForeground` 被传输层用），传输层自己内联一套不同参数的重试（见 §2-C）。

## 2. 具体反模式（file:line）

**A. DownloadHandlerBuffer 假流式（最核心）**
`LlmTransportHost.cs:85` 用 `DownloadHandlerBuffer`，`:88-98` 轮询等 `op.isDone`，`:106-108` 才一次性 `tcs.SetResult(request.downloadHandler.text)`；`StreamChatAsync`（`:41-74`）是 **await 整段响应完毕后 `Split('\n')` 逐行 yield**（`:63-73`）。接口签名 `IAsyncEnumerable<StreamChunk>` 是假的：首 token 延迟 = 完整生成时长。下游 `AgentLoop.cs:135-144` 逐 chunk append + `FinalChunkEvent` 的 UI 增量实际一次性到达。

**B. 整请求 timeout（与 A、C 形成致命组合）**
`LlmTransportHost.cs:86` `request.timeout = 120`（流式）/ `:184`（非流式）。UWR timeout 覆盖**整个请求生命周期含 SSE 全程**，不是首字节/空闲超时：>120s 的长回答被掐死。掐死表现为 ConnectionError（"Request timeout"）→ 落进 `:114-125` 的 ConnectionError 重试分支 → **整个 120s 生成重发重计费**，最多 2 次退避 → 最坏 ~6 分钟等待 + 3 倍计费。

**C. 重试只覆盖"初始连接"是错觉**
`LlmTransportHost.cs:79` 注释宣称 "Retry on initial connect … (not during streaming)"，但 UWR 单请求模型下没有"连接 vs 流"两阶段，重试单位=整个 POST：429/5xx 在服务端已生成完毕后返回，客户端照样全量重发 prompt。且 maxRetries 三处不一致：流式硬编码 2（`:80`）、ChatAsync 按 role 3 或 0（`:179`）、`ApiRetryPolicy.MAX_RETRIES=4`/`MAX_529_RETRIES=3`（`ApiRetryPolicy.cs:15-16`）无人使用。

**D. responseCode=0 误判：修了一半**
`LlmTransportHost.cs:112-114` 有 "Bug 4" 注释（ConnectionError→responseCode=0→ClassifyStatus(0)=Success），靠"先查 `request.result`"绕过；根因未修：`ApiRetryPolicy.cs:39` 兜底 `_ => ApiErrorType.Success` 把 0/未知码全判 Success。残留路径：`Result.DataProcessingError` 或未知状态码走 `:127` → Success → 掉进 `:147-155` "transient 重试"分支 → 最终 `SetException(new ApiException(ApiErrorType.Success, ...))`——**异常类型叫 Success**。

**E. 取消/残留回调断裂**
- 接口有 ct（`ILlmClient.cs:16`），但 Core 四处调用全部传 `CancellationToken.None`（`AgentLoop.cs:135,179,245,376`）→ 取消通道形同虚设。
- `ChatAsync` 签名根本没有 ct（`LlmTransportHost.cs:162-169`），无法取消。
- 协程挂 MonoBehaviour（`:49,167`），宿主 OnDestroy 杀协程 → `tcs.Task` 永不完成 → **await 永久悬挂**；`OnDestroy`（`:317-320`）只清 `_instance`，不落 TCS。
- 取消被静默吞：`:56-59` catch OCE 后 `yield break`——消费者拿到空流而非取消信号；`:61` 又在另一路径 `ThrowIfCancellationRequested()` 向外抛 OCE，错误面不一致。

**F. 测试接口 timeout 不一致**
TestConnection 15s + maxTokens:1（`LlmTransportHost.cs:297,306`）、FetchModels 15s（`:257`）、RAG 60s（`RagTransportHost.cs:45`）、主链路 120s（`:86,184`）——四入口四种值，无共享策略常量。TestConnection 是裸 IEnumerator 被 UI 直接消费，行为与主链路（role 重试 + context-too-long 检测）完全脱节：探活成功≠主链路可用；探活失败不区分 401/429/网络（`:314` 只透传 `request.error`）。

## 3. 它做对的地方

1. **接口分离方向正确**：契约在 Core、传输在 Frontend、消费方依赖注入（§1 前半）。
2. **JSON 构建/解析纯函数化**（`ChatResponseParser.cs`）：`ParseChunk` 对 usage-only chunk、finish-reason-only chunk、空 choices 的分流细致（`:76-100`），`stream_options.include_usage`（`:30`）用法正确。
3. **错误分类意识**：`ApiErrorType` 细分 Timeout/RateLimit/Overload/AuthError/ServerError/ClientError/NetworkError/ContextTooLong（`ApiRetryPolicy.cs:31-40`）；400/413 响应体关键词二次归类 ContextTooLong（`LlmTransportHost.cs:131-140`）。
4. **重试分类意识（设计层）**：前景/背景角色决定是否重试（`ApiRetryPolicy.cs:21-28`）、指数退避+jitter（`:43-51`）、529 连续熔断转 TellPlayer（`:60-65`）——思路全对，可惜是死代码。
5. **错误类型→中文文案映射表**（`ApiRetryPolicy.cs:83-105`）值得直接借鉴。

## 4. 对 UnityLlmTransport 设计的"不要这样做"清单

1. 不要 `DownloadHandlerBuffer` + await 全文再 Split 假装流式——`IAsyncEnumerable` 签名不等于真流式；逐块回调（DownloadHandlerScript/自建接收路径）必须在传输层内部完成。
2. 不要把 UWR `timeout` 当空闲超时用——它覆盖全请求；SSE 场景要么弃 UWR，要么另设首字节/空闲看门狗。
3. 重试必须区分"未收到响应头"（可安全重发）与"已开始生成"（重发=重复计费）；状态码重试与超时重试策略分开。
4. 状态码分类的兜底分支不许是 Success——未知/0 一律 fail-closed 归 Unknown/NetworkError。
5. ct 必须贯通到实现层，调用侧禁止 `CancellationToken.None`；取消要么抛 OCE 要么显式事件，不许静默 `yield break`。
6. TCS 桥接必须配宿主死亡兜底（OnDestroy/OnDisable 时 `TrySetException`/`TrySetCanceled`），否则 await 悬挂。
7. timeout/重试参数收敛到单一策略对象，禁止各入口硬编码 15/60/120/2/3/4。
8. 纯函数决策引擎写出来就要接进传输层并配单测，不许与内联重试双轨并存。
9. MonoBehaviour/协程是实现细节，不得泄漏进接口（裸 `IEnumerator` 方法挂具体类）。

## 5. 结论

**avoid（作为传输实现）；reference（作为反例 + 错误分类词表）。评分：2/5。**

接口形状、错误分类词表、重试分类设计可参考（+）；但"真流式"承诺零兑现（假流式 + 整请求 timeout + 超时→重发计费三连），取消链路断裂，好的决策引擎是死代码，零测试，MonoBehaviour 耦合（-）。一句话：**接口学它，传输绕它。**
