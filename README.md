# UnityLlmTransport

> **Unity 运行时的独立 LLM/Agent 通讯底座**——在 Unity + netstandard2.1 的硬约束下，满足 agent 与 LLM 客户端的全部通讯需求（SSE / 流式 / WebSocket / 认证 / 取消 / 主线程分发）。当前首要服务对象是 PiUnityAgent，但任何 Unity 侧 LLM/Agent 客户端都可复用。

[English](README.en.md) | 中文

| | |
|---|---|
| **版本** | `0.6.0`（见 `VERSION`） |
| **状态** | Phase 0–6 全部完成，实测可用（四版本 Mono/IL2CPP 实机矩阵全过） |
| **分发** | 源码搬运，消费方钉 commit（见 [docs/13](docs/13-源码搬运与集成指引.md)）；NuGet/UPM 打包后置 |
| **许可** | MIT（`LICENSE`；vendor 归属见 `NOTICE`） |
| **兼容** | Unity 2021.3.45f1 → 6000.6.8f1（四版本基线）；C# 9 / netstandard2.1 |
| **消费方** | PiUnityAgent（当前首要，经其自有 Fetch 端口 + 宿主适配器）；其他 AI mod / 原生 AI 游戏同样可用 |

---

## 一、愿景

AI mod 作者和原生 AI 游戏开发者面对的同一个困境：**大模型通讯在 Unity 里没有现成的底座**。

- .NET 后端那套 `HttpClient` + SSE 库在 Unity 里不可用或不可靠：Mono/IL2CPP 运行时、UWR 网络后端、主线程模型、游戏生命周期（暂停/切场景/退出/域重载）全都和服务器端假设不同；
- 现有 mod 各写一套 UWR/裸 TCP 封装，质量参差、无法复用、无法单测；
- 直接依赖某个 provider SDK（OpenAI/Anthropic…）会把协议、认证、传输焊死在一起，换 provider = 重写。

**本项目的回答**：把"字节怎么出去、怎么回来、怎么取消、怎么认证、怎么送到 Unity 主线程"收进一层**可替换、可单测、零 UnityEngine 引用**的通讯抽象。上层（provider 协议、agent loop、UI）只依赖抽象端口，不依赖任何具体网络栈。

> 一句话：不是某个 provider 的 SSE 客户端，而是 **Unity 侧 LLM/Agent 通讯面的宿主实现**——pi-ai / pi-agent-core 是当前对齐的通讯契约与首要消费方。

**三类宿主，同一套抽象**：

| 宿主 | 用法 |
|---|---|
| AI mod（游戏锁死 Unity 版本） | PiUnityAgent 自有 Fetch 端口 + 宿主适配器 → 本组件传输栈 |
| 原生 Unity AI 游戏 | 直接用 `Abstractions` + `Sse` + 任一传输实现，或同上走 PiUnityAgent |
| .NET 8 后端 | 不需要本组件（已有 HttpClient/pi.net）；`Abstractions` 可作统一端口 |

---

## 二、效果（当前实测状态）

全部为 2026-11-08 复跑结果（证据：`docs/spike/` + `docs/spike/evidence/`）：

**dotnet 单测**（`dotnet test`，226 项全绿）：

| 套件 | 覆盖 |
|---|---|
| Core（Abstractions/Sse/装饰器） | 53 — 端口契约、SSE 解析、超时/重试/中间件装饰器、Mock |
| UWR（UnityWebRequest） | 47 — 流式下载、队列、错误分类、Abort/释放 |
| HttpClient | 56 — 流式/取消/超时/显式代理/TLS/重定向 + 稳定性 soak |
| Auth | 26 — ApiKey/OAuth 注入、401 刷新管线、Signer + soak |
| WebSocket | 44 — 会话边界/超时/消息预算/并发 + 默认 16MiB 上限 + soak |

**Unity 实机矩阵**（Mono + IL2CPP）：

| 传输 | 2022.3.62f3c1（主基线） | 2021.3.45f1 | 6000.3.28f1 | 6000.6.8f1 |
|---|---|---|---|---|
| UWR 流式（U1–U11） | ✅ Mono + IL2CPP 15/15 | ✅ 同 | ✅ 同 | ✅ 同 |
| HttpClient（H1–H9） | ✅ Mono 12/12 | ✅ 同 | ✅ 同 | ✅ 同 |
| WebSocket（W1–W6） | ✅ Mono + IL2CPP 6/6 | ✅ 同 | ✅ 同 | ✅ 同 |

含远程 `wss://` 实测（Phase 5 W5）。已知保留：UWR 的 accept-all 证书/客户端证书路径 dotnet-only（未进 Unity 实测）。

**源码搬运验证**：干净 Unity 2022 空工程按 docs/13 手工搬运 78 个 `.cs`（8 目录）→ 编译 0 错误 → W1–W6 全过。

**门禁**：Release 构建 0 warning / 0 error；`grep UnityEngine src/` 零命中；`grep PiUnityAgent src/` 零命中（不依赖消费方）。

---

## 三、架构

```
              上层消费方（Unity 侧 LLM/Agent 客户端：PiUnityAgent.Ai / 游戏代码 / mod…）
                              │  只依赖抽象端口
                              ▼
        ┌─────────────────────────────────────────────┐
        │       UnityLlmTransport.Abstractions        │  纯接口/DTO/装饰器，零 UnityEngine
        │  IStreamingHttpTransport · ITransportSession │
        │  IWebSocketTransport · IWebSocketSession     │
        │  TransportRequest/Options/Capabilities/...   │
        └─────────────────────────────────────────────┘
              ▲                    ▲                    ▲
   ┌──────────┴─────┐   ┌──────────┴──────┐   ┌────────┴────────┐
   │ UnityWebRequest │   │   HttpClient    │   │    WebSocket    │
   │  (unity/ 运行时) │   │  (桌面/后端路径)  │   │ (ClientWebSocket)│
   └─────────────────┘   └─────────────────┘   └─────────────────┘
              ▲
   ┌──────────┴──────────────────────────────────────┐
   │            UnityLlmTransport.Hosts.Unity         │  协程 Host / 主线程 dispatch / 生命周期
   └──────────────────────────────────────────────────┘
```

### 包一览

| 包 | 位置 | 职责 | UnityEngine | 单测 |
|---|---|---|---|---|
| `Abstractions` | `src/` | 端口、DTO、能力声明、错误分类、装饰器、Mock | ✅ 零 | 53 |
| `Sse` | `src/` | vendor `System.Net.ServerSentEvents`（MIT）的 netstandard2.0 解析器 | ✅ 零 | （含 Core） |
| `HttpClient` | `src/` | `HttpClientTransport`/`Session`（ResponseHeadersRead 流式；显式代理/RequestFinalizer） | ✅ 零 | 56 |
| `Auth` | `src/` | `ApiKeyMiddleware`、`OAuthTokenMiddleware`（401 刷新）、`IRequestSigner`+`SignerFinalizer` | ✅ 零 | 26 |
| `WebSocket` | `src/` | `ClientWebSocketTransport`/`Session`、错误分类、16MiB 消息预算；手写接收枚举器（绕 IL2CPP corelib 缺陷） | ✅ 零 | 44 |
| `UnityWebRequest.Core` | `src/` | UWR 纯桥接/队列/分类（netstandard2.1） | ✅ 零 | （含 UWR 套件） |
| `UnityWebRequest` | `unity/` | UWR 适配层 Runtime（`DownloadHandlerScript` 流式）+ `link.xml`（IL2CPP 防剥离） | 使用 | 47（联合 Core 测） |
| `Hosts.Unity` | `unity/` | `UnityCoroutineHost`：主线程 dispatch、`ShutdownToken` 生命周期 | 使用 | 实机 |

### 装饰器栈（HTTP 路径）

```
TimeoutTransport → RetryTransport → MiddlewareTransport → ObserverTransport → FinalizerTransport → 具体传输
   (首字节/空闲/响应上限)  (401刷新/重试钩子)   (ApiKey/OAuth 注入)      (观测)        (签名)
```

每层独立、可裁剪、可替换；重试策略由上层通过 `IRetryPolicy` 注入，传输层只做错误分类（`TransportErrorKind`：网络/HTTP/协议/解析/空闲/取消）。

### 关键设计决策

- **能力声明代替版本判断**：`TransportCapabilities`（`Http2`/`SystemProxy`/`RequestFinalizer`/…）声明各传输能做什么；显式代理等不支持的能力报 `Unsupported`，上层据此选择传输。
- **流式是真流式**：`ITransportSession.Body` 是 `Stream`，边到边读；不把"整包缓冲"叫流式。
- **B 方案接缝（2026-11-07 定）**：`PiUnityAgent.Ai` 不依赖本组件；它拥有自己的 Fetch 端口，`PiUnityAgent.Hosts.Unity/Ext` 的适配器映射到 `IStreamingHttpTransport`。接缝唯一权威：`PiUnityAgent/docs/07-通讯层集成契约.md`。

---

## 四、快速上手

完整搬运步骤（复制范围/唯一 IsExternalInit/link.xml/asmdef/commit pin）见 **[docs/13-源码搬运与集成指引.md](docs/13-源码搬运与集成指引.md)**。最小接入（UWR + SSE）：

```csharp
var host = new GameObject("LlmHost").AddComponent<UnityCoroutineHost>();
var transport = new UnityWebRequestTransport(host);
var stack = new TimeoutTransport(
    new RetryTransport(
        new MiddlewareTransport(
            new ObserverTransport(
                new FinalizerTransport(transport, signers),
                observer),
            new IRequestMiddleware[] { new ApiKeyMiddleware("key") }),
        new RetryOptions { MaxRetries = 2 },
        null,
        null));
var session = await stack.OpenAsync(request, new TransportOptions(), host.ShutdownToken);
var items = SseParser.Create(session.Body).EnumerateAsync();
```

WebSocket：`new ClientWebSocketTransport()`（消息默认上限 16MiB，`WebSocketOptions.MaxMessageBytes` 显式值优先）；HttpClient（桌面/后端）：`new HttpClientTransport()`。

可执行用法示例：各 `tests/*Tests/` 下的测试即用法文档；Unity 侧参见 `unity/Phase*Harness/`。

---

## 五、开发 Agent 指路表

**改代码前必读对应行；数值/契约以所指文档为准，不要凭记忆写。**

| 你要做 | 先读 | 入口/命令 |
|---|---|---|
| 把源码搬运进 Unity 工程 | `docs/13`（复制范围、唯一 IsExternalInit、link.xml、asmdef 两形态） | `src/` + `unity/` 8 目录，pin commit |
| 理解/修改架构与抽象 | `docs/10-架构设计.md` | `src/UnityLlmTransport.Abstractions/` |
| 新增/修改传输实现 | `docs/10` + `docs/05`（能力声明规则） | 对应 `src/UnityLlmTransport.*`；能力矩阵在 `docs/05` §3 |
| 动认证/签名 | `docs/14-可观测与脱敏指引.md`（凭据不落日志） | `src/UnityLlmTransport.Auth/` |
| 加日志/观测 | `docs/14`（脱敏规则） | `ITransportObserver` + `ObserverTransport` |
| 跑测试 | — | 见下方命令 |
| 加测试 | 现有套件风格 + `tests/Phase3TestSupport/`（进程内 Kestrel） | 对应 `tests/*Tests/` |
| Unity 实机验证 | `docs/SOP/Unity多版本通讯测试-SOP-2026-11-06.md` | `unity/Phase*Harness/` + `scripts/assemble-phase*-unity.sh` |
| 查版本兼容/支持定义 | `docs/05-版本与兼容基线.md` | 四版本基线 + 兼容矩阵 |
| 查需求边界 | `docs/00-核心需求.md` + `docs/03-pi通讯需求矩阵.md` | — |
| 发起新开发阶段 | `docs/12-开发执行模型-spec-plan-subagent.md` | `docs/superpowers/specs|plans/`（brainstorming→writing-plans→subagent-driven-development 技能链） |
| 查 pi 侧接缝 | `PiUnityAgent/docs/07-通讯层集成契约.md`（其仓库） | 本仓库不包含 PiUnityAgent 代码 |

**测试命令**（在仓库根，用裸 `dotnet`）：

```bash
dotnet build UnityLlmTransport.sln -c Release          # 门禁：0 warning / 0 error
dotnet test tests/UnityLlmTransport.Tests/             # 53
dotnet test tests/UnityLlmTransport.UnityWebRequest.Tests/   # 47
dotnet test tests/UnityLlmTransport.HttpClient.Tests/  # 56
dotnet test tests/UnityLlmTransport.Auth.Tests/        # 26
dotnet test tests/UnityLlmTransport.WebSocket.Tests/   # 44
```

注意：固定测试端口（5199/5201/5203/5211/5233/5234/5271/5273），同机不可并行跑套件。

---

## 六、硬约束（不可协商）

1. **C# 9 / netstandard2.1**——不用 .NET 6+ 专有 API（`System.Threading.Lock`、`Task.WaitAsync` 等禁止）；
2. **`src/` 零 UnityEngine 引用**——门禁 grep 验证；Unity 适配只进 `unity/`；
3. **零/少第三方依赖**——SSE 为 vendor 源码（MIT，见 `NOTICE`）；不引入原生插件；
4. **Unity 2021.3.45f1 最低**，四版本基线（2022.3.62f3c1 主基线 / 2021.3.45f1 / 6000.3.28f1 / 6000.6.8f1）；Mono 优先，IL2CPP 必须兼容（`link.xml`/`[Preserve]`）；
5. **本体层必配单测**——改 `src/` 必跑五套件 + Release 0 warning 门禁；
6. **WebGL 不支持**（M2 再议，方案 `.jslib` + fetch）；显式代理走 HttpClient，UWR 仅系统代理。

---

## 七、边界：不做什么

- **不做 provider 协议解析**——OpenAI/Anthropic/Gemini 的 JSON 映射属于 `PiUnityAgent.Ai`；
- **不做 agent loop / 工具执行**——属于 `PiUnityAgent.Core`；
- **不做 UI**——属于宿主/mod；
- **不服务非 Unity 引擎**——Unreal/Godot 等属于原版 pi / pi.net；
- **不绑定 Unity 版本**——只提供适配层，不要求升级引擎；
- **不引用 PiUnityAgent 的任何类型**——依赖方向单向：适配器在消费方仓库。

---

## 八、目录

```
UnityLlmTransport/
  LICENSE · NOTICE · VERSION
  README.md / README.en.md
  docs/                # 00-14 设计与指引 + SOP + spike 证据（见下方索引）
  src/                 # 六个纯 .NET 包（零 UnityEngine）：Abstractions / Sse / HttpClient /
                       #   Auth / WebSocket / UnityWebRequest.Core
  unity/               # Unity 侧：UnityLlmTransport.UnityWebRequest(Runtime+link.xml) /
                       #   Hosts.Unity / Phase2·3·5Harness
  tests/               # 五个测试工程 + Phase3TestSupport(进程内 Kestrel)
  scripts/             # assemble-phase2/3/5-unity.sh（装配进 Unity 工程）
  spikes/              # 本地测试服务（SseTestServer 5199 / Phase3TestServer 5201·5233·5234 / WsTestServer）
```

---

## 九、文档索引

| 文档 | 内容 |
|---|---|
| `docs/00-核心需求.md` | 需求与范围（R 编号需求清单） |
| `docs/01-Unity通讯问题陈述.md` | Unity 通讯问题的完整陈述 |
| `docs/02-调研问题.md` / `docs/07` / `docs/08` / `docs/09` | 参考项目调研方法、阅读笔记、评估矩阵 |
| `docs/03-pi通讯需求矩阵.md` | pi 对通讯层的需求矩阵（消费方需求权威） |
| `docs/04-Unity-AI-mod通讯问题清单.md` | 现有 AI mod 的通讯问题清单 |
| `docs/05-版本与兼容基线.md` | 四版本基线、兼容矩阵、支持定义 |
| `docs/06-参考项目复盘-*.md` | Yaolao/WorldTalk/江湖有灵 复盘 |
| `docs/10-架构设计.md` | 架构权威（分层/线程/生命周期/测试策略） |
| `docs/11-开发阶段划分.md` | Phase 0–6 划分与状态 |
| `docs/12-开发执行模型-spec-plan-subagent.md` | spec→plan→subagent 开发流程 |
| `docs/13-源码搬运与集成指引.md` | **消费方搬运指南（复制范围/IsExternalInit/link.xml/pin）** |
| `docs/14-可观测与脱敏指引.md` | `ITransportObserver` 观测点 + 脱敏规则 |
| `docs/SOP/` | Unity 多版本实机测试 SOP |
| `docs/spike/` | 各阶段实测结果 + `evidence/` 原始记录 |

---

## 十、状态与后置项

**已完成**：Phase 0（spike/IL2CPP 剥离实证）→ 1（核心抽象+SSE）→ 2（UWR）→ 3（HttpClient）→ 4（Auth）→ 5（WebSocket）→ 6（稳定性加固/搬运指引/发布验收）。详见 `docs/11`。

**后置（等需求触发，未排期）**：

| 项 | 触发条件 |
|---|---|
| NuGet / UPM / zip 打包 | pi 验收后明确分发需求 |
| 录制/回放 fixture | provider 协议稳定后 |
| RawTcp 兜底 | 有传输不可用场景实证 |
| WebGL（`.jslib` + fetch） | M2 立项 |
| WS→SSE 回退 / 自动重连 / `WebSocketCached` | 消费方需求 |
| telemetry 公共包 | 现用 `ITransportObserver` 够用 |
| 消费方 E2E / 游戏内验收 | 在 PiUnityAgent 仓库执行 |
