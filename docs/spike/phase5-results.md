# Phase 5 · WebSocket 四版本实机结果

> 日期：2026-11-07。环境：Windows 11 + Unity `2022.3.62f3c1`（测试主基线）/ `2021.3.45f1`（最低边界，产物目录 `2021.3.45f1c2`）/ `6000.3.28f1`（6.3）/ `6000.6.8f1`（6.6），各出 Mono + IL2CPP player 一个。
> 构建命令与判据见 `unity/Phase5Harness/README.md`；测试服务 = `spikes/WsTestServer`（ws://127.0.0.1:5200/ws/，本地回环）；W5 走外网 `wss://ws.postman-echo.com/raw`（系统代理 127.0.0.1:7897 开启，ProxyOverride 含 `127.*;<local>`）。
> 原始日志：`D:/UnitySpikes/phase5-<ver>-<backend>-player.log`（2022.3 为 `phase5-2022.3.62f3c1-...`）；逐字证据在 `docs/spike/evidence/phase5/`（4 × mono + 4 × il2cpp）。

**总裁决：四版本 × (Mono, IL2CPP) = 8 组全量场景，每组 6/6 `|OK|`（W1–W6），无一处 `|FAIL|`。IL2CPP 门禁要求的 W1/W3/W4 全过，且 W2/W5/W6 亦全过。**

## Mono（W1–W6）

| 场景 | 2022.3.62f3c1 | 2021.3.45f1c2 | 6000.3.28f1 | 6000.6.8f1 | 判据 |
|---|---|---|---|---|---|
| W1 text | OK type=Text payload=echo:hello | OK type=Text payload=echo:hello | OK type=Text payload=echo:hello | OK type=Text payload=echo:hello | echo:hello ✅ |
| W2 utf8 | OK payload=echo:你好，世界 | OK payload=echo:你好，世界 | OK payload=echo:你好，世界 | OK payload=echo:你好，世界 | 中文回环 ✅ |
| W3 close | OK echo=echo:bye type=Close close=NormalClosure | OK echo=echo:bye type=Close close=NormalClosure | OK echo=echo:bye type=Close close=NormalClosure | OK echo=echo:bye type=Close close=NormalClosure | echo + Close(NormalClosure) ✅ |
| W4 abort | OK send_kind=Aborted | OK send_kind=Aborted | OK send_kind=Aborted | OK send_kind=Aborted | Abort 后 Send 报 Aborted ✅ |
| W5 remote wss | OK type=Text payload=hello | OK type=Text payload=hello | OK type=Text payload=hello | OK type=Text payload=hello | hello 回环 ✅ |
| W6 error port | OK kind=Connect | OK kind=Connect | OK kind=Connect | OK kind=Connect | Connect/Handshake ✅ |

## IL2CPP

| 场景 | 2022.3.62f3c1 | 2021.3.45f1c2 | 6000.3.28f1 | 6000.6.8f1 |
|---|---|---|---|---|
| W1 text | OK type=Text payload=echo:hello | OK type=Text payload=echo:hello | OK type=Text payload=echo:hello | OK type=Text payload=echo:hello |
| W2 utf8 | OK payload=echo:你好，世界 | OK payload=echo:你好，世界 | OK payload=echo:你好，世界 | OK payload=echo:你好，世界 |
| W3 close | OK echo=echo:bye type=Close close=NormalClosure | OK echo=echo:bye type=Close close=NormalClosure | OK echo=echo:bye type=Close close=NormalClosure | OK echo=echo:bye type=Close close=NormalClosure |
| W4 abort | OK send_kind=Aborted | OK send_kind=Aborted | OK send_kind=Aborted | OK send_kind=Aborted |
| W5 remote wss | OK type=Text payload=hello | OK type=Text payload=hello | OK type=Text payload=hello | OK type=Text payload=hello |
| W6 error port | OK kind=Connect | OK kind=Connect | OK kind=Connect | OK kind=Connect |

> 每组结果首两行（逐字）：`PHASE5 unity_version=<版本>`、`PHASE5 main_thread=1`——场景全部在 Unity 主线程驱动。

## 结论

- **四版本 Mono + IL2CPP 全过（8 组 × 6 = 48 判据，零 FAIL）**。Phase 5 验收门（Spec §9：四版本 Mono W1–W6 + IL2CPP W1/W3/W4）达成；IL2CPP 侧 W2/W5/W6 实际也全过，超出最低门禁。
- **ClientWebSocket 在 Unity 的真实行为**：
  - 本地 `ws://` 回环、UTF-8 文本、服务端主动 NormalClosure（echo 与 Close 从同一次 `ReceiveAsync` 枚举先后取出，W3 判据含 Task 4 的 `echo=` 段适配）、Abort 后 Send 归一 `Aborted`、错误端口归一 `Connect`——四版本 × 双后端行为完全一致，无版本分支。
  - 远程 `wss://`（TLS + 系统代理开启）四版本 × 双后端直连成功，`Capabilities` 声明的 `WebSocketOverTls | SystemProxy | TlsCertificateHandler` 与实测一致。
- **已知修复：Unity 2022.3 IL2CPP 的 async-iterator 缺陷（本阶段唯一产品改动）**。首轮 2022.3 IL2CPP 实测 W1 抛 `InvalidOperationException: Operation is not valid due to the current state of the object`（栈：`ManualResetValueTaskSourceCoreShared.CompletionSentinel → InvokeContinuation → ClientWebSocketSession.<ReceiveAsync>d__13.MoveNext → 迭代器 DisposeAsync`）——即消费方在 `await foreach` 中收到一条消息后 break（或提前 return）时，编译器生成的 `AsyncIteratorMethodBuilder` 在 DisposeAsync 续跑路径上触发 Unity 2022.3 IL2CPP corelib 的 ValueTaskSource 缺陷；2021.3/6.3/6.6 IL2CPP 与四版本 Mono 均无此问题。且该异常让场景跳过会话释放 → 泄漏的活会话把单线程 echo 服务的 `CloseAsync(None)` 永久挂起 → accept 循环卡死 → W2–W4 被连带污染成 Connect 失败/握手悬挂（连锁，非独立缺陷）。修复（minimal，两层）：① `ClientWebSocketSession.ReceiveAsync` 改为手写 `IAsyncEnumerable/IAsyncEnumerator`（DisposeAsync 是普通代码，不触编译器续跑；单枚举守卫/空闲超时/预算/Close/取消/分类语义逐条对齐原实现）；② Phase5Runner 场景加 try/finally 释放会话 + WsTestServer 改 per-connection 任务 + `CloseAsync` 5s 超时（坏连接只拖死自己）。修复后 8 组全绿；dotnet `UnityLlmTransport.WebSocket.Tests` 34/34 不变。
- **远程 wss 依赖外网/系统代理的记录**：本环境系统代理 `127.0.0.1:7897` 开启（ProxyOverride 含 `127.*;<local>`），W5 全部 8 格通过；未做"关闭系统代理"的对照（同 Phase 2/3 口径，遗留见下）。若目标环境无外网或代理拦截 wss，W5 应按 `Connect`/`Tls` 分类失败并归因环境，不影响 W1–W4/W6 本地判据。
- **已知遗留（不在本阶段）**：
  - WebGL：`ClientWebSocket` 不适用（M2 用 `.jslib` + 浏览器 WebSocket），本阶段不声明、不测试；
  - RawTcp 兜底：未实施（原 §7 的 S5-3，后续按需求另立）；
  - WS→SSE 回退 / 自动重连 / `WebSocketCached` 装饰器：属消费方/后续阶段；
  - 系统代理关闭对照：未测（本地判据不依赖代理；远程 wss 依赖外网）；
  - 显式代理：`ClientWebSocket` 仅支持系统代理，`WebSocketOptions.Proxy != System` 归一 `Unsupported`（Task 1 语义，本阶段未变）。

## 原始结果行（证据，逐字）

> 8 份逐字行已提交至 `docs/spike/evidence/phase5/<ver>-<backend>-results.txt`（各行前缀 `PHASE5 ` 为日志 tag）。构建日志 `D:/UnitySpikes/phase5-<ver>-<backend>.log` 判据行 `PHASE5_BUILD <backend> Succeeded errors=0`。8 组的 W1–W6 行内容完全一致（确定性输出），仅 `unity_version` 行按版本不同；下录 2022.3 主基线两组，其余见证据文件。

### Mono · 2022.3.62f3c1（测试主基线）

```
PHASE5 unity_version=2022.3.62f3c1
PHASE5 main_thread=1
PHASE5 W1_text|OK|type=Text|payload=echo:hello
PHASE5 W2_utf8|OK|payload=echo:你好，世界
PHASE5 W3_close|OK|echo=echo:bye|type=Close|close=NormalClosure
PHASE5 W4_abort|OK|send_kind=Aborted
PHASE5 W5_remote_wss|OK|type=Text|payload=hello
PHASE5 W6_error_port|OK|kind=Connect
```

### IL2CPP · 2022.3.62f3c1（测试主基线；首轮 FAIL → 修复后全绿的版本）

```
PHASE5 unity_version=2022.3.62f3c1
PHASE5 main_thread=1
PHASE5 W1_text|OK|type=Text|payload=echo:hello
PHASE5 W2_utf8|OK|payload=echo:你好，世界
PHASE5 W3_close|OK|echo=echo:bye|type=Close|close=NormalClosure
PHASE5 W4_abort|OK|send_kind=Aborted
PHASE5 W5_remote_wss|OK|type=Text|payload=hello
PHASE5 W6_error_port|OK|kind=Connect
```

> 首轮（修复前）2022.3 IL2CPP 的 FAIL 记录（留档，诊断驱动修复；证据日志 `D:/UnitySpikes/phase5-2022.3.62f3c1-il2cpp-diag2-player.log`）：
> `W1_text|FAIL|InvalidOperationException: Operation is not valid due to the current state of the object.`（迭代器 DisposeAsync 续跑触发 Unity 2022.3 IL2CPP `ManualResetValueTaskSourceCore` 缺陷）；
> `W2_utf8|FAIL|TransportException kind=Connect`、`W3_close|FAIL|...`、`W4_abort|FAIL|...`（泄漏会话卡死单线程服务的连锁，`Unable to connect to the remote server` / `Connection reset by peer`）；
> `W5_remote_wss|OK|type=Text|payload=hello`、`W6_error_port|OK|kind=Connect`（不经本地服务，未受污染）。
> 其余 6 组（3 版本 × Mono/IL2CPP）首轮即全绿，修复后复测维持全绿。
