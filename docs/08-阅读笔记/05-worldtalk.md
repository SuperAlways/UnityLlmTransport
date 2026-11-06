# 05 · WorldTalk（太吾WorldTalk v26.7.18）流式 LLM 通讯阅读笔记

> 参考：`D:/shuoshu/taiwuask/mods/worldtalk-ref-v26.7.18`（Steam 创意工坊 mod 反编译源码）
> 阅读对象：`source/frontend/TaiwuAI_Beta.AI.Common/AiApiClient.cs`（1103 行）
> 日期：2026-11-06

## 1. 架构 / 许可证 / 目标平台

- **来源与许可**：Steam 创意工坊 mod「太吾WorldTalk (天幕心帷)」itemid `3729307604`，作者 边缘行者/CroweNoah，v26.7.18.18，GameVersion 1.0.58.0（`README.md:3-5`）。**ilspycmd 反编译产物，无任何许可证声明**；README 自注"仅用于学习借鉴，勿照抄业务逻辑"（`README.md:7`）。
- **目标平台**：`TargetFramework netstandard2.1`（`TaiwuAI_Beta.csproj:8`），引用 `UnityEngine.UnityWebRequestModule` / `Newtonsoft.Json` / `0Harmony`（csproj:18-34）——与本项目 Unity 2021.3 + netstandard2.1 基线完全同代（`UnityLlmTransport/docs/05-版本与兼容基线.md:24-25`）。
- **架构形态**：单一前端 DLL，按域拆命名空间目录（ChitChat/Services/Intelligence/Personality…）。`AiApiClient` 在共享层 `AI.Common`，调用方 9+ 处（TalkRequestManager/TalkTurnCoordinator/PersonalityManager/PigeonMailScheduler 等）。客户端为 **UnityWebRequest 协程式**（`Send` 返回 `IEnumerator`，AiApiClient.cs:413），由调用方 MonoBehaviour `StartCoroutine` 驱动（`TalkRequestManager.cs:191,239`）。取消走 `IAiRequestHandle.Cancel()` 接口（`IAiRequestHandle.cs:3-6`）。

## 2. StreamingDownloadHandler 流式路径（核心证据）

`DownloadHandlerScript` 子类（AiApiClient.cs:18），预分配 16KB 环形缓冲 `base(new byte[16384])`（:70）。

| 环节 | 位置 | 做法 |
|---|---|---|
| UTF-8 增量解码 | :29（`Encoding.UTF8.GetDecoder()` 字段）+ :82-84 | `GetCharCount/GetChars(flush:false)` 逐块解码——**跨块拆散的多字节字符被 decoder 内部状态保留**，正确解法 |
| 尾包冲刷 | `CompleteContent` :92-109 | 结束时 `GetChars(flush:true)` 冲残字（:94-102），再处理残留半行（:103-106），最后强制通知（:108） |
| 行缓冲 | `DrainCompleteLines` :168-181 | 手写 `IndexOf('\n')` 扫 StringBuilder（:359-369），`TrimEnd('\r')`，`Remove(0,num+1)` |
| SSE 解析 | `ProcessLine` :183-244 | 只认 `data:` 前缀（忽略大小写，:185）；空行/`[DONE]` 直接返回（:190）；逐行 `JObject.Parse`（:196） |
| content/reasoning | :224-229 | `choices[0]` 取 **`delta ?? message` 双兜底**（:224——流里混入整包 message 也能接住）；reasoning 兼容 `reasoning_content/reasoning/thinking/thinking_content` 四别名（:229） |
| tool_calls 聚合 | `AppendToolCallDeltas` :246-311 | 按 `index` 键控 `ToolCallAccumulator`（:261-266），id/name 增量拼接（:271,:292），arguments StringBuilder 追加（:294-308，string/null/object 三型处理）；空 arguments 补 `"{}"`（:146） |
| usage 聚合 | :197-221 每 chunk 调 `ParseUsageToken` :896-980 | 事实上的**多厂商 usage 键位速查表**：prompt/input_tokens（:898）、cached 6 条路径 `prompt_tokens_details.cached_tokens / input_tokens_details.cached_tokens / input_token_details.cache_read / prompt_cache_hit_tokens / cache_read_input_tokens / cached_tokens`（:901-938）、cache_creation 4 条（:939-974）、cache miss（:975）、total 缺失时 prompt+completion 兜底（:976-979） |
| UI 回调 | `NotifyContentChanged` :324-336 | 脏标记 + `Time.realtimeSinceStartup` **0.1s 节流**（:328-333），完成时 force（:108） |
| 工具流预览 | `BuildPreviewContent` :338-357 | 带 tools 的请求流式期不显示正文，改显示 `ResponseParser.BuildToolCallStreamingDisplayText` 解析出的参数预览（:344 → `ResponseParser.cs:356`） |

注意：`[DONE]` 只被跳过、**不会提前终止请求**（:190）；整条原始流还额外累积进 `_raw` StringBuilder 供 trace（:31,:85）。

## 3. 请求组装 / Accept / timeout / 取消

- `Send` 协程 :413-511：POST 到 `_connection.Endpoint`（:419）；body 为**手拼 JSON**（`SerializeBody` :543-619）：messages（tool role 带 `tool_call_id`、assistant 回带 `tool_calls` 原文透传，:559-577）+ 按**模型名前缀**分厂商的 thinking 开关（deepseek→`thinking.enabled` :582-585，gpt→`reasoning_effort` :587-589，qwen→`extra_body.enable_thinking` :591-593，minimax→`extra_body.reasoning_split` :595-597）+ tools/`tool_choice:"auto"`（:599-610）+ `response_format` 透传（:612-615）+ `stream`（:617）。
- 头：流式 `Accept: text/event-stream`（:428）；两种模式都带 `Content-Type: application/json` + `Authorization: Bearer`（:434-435）；非流式 `DownloadHandlerBuffer`（:432）。
- timeout：`request.timeout = ConfigManager.Timeout`（:436）= **整请求 75s，夹在 10~120**（`ConfigManager.cs:19,186-188`）。UnityWebRequest.timeout 计的是整个操作时长。
- 取消：`_currentRequest.Abort()` + 置 null（:404-411）；字段 :437 赋值、:442 协程恢复后清空。Abort 后走 `request.result != 1` 分支，错误信息含 "abort/cancel" 时由分类器归为 `Cancelled`（AiApiFailureClassifier.cs Classify）。

## 4. UI 节流与线程模型

- **全主线程单线程模型**：`DownloadHandlerScript.ReceiveData` 由 UWR 在主线程帧循环回调；每条 SSE 行的 `JObject.Parse`、解码、聚合全在主线程；`Time.realtimeSinceStartup` 即主线程 API；无任何锁/后台线程。0.1s 节流只压**回调频率**，**解析本身不被节流**——每 chunk 照常全量解析。

## 5. 错误 / 重试 / 解析异常

- 传输/HTTP 失败：:491-500，`ExtractApiError`（:833-873）从 JSON body 挖 `error.message`/`message`，兜底取前 500 字符正文拼进 ErrorMessage。
- 解析异常：全部**空 catch 吞掉**——`ParseOpenAIResponse`（:631-665，失败后退化到对整包 raw 的 `"content"\s*:\s*"..."` 正则 ：659）、`ParseReasoningContent`（:667-705）、`ParseToolCalls`（:1015-1102）、`ParseUsage`（:875-894）；SSE 行解析 catch 仅 debug 开关下告警并继续（:237-243）。
- 成功判据：content 非空 或 有 toolCalls（:485），否则记"正文为空"错误（:488）；正文里的工具调用还能被 `ActionToolCallParser.RecoverToolCallsFromContent` 抢救（:477-484）。
- 失败分类：`AiApiFailureClassifier` 关键词分桶 SchemaUnsupported/EmptyVisibleContent/Timeout/Transport/Cancelled/ContractParse → 结构化失败码（:501 调用）。
- **重试在调用层不在客户端**：`TalkRequestManager.cs:302` 两轮 attempt；流失败→降级非流重发（:337）；schema 被拒→去掉 response_format 重发（:330）；缺 main_text 走本地 `FinalTextRepairFallback`（:345,:360）。客户端本身零重试零退避。

## 6. 可借鉴 vs 不可照搬

**可借鉴（对本项目直接有营养）**
1. **Decoder 增量 UTF-8 + flush:false / 完结 flush:true**（:29,:82-84,:94-102）——拆散多字节字符的正确解法，比"每块 new UTF8Encoding(false) 会炸中文"的 naive 做法（Yaolao/WorldTalk 前作踩过）强一档。
2. **行缓冲 + 逐行 data: 解析 + delta??message 双兜底**（:168-244）——对厂商流格式噪音鲁棒。
3. **usage 键位多厂商扇出表**（:896-980）——直接抄语义当规格参考。
4. **tool_calls 按 index 聚合 + 空参补 `"{}"` + 规范 JSON 再序列化**（:246-311,:121-166）——多轮 tool 回环可用。
5. **handler 内脏标记 + 0.1s 节流 + 完结 force**（:324-336,:108）；`IAiRequestHandle.Cancel` 单请求句柄（:404-411）；失败码分类驱动上层重试策略（AiApiFailureClassifier.cs）。

**不可照搬**
1. **SSE 解析在主线程**：每 chunk 全量 JObject.Parse + 分配，长流掉帧；且节流只压回调不压解析。本项目高吞吐场景需把行缓冲/解析挪出主线程或至少批处理。
2. **整请求 timeout 75s**（:436）：慢而活的流会被中途掐死；没有首字节超时/空闲超时之分——真流式必须改成"逐 chunk 空闲计时"。
3. **`_raw` 全量累积**（:31,:85,:111-119）：整条响应再存一份，长 reasoning 流内存翻倍，trace 用途可改环形窗口。
4. **缺 `stream_options:{include_usage:true}`**（SerializeBody :543-619 无此项）：OpenAI 兼容端 usage 恒 0；DeepSeek 类默认吐 usage 掩盖了这个洞。
5. 手拼 JSON + 自写 `EscapeJson`（:786-831）、`tool_calls` 原文透传注入（:575）、整包正则兜底（:659）——转义/误匹配风险；厂商开关按模型名前缀硬编码（:581-597）。
6. 无 newline 的非 SSE 响应会让 `_lineBuffer` 无限增长且每 chunk 全量重扫（IndexOf :359-369，O(n²)）；空 catch 静默吞坏行无遥测。

## 7. 结论

**reference（参考借鉴），3.5/5。**
不可 vendor（无许可证的反编译第三方 mod，法律/来源不洁）；不可 wrap（它是 mod 内嵌类，非独立库）。但其流式骨架——decoder 增量解码、行缓冲、index 键 tool_calls 聚合、usage 多厂商键位、0.1s 节流、失败码分类——正是 Unity 2021 + netstandard2.1 + UWR 真流式的**同代可行解**，与 pi.net（net9/10 运行时假设不可用，见 05-版本与兼容基线.md:16）互补：语义学 pi，UWR 侧工程细节学 WorldTalk。落地时修正三件事：主线程解析、整请求 timeout、include_usage。
