# Phase 2 · 三版本实机结果

> 日期：2026-11-07。环境：Windows 11 + Unity `2021.3.45f1` / `6000.3.28f1` / `6000.6.8f1`，各出 Mono2x + IL2CPP 两个 player 构建。
> 构建命令见 `unity/Phase2Harness/README.md`；SSE 测试服务 = `spikes/SseTestServer`（`127.0.0.1:5199`）。
> 原始日志：`D:/UnitySpikes/phase2-<ver>-<backend>-player.log`；摘录文件 `D:/UnitySpikes/phase2-<ver>-<backend>-results.txt`，已逐字入库 → `docs/spike/evidence/phase2/<ver>-<backend>-results.txt`（六组各 15/15 `|OK|`）。
> ⚠️ 2021.3 的构建目录是 `Builds/Phase2/2021.3.45f1c2/`（`Application.unityVersion` 带 c2 后缀，其余两版与编辑器版本号一致）。

**总裁决：三版本 × 双后端 = 6 组全量场景，每组 15/15 `|OK|`，无一处 `|FAIL|`。IL2CPP 门禁只要求 U1/U3/U5，实测同样 15/15。**

## Mono 矩阵（全量 U1–U11）

| 场景 | 2021.3.45f1c2 | 6000.3.28f1 | 6000.6.8f1 | 判据 |
|---|---|---|---|---|
| U1 流式 | OK status=200 open_ms=115 first_ms=132 total_ms=687 items=6 | OK open_ms=104 first_ms=119 total_ms=676 items=6 | OK open_ms=94 first_ms=108 total_ms=646 items=6 | open_ms < total_ms；items≥6 ✅ |
| U2 首字节快照 | OK status=200 content_type=True | OK status=200 content_type=True | OK status=200 content_type=True | status>0；content_type=True ✅ |
| U3 取消 | OK kind=Cancelled | OK kind=Cancelled | OK kind=Cancelled | kind=Cancelled/Aborted ✅ |
| U4 首字节超时 | OK kind=FirstByteTimeout | OK kind=FirstByteTimeout | OK kind=FirstByteTimeout | kind=FirstByteTimeout ✅ |
| U4 空闲超时 | OK kind=IdleTimeout | OK kind=IdleTimeout | OK kind=IdleTimeout | kind=IdleTimeout ✅ |
| U5 溢出 | OK kind=Overflow（65536 容量） | OK kind=Overflow | OK kind=Overflow | kind=Overflow ✅ |
| U6 500 错误体 | OK status=500 body={"error":"boom"} | OK status=500 body={"error":"boom"} | OK status=500 body={"error":"boom"} | status=500；body 含 boom ✅ |
| U7 TLS accept | OK status=200 | OK status=200 | OK status=200 | status=200 ✅ |
| U7 TLS default | OK kind=Tls | OK kind=Tls | OK kind=Tls | kind=Tls ✅ |
| U7 显式代理 | OK kind=Unsupported | OK kind=Unsupported | OK kind=Unsupported | kind=Unsupported ✅ |
| U8 gzip | OK items=6 first_ms=81 | OK items=6 first_ms=82 | OK items=6 first_ms=82 | items≥6 ✅ |
| U9 线程 | OK main=1 parse=5 observer=11 chunks=8 | OK main=1 parse=9 observer=7 | OK main=1 parse=12 observer=7 | parse/observer ≠ main ✅ |
| U10 重定向 Error | OK kind=Protocol | OK kind=Protocol | OK kind=Protocol | 有异常 ✅ |
| U10 重定向 Follow | OK status=200 items=6 | OK status=200 items=6 | OK status=200 items=6 | items≥6 ✅ |
| U11 Abort 残留 | OK at_abort=1 after_1s=1 | OK at_abort=1 after_1s=1 | OK at_abort=1 after_1s=1 | after_1s == at_abort ✅（读侧干净，判据边界见结论区 U11 说明） |

## IL2CPP 冒烟（要求 U1/U3/U5，实测全量通过）

| 场景 | 2021.3.45f1c2 | 6000.3.28f1 | 6000.6.8f1 | 判据 |
|---|---|---|---|---|
| U1 流式 | OK open_ms=63 first_ms=65 total_ms=615 items=6 | OK open_ms=66 first_ms=68 total_ms=617 items=6 | OK open_ms=67 first_ms=67 total_ms=617 items=6 | open_ms < total_ms；items≥6 ✅ |
| U3 取消 | OK kind=Cancelled | OK kind=Cancelled | OK kind=Cancelled | kind=Cancelled/Aborted ✅ |
| U5 溢出 | OK kind=Overflow | OK kind=Overflow | OK kind=Overflow | kind=Overflow ✅ |

其余 12 个场景（U2/U4×2/U6/U7×3/U8/U9/U10×2/U11）在三个 IL2CPP 构建里也全部 `|OK|`，逐行原文见下方"原始结果行"。

## 结论

- **gzip 门禁：通过 → `Compression` 能力保留。** 实测事实：三版本 UWR 对 gzip 响应**自动解压**，但响应头仍保留 `Content-Encoding: gzip`；Mono 的 `GZipStream` 只支持同步读。适配层设计（spec §5.7 已修订）：会话 `Body` 用 `GzipSniffReadStream` 按头部 `1f 8b` magic 嗅探——已解压字节直通；真 gzip 经 `AsyncOffloadStream(SyncQueueReadStream)` 在线程池解码，调用线程不阻塞。请求侧固定 `Accept-Encoding: identity`。U8 三版本 × 双后端 items=6 全过。
- **首字节时 responseCode/headers 可用性：通过。** U2 三版本 × 双后端 `status=200 content_type=True`——`StartAsync` 就绪时 `responseCode != 0` 且 `GetResponseHeaders()` 含 `content-type`，spec §5.3 实现门禁成立，无需暂停设计。
- **主线程零解析 / Abort 后读侧干净 / 溢出无卡死：通过。** U9 全部 parse/observer ≠ main；U11 `after_1s == at_abort`（1==1）；U5 三版本即时 `Overflow`，无卡死。
- **U11 判据边界（终审订正）**：观察块（observer chunk）只在读侧读取时经 `TeeStream` 触发，故 U11 证明的是**读侧干净**——abort 后读通道干净终止、1s 窗口无迟到观察块——**不直接证明引擎层残留回调（ReceiveData）已停**。引擎层抑制由 `StreamingDownloadHandler._aborted` 抑制旗设计 + Phase 0 实机证据支撑；ReceiveData 计数器直证**推迟到 Phase 3** 补测（在计时窗口内无读取动作，观察块计数天然不动，这是 U11 判据的结构性边界）。
- **已知遗留 / 观察（非阻塞）**：
  - U9 的 parse/observer 线程号随版本/后端波动（如 Mono 6/8、9/7、12/7；IL2CPP 5/7、8/7），仅线程号差异，语义（≠主线程）一致。
  - U7 依赖外网 `self-signed.badssl.com` 可达性，本次全通；断网环境该两格会因网络失败，属环境因素。
  - `ConnectTimeout` 在 UWR 下实际约束到响应结束（spec §5.2 已记偏离），非本矩阵判据。
  - 显式代理仍返回 `Unsupported`（适配层按计划拒绝，未实机验 407 隧道路径）。

## 原始结果行（证据，逐字）

> 证据入库：本节逐字行已提交至 `docs/spike/evidence/phase2/<ver>-<backend>-results.txt`。**2021.3 Mono** 为终审修复后复跑（见上）；**其余五组（2021 IL2CPP / 6000.3 Mono+IL2CPP / 6000.6 Mono+IL2CPP）保留修复前实机证据**——该修复只改变 abort 路径上错误入队与抑制旗的先后顺序（消息次序），所有判据行为与消息文本不受影响，故不重跑。

### Mono · 2021.3.45f1c2

> 本块为**终审修复后复跑**（错误分类先入队，见 git log `fix(phase2): 终审`）：重装配 + 重建 Mono + 实机 15/15 `|OK|`，日志 `D:/UnitySpikes/phase2-2021-mono-player-final.log`。与修复前相比仅时序/线程号波动（U1 open 99→115ms、U9 线程 6/8→5/11），判定行与消息文本逐一不变。

```
PHASE2 unity_version=2021.3.45f1c2
PHASE2 main_thread=1
PHASE2 U1_stream|OK|status=200|open_ms=115|items=6|first_ms=132|total_ms=687|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U2_snapshot|OK|status=200|content_type=True
PHASE2 U3_cancel|OK|cancel_error=TransportException kind=Cancelled code=- msg=Response aborted (Cancelled).
PHASE2 U4_first_byte_timeout|OK|kind=FirstByteTimeout
PHASE2 U4_idle_timeout|OK|kind=IdleTimeout
PHASE2 U5_overflow|OK|kind=Overflow|msg=Response exceeded ChunkQueue capacity of 65536 bytes.
PHASE2 U6_error_body|OK|status=500|body={"error":"boom"}
PHASE2 U7_tls_accept|OK|status=200
PHASE2 U7_tls_default|OK|kind=Tls
PHASE2 U7_explicit_proxy|OK|kind=Unsupported
PHASE2 U8_gzip|OK|items=6|first_ms=82|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U9_threads|OK|main=1|parse=5|observer=11|chunks=8
PHASE2 U10_redirect_error|OK|kind=Protocol
PHASE2 U10_redirect_follow|OK|status=200|items=6
PHASE2 U11_abort_residual|OK|chunks_at_abort=1|after_1s=1
```

### Mono · 6000.3.28f1

```
PHASE2 unity_version=6000.3.28f1
PHASE2 main_thread=1
PHASE2 U1_stream|OK|status=200|open_ms=104|items=6|first_ms=119|total_ms=676|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U2_snapshot|OK|status=200|content_type=True
PHASE2 U3_cancel|OK|cancel_error=TransportException kind=Cancelled code=- msg=Response aborted (Cancelled).
PHASE2 U4_first_byte_timeout|OK|kind=FirstByteTimeout
PHASE2 U4_idle_timeout|OK|kind=IdleTimeout
PHASE2 U5_overflow|OK|kind=Overflow|msg=Response exceeded ChunkQueue capacity of 65536 bytes.
PHASE2 U6_error_body|OK|status=500|body={"error":"boom"}
PHASE2 U7_tls_accept|OK|status=200
PHASE2 U7_tls_default|OK|kind=Tls
PHASE2 U7_explicit_proxy|OK|kind=Unsupported
PHASE2 U8_gzip|OK|items=6|first_ms=82|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U9_threads|OK|main=1|parse=9|observer=7|chunks=8
PHASE2 U10_redirect_error|OK|kind=Protocol
PHASE2 U10_redirect_follow|OK|status=200|items=6
PHASE2 U11_abort_residual|OK|chunks_at_abort=1|after_1s=1
```

### Mono · 6000.6.8f1

```
PHASE2 unity_version=6000.6.8f1
PHASE2 main_thread=1
PHASE2 U1_stream|OK|status=200|open_ms=94|items=6|first_ms=108|total_ms=646|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U2_snapshot|OK|status=200|content_type=True
PHASE2 U3_cancel|OK|cancel_error=TransportException kind=Cancelled code=- msg=Response aborted (Cancelled).
PHASE2 U4_first_byte_timeout|OK|kind=FirstByteTimeout
PHASE2 U4_idle_timeout|OK|kind=IdleTimeout
PHASE2 U5_overflow|OK|kind=Overflow|msg=Response exceeded ChunkQueue capacity of 65536 bytes.
PHASE2 U6_error_body|OK|status=500|body={"error":"boom"}
PHASE2 U7_tls_accept|OK|status=200
PHASE2 U7_tls_default|OK|kind=Tls
PHASE2 U7_explicit_proxy|OK|kind=Unsupported
PHASE2 U8_gzip|OK|items=6|first_ms=82|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U9_threads|OK|main=1|parse=12|observer=7|chunks=8
PHASE2 U10_redirect_error|OK|kind=Protocol
PHASE2 U10_redirect_follow|OK|status=200|items=6
PHASE2 U11_abort_residual|OK|chunks_at_abort=1|after_1s=1
```

### IL2CPP · 2021.3.45f1c2

```
PHASE2 unity_version=2021.3.45f1c2
PHASE2 main_thread=1
PHASE2 U1_stream|OK|status=200|open_ms=63|items=6|first_ms=65|total_ms=615|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U2_snapshot|OK|status=200|content_type=True
PHASE2 U3_cancel|OK|cancel_error=TransportException kind=Cancelled code=- msg=Response aborted (Cancelled).
PHASE2 U4_first_byte_timeout|OK|kind=FirstByteTimeout
PHASE2 U4_idle_timeout|OK|kind=IdleTimeout
PHASE2 U5_overflow|OK|kind=Overflow|msg=Response exceeded ChunkQueue capacity of 65536 bytes.
PHASE2 U6_error_body|OK|status=500|body={"error":"boom"}
PHASE2 U7_tls_accept|OK|status=200
PHASE2 U7_tls_default|OK|kind=Tls
PHASE2 U7_explicit_proxy|OK|kind=Unsupported
PHASE2 U8_gzip|OK|items=6|first_ms=83|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U9_threads|OK|main=1|parse=5|observer=7|chunks=8
PHASE2 U10_redirect_error|OK|kind=Protocol
PHASE2 U10_redirect_follow|OK|status=200|items=6
PHASE2 U11_abort_residual|OK|chunks_at_abort=1|after_1s=1
```

### IL2CPP · 6000.3.28f1

```
PHASE2 unity_version=6000.3.28f1
PHASE2 main_thread=1
PHASE2 U1_stream|OK|status=200|open_ms=66|items=6|first_ms=68|total_ms=617|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U2_snapshot|OK|status=200|content_type=True
PHASE2 U3_cancel|OK|cancel_error=TransportException kind=Cancelled code=- msg=Response aborted (Cancelled).
PHASE2 U4_first_byte_timeout|OK|kind=FirstByteTimeout
PHASE2 U4_idle_timeout|OK|kind=IdleTimeout
PHASE2 U5_overflow|OK|kind=Overflow|msg=Response exceeded ChunkQueue capacity of 65536 bytes.
PHASE2 U6_error_body|OK|status=500|body={"error":"boom"}
PHASE2 U7_tls_accept|OK|status=200
PHASE2 U7_tls_default|OK|kind=Tls
PHASE2 U7_explicit_proxy|OK|kind=Unsupported
PHASE2 U8_gzip|OK|items=6|first_ms=83|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U9_threads|OK|main=1|parse=8|observer=7|chunks=8
PHASE2 U10_redirect_error|OK|kind=Protocol
PHASE2 U10_redirect_follow|OK|status=200|items=6
PHASE2 U11_abort_residual|OK|chunks_at_abort=1|after_1s=1
```

### IL2CPP · 6000.6.8f1

```
PHASE2 unity_version=6000.6.8f1
PHASE2 main_thread=1
PHASE2 U1_stream|OK|status=200|open_ms=67|items=6|first_ms=67|total_ms=617|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U2_snapshot|OK|status=200|content_type=True
PHASE2 U3_cancel|OK|cancel_error=TransportException kind=Cancelled code=- msg=Response aborted (Cancelled).
PHASE2 U4_first_byte_timeout|OK|kind=FirstByteTimeout
PHASE2 U4_idle_timeout|OK|kind=IdleTimeout
PHASE2 U5_overflow|OK|kind=Overflow|msg=Response exceeded ChunkQueue capacity of 65536 bytes.
PHASE2 U6_error_body|OK|status=500|body={"error":"boom"}
PHASE2 U7_tls_accept|OK|status=200
PHASE2 U7_tls_default|OK|kind=Tls
PHASE2 U7_explicit_proxy|OK|kind=Unsupported
PHASE2 U8_gzip|OK|items=6|first_ms=84|data={"choices":[{"delta":{"content":"你"}}]}{"choices":[{"delta":{"content":"好"}}]}{"choices":[{"delta":{"content":"，"}}]}{"choices":[{"delta":{"content":"世"}}]}{"choices":[{"delta":{"content":"界"}}]}[DONE]
PHASE2 U9_threads|OK|main=1|parse=8|observer=7|chunks=8
PHASE2 U10_redirect_error|OK|kind=Protocol
PHASE2 U10_redirect_follow|OK|status=200|items=6
PHASE2 U11_abort_residual|OK|chunks_at_abort=1|after_1s=1
```
