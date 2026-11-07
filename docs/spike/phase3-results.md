# Phase 3 · HttpClient 三版本 Mono 结果

> 日期：2026-11-07。环境：Windows 11 + Unity `2021.3.45f1` / `6000.3.28f1` / `6000.6.8f1`，各出 Mono2x player 一个。
> 构建命令见 `unity/Phase3Harness/README.md`；测试服务 = `spikes/Phase3TestServer`（HTTP 5201 / HTTPS 5233 / 显式代理 5234，全部本地自签/回环，无外网依赖）。
> 原始日志：`D:/UnitySpikes/phase3-<ver>-mono-player.log`；证据副本在 `docs/spike/evidence/phase3/`（每组 2 行头 + 12 行 `|OK|`）。
> ⚠️ 2021.3 的构建目录是 `Builds/Phase3/2021.3.45f1c2/`（`Application.unityVersion` 带 c2 后缀，其余两版与编辑器版本号一致）。

**总裁决：三版本 × Mono = 3 组全量场景，每组 12/12 `|OK|`（H1–H9），无一处 `|FAIL|`。本阶段只跑 Mono（IL2CPP 不声明）。**

## 矩阵

| 场景 | 2021.3.45f1c2 | 6000.3.28f1 | 6000.6.8f1 | 判据 |
|---|---|---|---|---|
| H1 流式 | OK status=200 open_ms=332 total_ms=870 items=6 | OK open_ms=174 total_ms=697 items=6 | OK open_ms=193 total_ms=747 items=6 | open_ms < total_ms；items≥6 ✅ |
| H2 取消 | OK kind=Cancelled | OK kind=Cancelled | OK kind=Cancelled | Cancelled/Aborted ✅ |
| H3 首字节超时 | OK kind=FirstByteTimeout | OK kind=FirstByteTimeout | OK kind=FirstByteTimeout | FirstByteTimeout ✅ |
| H3 空闲超时 | OK kind=IdleTimeout | OK kind=IdleTimeout | OK kind=IdleTimeout | IdleTimeout ✅ |
| H4 500 错误体 | OK status=500 body={"error":"boom"} | OK status=500 body={"error":"boom"} | OK status=500 body={"error":"boom"} | status=500；body 含 boom ✅ |
| H5 gzip | OK items=6 | OK items=6 | OK items=6 | items≥6 ✅ |
| H6 重定向 Error | OK kind=Protocol | OK kind=Protocol | OK kind=Protocol | 有异常 ✅ |
| H6 重定向 Follow | OK status=200 items=6 | OK status=200 items=6 | OK status=200 items=6 | items≥6 ✅ |
| H7 TLS default | OK kind=Tls | OK kind=Tls | OK kind=Tls | Tls ✅ |
| H7 TLS accept | OK status=200 | OK status=200 | OK status=200 | status=200 ✅ |
| H8 显式代理 | OK status=200 body=proxied | OK status=200 body=proxied | OK status=200 body=proxied | status=200；body=proxied ✅ |
| H9 headers/线程 | OK main=1 parse=6 skip=False test=True | OK main=1 parse=12 skip=False test=True | OK main=1 parse=9 skip=False test=True | parse ≠ main；skip=False；test=True ✅ |

## 结论

- **三版本 Mono 全过**：H1–H9 共 12 判据 × 3 版本全 `|OK|`，Phase 3 验收门（Spec §9 "三版本 Unity Mono H1–H9"）达成。运行版本与来源：三版 player 均从 worktree HEAD `8f87b49`（Task 6 终审修复提交）装配构建（2021 版在取证前重建，排除修复前旧产物）。
- **Mono 下 HttpClient 真流式：成立。** H1 三版本 `open_ms` ≪ `total_ms`（332/870、174/697、193/747 ms）——响应头就绪后 body 仍持续增量到达，SSE 解析器在流上逐块产出 6 items，不是"整包缓冲后一次性给"。
- **取消/超时语义与 dotnet 侧一致。** H2 三版本 `kind=Cancelled`（连接被中止）；H3 两个看门狗 `FirstByteTimeout`/`IdleTimeout` 均按期触发。这两条在 2021 Mono 上曾暴露真缺陷（见下"终审修复"），修复后行为与 .NET 8 侧对齐。
- **TLS 默认/accept-all 三版本一致。** H7 default 全部归一为 `kind=Tls`（本地自签证书拒绝）；accept-all 全部 `status=200`。注意：Phase 3 的 H7 用 `spikes/Phase3TestServer` 本地自签端点（5233），不依赖外网 badssl，与 Phase 2 U7 的环境依赖不同。
- **显式 `WebProxy` 三版本一致。** H8 三版本经显式代理（5234）取回 `body=proxied`，`status=200`——UWR 拿不到的显式代理能力在 HttpClient 传输上三版本实测可用。
- **gzip 自动解压后的 `Content-Encoding` 观察（记录，不改实现）**：H5 只产出 items≥6 判据，未读响应头，故本 harness 未直接采样解压后的 `Content-Encoding` 值；handler 映射按 `options.Decompress` 走 `AutomaticDecompression`（`HttpClientHandlerFactory.cs`），.NET 侧行为是解压后剥离该头（与 UWR 自动解压但保留头的 Phase 2 观察不同——此差异在 dotnet 单测覆盖解压正确性，头字段是否可观察不在本阶段判据内）。头保留/剥离的实机直证留待有需要时补一格 H5b。
- **H9 主线程零解析成立**：parse 线程号 6/12/9 均 ≠ main=1；`X-Skip`（null 值）未上协议头，`X-Test` 上头成功。
- **终审修复（Task 6 实机门禁暴露的两真缺陷，本批已合入）**：
  1. **`TimeoutTransport`（Phase 1 共享装饰器）超时 CTS 触发时主动 Abort 内层会话**——此前只取消读等待，Mono 的 `WebRequest` 实现无视读中段的取消令牌，H3 两个看门狗在 2021 Mono 上不触发；修复后三版本 + dotnet 三套件全绿（`UnityLlmTransport.Tests` 53/53、`UnityLlmTransport.UnityWebRequest.Tests` 47/47、`UnityLlmTransport.HttpClient.Tests` 52/52）。
  2. **`HttpClientErrorClassifier` 把 Mono `WebException` 的 `TrustFailure`/`SecureChannelFailure` 归一为 `Tls`**（原误归 `Connect`）——Mono 的 SslStream 异常在Unity Mono 下以 `WebException` 形态浮出，不带 `AuthenticationException`；修复后 H7 default 三版本 `kind=Tls`。
- **已知遗留 / 观察（非阻塞）**：
  - HTTP/2 未声明：`HttpClientTransport.DefaultCapabilities` 不含 `Http2`（netstandard2.1 的 `HttpClientHandler` 无版本控制 API），6.3/6.6 也不假装支持；后续需要时走 capability 协商另立。
  - IL2CPP 未声明：本阶段只跑 Mono；`HttpClient` 包走 IL2CPP 需 AOT 冒烟（无 handler 重写依赖，风险低于 UWR 适配层），未做不声明。
  - H9 线程号随版本波动（6/12/9），仅线程号差异，语义（≠主线程）一致。
  - 2021.3 Mono `open_ms` 波动大（Task 6 首跑 193ms → 本跑 332ms），首连含 TLS/连接建立抖动，不影响 open<total 判据。
  - 测试服务 `Phase3TestSupport`（ASP.NET Core）只进测试，不进产品包。

## 原始结果行（证据，逐字）

> 证据入库：三组逐字行已提交至 `docs/spike/evidence/phase3/<ver>-mono-results.txt`（各行前缀 `PHASE3 ` 为日志 tag）。构建日志 `D:/UnitySpikes/phase3-<ver>-mono.log` 判据行 `PHASE3_BUILD mono Succeeded errors=0`。

### Mono · 2021.3.45f1c2

```
PHASE3 unity_version=2021.3.45f1c2
PHASE3 main_thread=1
PHASE3 H1_stream|OK|status=200|open_ms=332|items=6|total_ms=870|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE3 H2_cancel|OK|cancel_error=TransportException kind=Cancelled code=- msg=Response aborted (Cancelled).
PHASE3 H3_first_byte_timeout|OK|kind=FirstByteTimeout
PHASE3 H3_idle_timeout|OK|kind=IdleTimeout
PHASE3 H4_error_body|OK|status=500|body={"error":"boom"}
PHASE3 H5_gzip|OK|items=6|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE3 H6_redirect_error|OK|kind=Protocol
PHASE3 H6_redirect_follow|OK|status=200|items=6
PHASE3 H7_tls_default|OK|kind=Tls
PHASE3 H7_tls_accept|OK|status=200|body=ok
PHASE3 H8_explicit_proxy|OK|status=200|body=proxied
PHASE3 H9_headers_threads|OK|main=1|parse=6|skip=False|test=True
```

### Mono · 6000.3.28f1

```
PHASE3 unity_version=6000.3.28f1
PHASE3 main_thread=1
PHASE3 H1_stream|OK|status=200|open_ms=174|items=6|total_ms=697|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE3 H2_cancel|OK|cancel_error=TransportException kind=Cancelled code=- msg=Response aborted (Cancelled).
PHASE3 H3_first_byte_timeout|OK|kind=FirstByteTimeout
PHASE3 H3_idle_timeout|OK|kind=IdleTimeout
PHASE3 H4_error_body|OK|status=500|body={"error":"boom"}
PHASE3 H5_gzip|OK|items=6|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE3 H6_redirect_error|OK|kind=Protocol
PHASE3 H6_redirect_follow|OK|status=200|items=6
PHASE3 H7_tls_default|OK|kind=Tls
PHASE3 H7_tls_accept|OK|status=200|body=ok
PHASE3 H8_explicit_proxy|OK|status=200|body=proxied
PHASE3 H9_headers_threads|OK|main=1|parse=12|skip=False|test=True
```

### Mono · 6000.6.8f1

```
PHASE3 unity_version=6000.6.8f1
PHASE3 main_thread=1
PHASE3 H1_stream|OK|status=200|open_ms=193|items=6|total_ms=747|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE3 H2_cancel|OK|cancel_error=TransportException kind=Cancelled code=- msg=Response aborted (Cancelled).
PHASE3 H3_first_byte_timeout|OK|kind=FirstByteTimeout
PHASE3 H3_idle_timeout|OK|kind=IdleTimeout
PHASE3 H4_error_body|OK|status=500|body={"error":"boom"}
PHASE3 H5_gzip|OK|items=6|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE3 H6_redirect_error|OK|kind=Protocol
PHASE3 H6_redirect_follow|OK|status=200|items=6
PHASE3 H7_tls_default|OK|kind=Tls
PHASE3 H7_tls_accept|OK|status=200|body=ok
PHASE3 H8_explicit_proxy|OK|status=200|body=proxied
PHASE3 H9_headers_threads|OK|main=1|parse=9|skip=False|test=True
```
