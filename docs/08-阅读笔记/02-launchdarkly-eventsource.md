# 02 · LaunchDarkly dotnet-eventsource 阅读笔记

- 仓库快照：`refs/02-launchdarkly-dotnet-eventsource`（shallow clone，仅 1 commit：`59b87b6` release 6.0.0，2026-09-15；commit 级历史不可考，活跃度以 CHANGELOG 为据）
- 阅读范围：`src/LaunchDarkly.EventSource/`（全部 25 文件核心已读）+ `test/` 目录结构 + 关键重连测试
- 日期：2026-11-06

## 1. TFM / 依赖 / 许可证 / 维护

| 项 | 事实 | 证据 |
|---|---|---|
| 版本 | 6.0.0 | `src/LaunchDarkly.EventSource/LaunchDarkly.EventSource.csproj:4` |
| TFM | `netstandard2.0; net462`（无 netstandard2.1 / net8） | csproj:6 |
| LangVersion | 7.3（无 C# 8+ 语法，async/Task API） | csproj:11 |
| 依赖 | 仅 `LaunchDarkly.Logging [2.0.0,3.0.0)`（小日志库，非游戏相关） | csproj:23 |
| net462 | 显式引 `System`/`System.Net.Http` | csproj:26-30 |
| 许可证 | Apache-2.0 | LICENSE:3；csproj:7 |
| 维护 | LaunchDarkly 官方维护，供其 .NET SDK 使用。6.0.0=2026-09-11，5.3.2=2026-08-10（Android 流卡死修复），5.3.1=2026-07-01，5.3.0=2025-12-16 | CHANGELOG.md 前 60 行 |

**Unity 可用性判断**：netstandard2.0 产物可被 Unity 2021（.NET Standard 2.1 profile）直接引用；零 UnityEngine 依赖；LangVersion 7.3 无 IL2CPP 裁剪风险。唯一连带依赖是日志库。

## 2. ResponseStartTimeout / ReadTimeout 精确语义

**ResponseStartTimeout**（默认 10s，`Configuration.cs:34`）
- 语义：「从发起 HTTP 请求到收到响应头」的上限，等价于 `HttpClient.Timeout`（`ConfigurationBuilder.cs:175-199`）；实现上就是赋给自建 client 的 `client.Timeout`（`EventSource.cs:307-314`），请求以 `HttpCompletionOption.ResponseHeadersRead` 发出（`EventSourceService.cs:99-101`），即 SendAsync 在头到达即完成。
- 不是 TCP 连接超时；net core/5+ 可用 `SocketsHttpHandler.ConnectTimeout` 精确控制（`ConfigurationBuilder.cs:184-190` 注释）。
- 边界：负值 → `Timeout.InfiniteTimeSpan`（`ConfigurationBuilder.cs:443-445`），可设无限。
- **注入 HttpClient 时不生效**：库不改用户 client 的任何属性，`ResponseStartTimeout` 被忽略（`ConfigurationBuilder.cs:389-391`），超时由用户 client 自身的 Timeout 决定。
- 平台差异【推测】：.NET Framework/旧 Mono 的 HttpClient.Timeout 可能覆盖到响应体读取阶段，会掐死长流；Unity Mono 上需实测。缓解=注入 Timeout=InfiniteTimeSpan 的 client，靠 ReadTimeout 兜底。

**ReadTimeout**（默认 5 分钟，`Configuration.cs:46`）
- 语义：**逐次读取的空闲超时（idle per read）**，不是总时长——每次 `ReadLineAsync`（字符串模式，`EventSourceService.cs:159-160`）或 `stream.ReadAsync`（UTF8 模式，`EventSourceService.cs:182-183`）单独计时，读到数据即重置。实现：`DoWithTimeout` 用 `new CancellationTokenSource(timeout)` + linked token 包裹（`AsyncHelpers.cs:17-58`），超时抛 `ReadTimeoutException`（`ReadTimeoutException.cs:9-21`）。
- 边界：负值→无限（`ConfigurationBuilder.cs:443-445`）；超时≠失败终结，触发 Error 事件→断开→**走重连**（见 §3）；服务器可发 SSE 注释行 `:` 心跳保活（`ConfigurationBuilder.cs:206-209`）。
- 实现细节值得抄：网络 socket 的 `ReadAsync` 不响应 CancellationToken（`EventSourceService.cs:179-181` 注释），须用 `AllowCancellation`（`AsyncHelpers.cs:64-94`，ContinueWith 包装+丢弃原 task 异常）才能可取消——这正是 Unity Mono 下同样会遇到的问题。

## 3. 重连 / 退避 / retry 字段 / 禁用重连

**重连环**：`StartAsync` 是 `while (ReadyState != ReadyState.Shutdown)`（`EventSource.cs:126-212`）——任何失败（HTTP 错误、ReadTimeout、流断）→ catch → 发 Error 事件 → `Close(Closed)` → 循环回顶退避重试。**没有 MaxRetries 配置**，唯一出口是 `Close()`（→Shutdown，`EventSource.cs:271-285`，取消当前请求 token + shutdown token，非用户提供则 Dispose HttpClient）。

**退避**（`ExponentialBackoffWithDecorrelation.cs`）：
- delay = `min * 2^n` 封顶 max，抖动 = `unjittered/2 + Random.Next(unjittered)/2`（:35-51）；默认 min 1s / max 30s（`Configuration.cs:22,28`）。用「天花板右移防溢出」实现，n 无界安全（:71-94）——实现质量高，值得直接借鉴。
- `BackoffResetThreshold` 默认 60s（`Configuration.cs:52`）：连接存活≥阈值→n 归零+清临时边界（`EventSource.cs:133-144`），防止偶发重连吃满 30s 上限。
- **SSE `retry:` 字段**：解析于 `EventSource.cs:449-458`，钳制到 [0, 3,600,000ms]（`Constants.cs:31`）；成为**粘性**服务端主导 min delay——清 n、换边界、活过 ResetBackoffN，只有新 retry 才覆盖（`ExponentialBackoffWithDecorrelation.cs:119-127`）。对 LLM 场景是干扰项：某些 LLM 网关会发 retry 字段，会劫持你的重连节奏。

**一次性 LLM 流如何禁用重连**（测试实证的两条路）：
1. **收到 `[DONE]` → `Close()`**：库没有 [DONE] 概念；在 `MessageReceived` 里判 `data == "[DONE]"` 后调 `Close()`。Close 同步置 Shutdown 并 Cancel 当前请求 token（`EventSource.cs:277-278`），读循环每轮检查该 token（`EventSourceService.cs:157,177`），于是 `StartAsync` 环退出——干净收尾。
2. **Error 处理器里 `Close()`**：官方测试 `NoReconnectAttemptIsMadeIfErrorHandlerClosesEventSource`（`test/LaunchDarkly.EventSource.Tests/EventSourceReconnectingTest.cs:210-228`）实证 401 后零重连。即「把自动重连改成由 Error 事件驱动的人工决策」是库设计支持的形态。

两处都要做：只拦 [DONE] 不拦 Error，则鉴权失败/网络断会无限重连；只拦 Error 不拦 [DONE]，正常收尾后流读到 EOF（`ReadLineAsync` 返回 null，`EventSourceService.cs:161-165`）会走 reconnect 环再发一次请求。

## 4. HttpClient 注入 / headers / auth / 取消 / 错误分类

- **注入**：`HttpClient(client)`（`ConfigurationBuilder.cs:395-399`）或 `HttpMessageHandler(handler)`（:373-377，给了 client 则忽略）；用户 client 生命周期自理、不 dispose（`EventSource.cs:280-284`）、属性不被改。
- **headers/auth**：`RequestHeader(s)`（`ConfigurationBuilder.cs:342-362`）逐条加到请求（`EventSourceService.cs:201-221`）；Authorization 格式错误的异常信息被消毒不泄值（:211-218）。auth=自己加 header，无特殊通道。默认自动补 `Accept: text/event-stream`、UA、`Cache-Control: no-cache`、`ExpectContinue=false`（:245-253）。
- **POST 流式**：`Method(HttpMethod)`（:407-411）+ `RequestBodyFactory`（:421-437，factory 因重连要重发）——OpenAI 式 POST SSE 可直接用。
- **取消**：**无公开 CancellationToken 参数**，`StartAsync()` 不收 token（`EventSource.cs:126`）；取消只能走 `Close()/Dispose()`。对 Unity 场景是硬伤：无法把「用户关面板/切场景」的 token 接进去。
- **错误分类**（可据此做用户提示）：
  - 非 2xx 或 204 → `EventSourceServiceUnsuccessfulResponseException`（带 StatusCode + Headers，`EventSourceService.cs:258-264`、`EventSourceServiceUnsuccessfulResponseException.cs:9-44`）；
  - 空响应体 / Content-Type ≠ `text/event-stream` / 非 UTF-8 编码 → `EventSourceServiceCancelledException`（`EventSourceService.cs:109-113,266-275`）；
  - 读超时 → `ReadTimeoutException`；自身 token 未请求的 OCE → 归一为 `TimeoutException`（`EventSource.cs:190-202`）。
  - ⚠️ 边界 bug：`response.Content.Headers.ContentType.MediaType`（`EventSourceService.cs:271`）在服务器**不回 Content-Type 头**时 ContentType 为 null → NRE（推测：.NET 标准行为下成立，未实机验）。部分 LLM 网关省 Content-Type 会踩。

## 5. Unity 桌面/后端传输参考性 · Mono/IL2CPP 风险

- **API 形态契合**：事件回调（`MessageReceived/Opened/Closed/Error`，`EventSource.cs:42-54`）+ 事件驱动重连，正好能承载「LLM 一次性流 + [DONE] 收尾 + 失败人工重试」。
- **风险 1（HttpClient 流式）**：库自己的 Android 修复是最强信号——`ReadBufferSize` 从默认提到 8192 以绕过 Xamarin.Android handler 的 4096 BufferedStream 卡死（`EventSourceService.cs:20-23`，CHANGELOG 5.3.2 SDK-2755）。Mono/IL2CPP 的 HttpClient 实现同样有平台怪癖（Timeout 覆盖范围、流式读缓冲），**必须实机验证**，不能想当然。
- **风险 2（无 CancellationToken）**：见 §4，取消语义只能绕 Close()。
- **风险 3（重连为默认态）**：需 [DONE]+Error 双拦截才静音（§3）。
- **风险 4（严格 Content-Type 校验）**：非 `text/event-stream` 即拒（含 NRE 边界），接入怪异网关要改。
- **正资产**：`EventParser`（97 行，纯函数）+ `ByteArrayLineScanner` + `Utf8ByteSpan` 三件是自包含零依赖、Apache-2.0 的 SSE 逐行解析实现（含 UTF8 字节路径省 string 分配，`PreferDataAsUtf8Bytes`，`EventSourceService.cs:117-121`），可直接 vendor。

## 6. 结论

**评分：4 / 5 —— reference（照抄语义，不建议直接 vendor 整库）**

- 语义层（双超时边界、退避+抖动+reset 阈值、retry 字段粘性、错误分类）是本项目「真流式传输」最值得对标的一手参考，实现质量高且有 contract-tests 与 12 个测试文件背书。
- 不 vendor 整库的理由：重连为默认态、无公开 CancellationToken、依赖 LaunchDarkly.Logging、Content-Type 严格校验+边界 NRE——四点都和一次性 LLM 流的目标形态相悖，绕过成本≈自己写。
- 可 vendor 的子集：`EventParser.cs` / `ByteArrayLineScanner.cs` / `Utf8ByteSpan.cs`（解析三件）+ `DoWithTimeout/AllowCancellation` 的取消包装思路（`AsyncHelpers.cs`）——后者思路抄、实现按 Unity 环境重写更稳。
