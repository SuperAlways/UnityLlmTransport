# 阅读笔记 · 06 · jianghuyouling（江湖有灵）流式 LLM 传输层

> 参考：`mods/jianghuyouling-ref-v0.30.0`（Steam 创意工坊 mod「江湖有灵测试版」itemid 3764815892，作者 EricSun0218，v0.30.0.24 的 ilspycmd 反编译产物，见该仓库 README.md:1-5）。只读笔记，非实现。
> 下文 `Client:` = `source/core/JianghuYouling.Core.Llm/OpenAiCompatibleClient.cs`（3251 行），其余同目录。

## 1. 架构 / TFM / 许可证

- **TFM 是 net48 不是 netstandard2.1**（`source/core/JianghuYouling.Core.csproj:5`），LangVersion 15.0 + AllowUnsafeBlocks（:8-9），引用 System.Net.Http + Newtonsoft.Json（:144-145）。API 面全部是 netstandard2.1 也有的（TcpClient/SslStream/SemaphoreSlim/ServicePointManager），代码可移植，**csproj 不可照搬**。
- **无任何 LICENSE 文件**，且是第三方闭源 mod 的反编译产物——法律上不可 vendor，只可作模式参考。
- 架构分层清晰，四段式流水线：transport（Client）→ 帧解码（SseBodyReader）→ SSE 事件（SseEventReader）→ 语义累积（StreamingToolCollector），层间只传 `Action<string>` 回调 + `ProtocolError` 字符串。**非流式走 HttpClient**（Client:1577 SendRawAsync:1631），**流式走裸 TCP 手写 HTTP/1.1**（Client:934 RawSseStreamAsync）。
- 全局并发闸门：普通 3 / Ollama 1 / 后台 2（Client:70-74, 1730-1737）。
- **为什么流式不用 HttpClient**：反编译代码无注释、没写动机（这是反编译的固有损失）。可观察事实推断：(a) HttpClient.Timeout 是整请求一刀切，net48 上无法做"每读一次重置的空闲超时"，裸 TCP 每次读挂独立 CTS 即可（Client:1071-1077）；(b) 取消要立即掐 socket——`ct.Register(() => tcp.Close())`（Client:985-994）比等 HttpClient 响应流取消传播更硬；(c) 想做头部级严格校验与 Content-Length 对账（Client:1288-1406），托管栈不会给你原始头字节。代价见 §4。

## 2. RawSseStreamAsync 路径（file:line）

1. 端点解析：scheme/port（443|80）/DnsSafeHost（Client:943-947）；请求体 >8MB 直接拒（:949-955）。
2. 手写 HTTP/1.1 请求头：POST 路径 + Host + `Authorization: Bearer` + `Content-Type: application/json` + `Accept: text/event-stream` + `Accept-Encoding: identity` + `Content-Length` + **`Connection: close`**（:956-969）。
3. `new TcpClient()` + `ct.Register(tcp.Close)`（:984-994）；连接/握手 CTS `max(15, timeoutSec)s` 同样挂 tcp.Close（:996-1009）。
4. `ConnectAsync`（:1014）→ 回环劫持检查（:1015-1022）→ `tcp.GetStream()`（:1023）→ https 时 `new SslStream(..., 回环豁免回调)` + `AuthenticateAsClientAsync`（:1026-1028）→ 写头/写体/Flush（:1034-1036）。任何异常先分辨 ct 取消 vs setup 超时（:1038-1057）。
5. 读循环：16KB buf（:1061）；每次 `stream.ReadAsync` 挂**新** idle CTS `max(60, timeoutSec)s`（:1071-1077）；OCE 按 ct.IsCancellationRequested 分成"已取消"/"流式空闲超时(Ns无数据)"（:1079-1095）。
6. 响应头解析：逐字节累积找 `\r\n\r\n`，>64KB 报错（:1101-1114）；`TryParseHeaders`（:1288-1406）：全 ASCII（:1300-1307）、状态行必须 HTTP/1.0|1.1（:1315）、状态码 3 位纯数字（:1320-1325）、禁折行/非法字段名/控制字符值（:1331-1356）、TE 只支持单一 chunked、TE 与 CL 互斥（:1357-1403）、CL ≤8MB（:1368）。
7. 非 2xx → `ReadErrorBodyAsync` 复用 SseBodyReader 收错误体，16KB 上限 + 10s idle（:1135-1143, 1445-1496）；Content-Type 必须 text/event-stream（:1144-1151, 1424-1427）。
8. 组装：`SseEventReader`（顺带量 TTFT，首条非 [DONE] data）→ `SseBodyReader(chunked?)`，首批残余字节立刻 Feed（:1152-1164）；后续每读一批 `sse.Feed`（:1168）。
9. 终止条件：chunked 必须 SseBodyReader.Done（0-chunk+trailer）；identity 必须字节数=Content-Length（:1195）；收尾 Flush/Complete + CL 对账 + `collector.IsProtocolComplete`（finish_reason/[DONE] 前中断即报错）（:1196-1237）。
10. finally：ssl → stream → tcp 三连 dispose/close（:1262-1285）。
- **SseBodyReader**：手写 chunked 状态机 Size/SizeLf/Data/DataCr/DataLf/Trailer/TrailerLf（SseBodyReader.cs:8-19）；chunk size 行 ≤128B、hex ≤16 位（:103,126）；trailer ≤64KB、行 ≤8192、禁 CL/TE/Host（:207-358, 520-548）；0-chunk 后多余字节报错（:365-368）；UTF-8 严格解码 throwOnInvalidBytes（:29,427）；BOM 剥除（:441-445）；CRLF 归一（:446-462）；行 ≤2M chars（:464-467）；decoded ≤8MB（:159-161,411-415）；TE 开销 ≤ 8×decoded+64KB（:83-88）。
- **SseEventReader**：`:` 注释行忽略、`data:` 拼接（去一个前导空格）、空行 dispatch（SseEventReader.cs:30-57,69-78）；行/事件各 ≤2M（:26,49）；`Complete()` 把"未空行终止的残余 data"判为协议错（:61-67）。**只认 `data` 字段**，event:/id:/retry: 静默丢弃。

## 3. 超时 / 取消 / TLS / 上限

- 连接+TLS+写请求：`max(15, timeoutSec)s` 一个 CTS（Client:996）；流空闲：每读一次重置的 `max(60, timeoutSec)s` CTS（:1071-1074）。**无整请求硬超时**——只要 60s 内有任意字节就永远等（OpenAI 长推理场景是特性不是 bug）。`IdleStreamSec` 常量（:104）是死代码未使用。
- 取消：外部 ct + `tcp.Close()` 硬杀阻塞读（:985-994）；正常收尾三连 dispose（:1262-1285）。
- TLS：SslStream 证书策略仅豁免回环 host（:1026）；**未显式 pin 协议版本**——:979 的 `ServicePointManager |= Tls12` 只影响 HttpClient 路径，对 SslStream 无效（冗余/坑）。
- 上限（`LlmProtocolLimits.cs` 全部集中常量）：请求体 8MB、响应体 8MB、HTTP 头 64KB、错误体 16KB、SSE 行/事件各 2M chars、JSON 深度 64、工具定义 128/调用 32、工具参数 64KB/总 512KB。上限判错全部是**中文明确报错串**。

## 4. 裸 TCP 的代价（无代理 / HTTP2 / 重定向 / 连接池）

- **零代理支持**：全代码无 Proxy 引用，不走系统代理、无 CONNECT 隧道——公司代理/VPN 环境直连失败。
- **无重定向**：301/302/307 直接按非 2xx 走错误体路径（Client:1135-1143），不会跟随；带重定向的中转端点直接报错。
- **无连接池**：`Connection: close`（:968）每请求全新 TCP+TLS 握手，握手成本全部计入 TTFT；高频小请求显著劣于 keep-alive。
- **仅 HTTP/1.1**：无 h2（SslStream 不带 ALPN options），且 DNS 每次重新解析。
- 这些代价他们付得起（mod 是低频单用户场景），但 HttpClient + `HttpCompletionOption.ResponseHeadersRead` 在 netstandard2.1 上可免费拿到代理/keep-alive/解压，只欠"分层超时 + 硬取消"两件事——见 §6。

## 5. 错误分类 / 重试

- `IsTransientConnError`（Client:332-355）：沿 InnerException 链走，**InvalidDataException → 明确不重试**（协议错≠瞬时错，这个区分是对的）；HttpRequestException/IOException/SocketException → transient；再兜底消息子串匹配（transport connection / connection reset / expecting chunk trailer / 已中止 等）。
- **重试只在非流式 SendRawAsync**：Ollama 3 次 / 其他 2 次（:1616），退避 450ms（Ollama 750ms×attempt）（:1681），retryClass=`transient_connection`（:1680）。
- **流式路径没有 transient 重试**（SendToolRoundStreamCoreAsync:749-780）：只有两个"参数被拒"自适应降级重发——`stream_options/include_usage` 被拒→关 usage 重发（class=stream_options_unsupported）；`reasoning_effort` 被拒→剥参重发（class=reasoning_effort_unsupported）。流式中途断线直接把错误交上层，靠调用方重试。
- `IsRetryableServiceError`（:357-377）给上层做字符串分类：429/408/409/425/5xx→true；401/403/400/上下文超限→false。子串匹配耦合中文错误串，脆弱。
- 观测贯穿：QueueMs/TtftMs/RetryCount/RetryClass/七种 token 数全程记账进 LlmLog（:434）。

## 6. 可借鉴 vs 不默认照搬

**可借鉴（按 netstandard2.1 重写）：**
1. **四层分离** transport→帧→SSE→语义累积，层间只走 `Action<string>` + `ProtocolError`——真流式管线的正确形状，且每层可独立单测。
2. **分层超时**：握手一个短 CTS（15s）、每次 ReadAsync 一个独立重置的 idle CTS（60s）——区分"没连上"和"流断了"，两套报错文案。HttpClient 也能做：包一层自管读循环即可。
3. **硬取消**：取消时直接杀底层 socket/流而非等异步传播；netstandard2.1 下对 NetworkStream 同样成立。
4. **上限体系集中成常量类**（LlmProtocolLimits）+ chunked/头严格校验防伪造流。
5. **协议完整性判定**：SawDone/SawFinishReason/IsProtocolComplete（StreamingToolCollector.cs:90-131）——"收到流末尾"≠"收到完整回答"，半截流必须报错。
6. TTFT/QueueMs/RetryClass 指标从 wire 层一路带到日志。

**不默认照搬：**
1. **裸 TCP 作为首选传输**——他们要的是头部级校验与硬取消，代价是丢代理/keep-alive/重定向，且要自己补 120 行头解析+400 行状态机（HttpClient 白送的部分）。我们 netstandard2.1 + HttpClient(ResponseHeadersRead) 自管读循环可两全。
2. 3251 行 god class：Gemini thought-signature / DeepSeek thinking 等 provider hack 与传输层混在一起。
3. 错误分类靠异常/错误消息子串匹配（含中文字面量）。
4. 静态单例 HttpClient + 全局 SemaphoreSlim 的进程级共享态（`RequestThinking` 已被他们自己标 Obsolete，Client:106-107）。

## 7. 结论

**reference，3/5。** 不可 vendor（第三方闭源 mod 反编译产物、无许可证、net48 TFM）；不值得整体 wrap（传输假设与 Newtonsoft/静态态耦合太深）。价值集中在四个可移植模式：分层超时、硬取消、严格 SSE 帧解析上限体系、协议完整性判定——恰好都是"Unity 2021 真流式"要解的核心题，且都用 netstandard2.1 等价 API 可重写。它证明的路线是"自管读循环 + 底层流读"而非"必须裸 TCP"。
