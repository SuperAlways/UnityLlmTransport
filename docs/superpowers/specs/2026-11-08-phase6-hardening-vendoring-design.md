# Phase 6 · 稳定性加固与源码搬运设计 spec

> **状态**：设计稿，待用户 review。
> **阶段**：Phase 6（不发布包；聚焦稳定性/高可用、源码搬运确定性、发布验收）。
> **输入**：`docs/00-核心需求.md` R8/R15/R16、`docs/10-架构设计.md` §7、`docs/11-开发阶段划分.md` §8（已按本 spec 缩减）、Phase 1–5 结果。
> **日期**：2026-11-08

---

## 1. 背景与决策

Phase 0–5 已完成并合 main。当前分发决策（2026-11-08）：

- **不发布 NuGet/UPM/源码 zip**；pi（及后续消费方）直接**搬运源码**并钉 commit；
- 等消费方验收证明稳定性、明确分发需求后，再评估正式打包（NuGet/UPM/可选配置包）；
- 因此 P6 只做三件事：**稳定性/高可用加固、源码搬运指引、发布验收**。

录制/回放、fixture 库、telemetry 新公共包均**后置**（Mock 已满足 CI；避免在 pi 协议未稳定时录出过期 fixture）。

---

## 2. 范围

### 2.1 包含（必做）

| 交付 | 内容 |
|---|---|
| 稳定性测试 | dotnet soak：HttpClient/WebSocket/Auth 的重复连接、长流、取消、释放、上限；Unity 全矩阵重跑 |
| 稳定性修复 | 仅当 soak/审计发现真实缺陷；含 **WS 默认消息上限**（见 §5.1） |
| 源码搬运指引 | `docs/13-源码搬运与集成指引.md`：文件清单/排除项/唯一 IsExternalInit/link.xml/asmdef 两种方式/commit pin |
| 许可与版本 | 根 `LICENSE`（MIT）、`NOTICE`（vendor SSE MIT 归属）、`VERSION`（`0.6.0` 里程碑；不发布，无公共 tag） |
| 可观测/脱敏指引 | `docs/14-可观测与脱敏指引.md`：用现有 `ITransportObserver`；Authorization/x-api-key/token 不落日志；错误体截断 |
| Release 验收 | 全量构建/测试；四版本 Mono/IL2CPP 矩阵重跑；0 errors（测试警告清零/记录）；用户验收 |

### 2.2 不包含（后置/硬边界）

- NuGet / UPM / 源码 zip 打包与发布；
- WebSocket `WebSocketCached`、自动重连、WS→SSE 回退（消费方）；
- RawTcp / WebGL；
- 消费方（pi）代码、适配器、联调、游戏内验收；
- 录制/回放、fixture 库；
- 新 telemetry 公共包、`IdentityOptions`/prompt-cache 的新公共形状（如消费方提出，另立小 spec）；
- 不修改 Abstractions/Sse/HttpClient/Auth/WebSocket/UWR 的公共类型形状（唯一例外见 §5.1 的默认值语义）。

---

## 3. 稳定性测试（dotnet soak）

### 3.1 文件

```
tests/UnityLlmTransport.HttpClient.Tests/StabilityTests.cs
tests/UnityLlmTransport.WebSocket.Tests/StabilityTests.cs
tests/UnityLlmTransport.Auth.Tests/StabilityTests.cs
```

复用现有测试服务（Phase3TestSupport / WsTestServer 在测试内的 `WsTestSupport` / MockTransport）。

### 3.2 场景（最小但可判）

**HttpClient（`StabilityTests.cs`）**
1. `Sequential_SSE_Requests_No_Leak`：20 次 `/sse` Open→parse→Dispose，全部 6 items；无异常。
2. `Long_Response_Respects_MaxResponseBytes`：`/big` + `TimeoutTransport` `MaxResponseBytes=64KiB` → Overflow（已由 Phase3 覆盖，稳定性侧以 20 次循环验证不卡）。
3. `Cancel_And_Dispose_During_Stream`：10 次 `/sse` 中途取消/Dispose，无悬挂。
4. `Error_Body_Repeated`：20 次 `/sse-error` 500，status/body 可读；统计响应体不无限增长（服务端返回小体）。
5. `Injected_Client_Reuse`：注入 `HttpClient` 连续 10 次请求（连接池路径）全部成功。

**WebSocket（`StabilityTests.cs`）**
1. `Sequential_Connections_No_Leak`：30 次 `/ws/echo` connect→send→receive→Close/Dispose，无异常/无服务端卡死。
2. `Message_Size_Boundary`：`MaxMessageBytes` 恰好/超出一字节的两组消息；超出 → Overflow + Abort。
3. `Abort_Repeated`：20 次 connect→Abort→Dispose，资源不悬挂。
4. `Concurrent_Send_Under_Load`：10 个并发 Send（或 5 对）全部完成，无 BCL outstanding-send 错误。
5. `Close_Handshake_Repeated`：20 次正常 Close 握手，state Closed。

**Auth（`StabilityTests.cs`）**
1. `Retry_Refresh_Repeated`：20 次 401→refresh→200 管线，每次新 token 命中；无刷新泄漏。
2. `Middleware_Many_Requests`：同一 OAuth middleware 连续 50 次 Open，token 注入一致。

> 内存断言：**不做脆弱的 `GC.GetTotalMemory` 阈值硬门**；用“重复次数内无异常/无卡死/上限行为一致”为判据；内存/分配由代码审计与结果观察记录。

### 3.3 审计清单（先读后改）

- UWR：`MaxBufferedBytes` 默认 4MiB；溢出 Abort/Overflow；Abort 后残留回调丢弃（Phase 2 已测）。
- WS：`MaxMessageBytes` 默认（见 §5.1）；`IdleTimeout` 每帧；Close/Abort 幂等。
- HttpClient：响应体是流（无内部缓冲）；`MaxResponseBytes` 依赖 `TimeoutTransport`；错误体读取由消费方限制。
- Sse：`SseParserOptions.MaxBufferSize` 默认边界；超大事件 → `InvalidDataException`。
- Auth：token/secret 不进入异常消息；middleware 无日志。

若审计发现真实缺陷：最小修复 + 测试 + 记录到本 spec 的实现计划（不扩大公共形状）。

---

## 4. 源码搬运指引（`docs/13-源码搬运与集成指引.md`）

### 4.1 必读规则（写死）

- **复制范围**：
  - `src/UnityLlmTransport.Abstractions/**/*.cs`
  - `src/UnityLlmTransport.Sse/**/*.cs`
  - `src/UnityLlmTransport.HttpClient/**/*.cs`
  - `src/UnityLlmTransport.Auth/**/*.cs`
  - `src/UnityLlmTransport.WebSocket/**/*.cs`
  - `src/UnityLlmTransport.UnityWebRequest.Core/**/*.cs`
  - `unity/UnityLlmTransport.UnityWebRequest/Runtime/**/*.cs`
  - `unity/UnityLlmTransport.Hosts.Unity/Runtime/**/*.cs`
  - `unity/UnityLlmTransport.UnityWebRequest/link.xml`
- **排除**：`tests/`、`spikes/`、`unity/Phase*Harness/`、`scripts/`、`obj/`、`bin/`、`Library/`。
- **唯一 IsExternalInit**：各纯包都有 `Internal/IsExternalInit.cs`；若合并进同一程序集（Assembly-CSharp 或无 asmdef 的 mod），**只保留 Abstractions 的那份**，删除 Sse/HttpClient/Auth/WebSocket/UWR.Core 的副本，否则 CS0101。
- **link.xml**：`StreamingDownloadHandler`/`UnityCoroutineHost` 的 preserve 目标程序集名必须等于实际承载程序集（默认 `Assembly-CSharp`；用 asmdef 则改 `fullname`）。
- **asmdef 两种形态**：①无 asmdef（编译进 Assembly-CSharp，link.xml 默认）；②自定义 asmdef（需把 link.xml 的 `fullname` 改成 asmdef 程序集名，并引用 Abstractions/Sse/各纯包）。
- **版本**：消费方钉 `commit=<sha>`；不跟 `main` 漂移；升级按 commit diff 逐包搬运。
- **许可**：随源码保留 `LICENSE` + `NOTICE`（vendor `System.Net.ServerSentEvents` MIT 归属）。

### 4.2 最小接入示例（文档内）

```csharp
var host = new GameObject("LlmHost").AddComponent<UnityCoroutineHost>();
var transport = new UnityWebRequestTransport(host);
var stack = new TimeoutTransport(
    new RetryTransport(
        new MiddlewareTransport(
            new ObserverTransport(
                new FinalizerTransport(transport, signers),
                observer),
            middlewares),
        retryOptions, retryPolicy, authRefresh));
var session = await stack.OpenAsync(request, options, host.ShutdownToken);
var items = SseParser.Create(session.Body).EnumerateAsync();
```

（WebSocket/HttpClient/Auth 的示例分别列出。）

---

## 5. 稳定性修复（最小）

### 5.1 WebSocket 默认消息上限

现状：`WebSocketOptions.MaxMessageBytes == null` → 不限，恶意/异常服务端可造成内存膨胀。
P6 决定：`ClientWebSocketTransport` 在 `MaxMessageBytes == null` 时应用**安全默认 16 MiB**（常量 `DefaultMaxMessageBytes`），并在 `WebSocketOptions` 文档/README 说明；显式设置值优先；不修改 Abstractions 类型形状。

- 测试：默认下 16MiB+ 消息 → Overflow；显式 `MaxMessageBytes=3` 仍按 3。
- 偏离记录：默认值从“无限制”变为“16MiB”；如消费方需要无上限，显式设 `int.MaxValue`（不推荐）。

### 5.2 Release 警告清零

- 修掉现有测试警告（如 xUnit2013、CA2022）或在测试项目明确 `<NoWarn>` 并记录理由；
- Release 构建 0 errors；警告数记录在发布结果。

---

## 6. 发布验收

- dotnet 全部测试绿（原有 53 + 52 + 47 + 24 + 38 + 新增 stability）；
- Release build 0 errors；警告清零/记录；
- 四版本 Mono/IL2CPP 矩阵重跑：
  - Phase 2 UWR U1–U11（Mono/IL2CPP 四版本）；
  - Phase 3 HttpClient H1–H9（Mono 四版本）；
  - Phase 5 WebSocket W1–W6（Mono/IL2CPP 四版本）；
  - 结果/evidence 更新（phase2/3/5 results 只追加本轮记录或新文档；不改历史）。
- 源码搬运验证：按 `docs/13` 在干净 Unity 2022 工程装配 + 编译 + Phase5 W1/W3/W4 冒烟通过；
- `LICENSE`/`NOTICE`/`VERSION` 入库；
- 用户验收；pi 钉 commit。

---

## 7. 后置与风险

| 项 | 处理 |
|---|---|
| NuGet/UPM/源码 zip | 等 pi 验收与分发需求后再设计 |
| 录制/回放 fixtures | 等 provider 协议稳定后再做 |
| telemetry 公共包 | 现用 `ITransportObserver` + 文档；新包按需 |
| IdentityOptions/prompt-cache | 消费方按需另立小 spec；当前以 headers/middleware/偏离表承载 |
| RawTcp/WebGL | 不进本阶段 |
| 消费方 E2E/游戏内验收 | 在 pi 仓库 |

**风险**：
1. soak 测试循环次数多，Unity 矩阵时间成本高；以“每场景最小判据”控制。
2. 固定测试端口（5199/5201/5203/5271/5273）不可同机并行；串行执行。
3. 源码搬运的唯一 `IsExternalInit`/link.xml 程序集名是人工步骤，靠文档与验证工程兜底。
4. 默认 WS 16MiB 上限改变行为，需消费方知悉（显式 `int.MaxValue` 可解除）。

---

## 8. 验收标准

- [ ] 三个 StabilityTests 文件全绿（场景见 §3.2）；
- [ ] 审计清单逐项有结论（无缺陷则记录“已审”）；
- [ ] WS 默认 16MiB 上限实现 + 测试；
- [ ] `docs/13-源码搬运与集成指引.md` 完成，含文件清单/排除项/唯一 shim/link.xml/asmdef/commit pin；
- [ ] `docs/14-可观测与脱敏指引.md` 完成；
- [ ] `LICENSE`/`NOTICE`/`VERSION` 入库；
- [ ] 全量 dotnet 测试绿 + Release 0 errors（警告清零/记录）；
- [ ] 四版本矩阵重跑（Phase2/3/5）结果入库；
- [ ] 按 §4 指引装配的干净 Unity 工程编译 + W1/W3/W4 冒烟通过；
- [ ] 用户验收 + pi commit pin。

---

## 9. 参考

- `docs/11-开发阶段划分.md` §8（本 spec 缩减）
- `docs/13-源码搬运与集成指引.md`（本阶段产出）
- Phase 2/3/5 results 与 evidence
- `docs/10-架构设计.md` §7（线程/生命周期）、§9（测试策略）
