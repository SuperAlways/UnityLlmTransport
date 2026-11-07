# UnityLlmTransport

> **状态**：Phase 0/1/2 已完成（2021.3/6.3/6.6 + 2022.3.62f3c1，2026-11-07；2022.3 主基线 Mono/IL2CPP U1–U11 15/15）；Phase 3 开发中（worktree）。
> **定位**：Unity 运行时（AI mod 与原生 LLM/Agent 驱动的游戏）的**独立通讯底座**——在 Unity + netstandard2.1 条件下，承载 pi-ai / pi-agent-core 的**全部通讯需求**。PiUnityAgent 是首个消费者（通过其自有 Fetch 端口 + Host 适配器），但不是唯一消费者。

---

## 一、一句话目标

> 不是一个“OpenAI SSE 客户端”，而是**pi 通讯面的 Unity 宿主实现**：让所有 provider 协议、认证、流式、长任务、宿主事件都能只依赖一层可替换的通讯抽象。

---

## 二、覆盖范围（设计上）

### 传输

- HTTP/1.1（必需）；HTTP/2（可选）；
- SSE、chunked transfer-encoding、NDJSON；
- WebSocket（部分 provider/传输需要）；
- 流式请求体（如需要）与流式响应体；
- 连接池、重定向、压缩（可控）。

### 请求/响应能力

- Method、URL、headers、body（string/bytes/stream）；
- 状态码、响应 headers、流式响应体；
- 代理（系统/显式）、TLS/证书策略；
- 连接超时、首字节超时、流空闲超时；
- 取消传播（取消后尽快中止底层连接）；
- 错误分类：网络、HTTP、协议、解析、空闲、取消；
- 重试钩子/错误分类（重试策略由上层决定）。

### 认证与签名

- API Key 注入；
- OAuth token 注入与刷新钩子；
- AWS SigV4、GCP ADC/Service Account、Azure AD 等签名钩子的扩展点；
- 凭据存储抽象（具体实现可后置）。

### 宿主

- 核心程序集**不引用 UnityEngine**；
- 后台网络/解析 → 主线程事件分发；
- 游戏暂停/切场景/退出/域重载时可取消、无泄漏；
- 多 mod 共存。

### 测试

- 传输可注入 mock；
- CI 无网、无 key；
- 录制/回放、错误注入。

---

## 三、不做什么

- **不做 provider 协议解析**：OpenAI/Anthropic/Gemini 的 JSON 映射属于 `PiUnityAgent.Ai`（S4/S5）。
- **不做 agent loop / 工具执行**：属于 `PiUnityAgent.Core`。
- **不做 UI**：属于宿主/mod。
- **不服务非 Unity 引擎**：Unreal/Godot/自研 C++ 等属于原版 pi / pi.net 或新 transport 的范围。
- **不绑定 Unity 版本**：只提供宿主适配层，不要求升级引擎。
- **不把“整包缓冲”叫流式**。

---

## 三·五、为什么不是 .NET 8 后端组件

.NET 8 后端已经有完整的 `HttpClient` / pi.net，不需要我们再造一层。这个组件的存在理由只有一个：**Unity 运行时的约束**。

- Mono/IL2CPP、主线程、UWR 后端、游戏生命周期；
- 不能升级 Unity；`netstandard2.1`；
- 现有 mod 各写一套 UWR/裸 TCP 封装，需要统一。

核心抽象仍不引用 `UnityEngine`（为了单测和可替换），但产品范围是 **Unity 运行时**；.NET 8 后端只是可选的消费方，不是本组件的目标环境。

**三类宿主的选型**（同一套定义、不同组合）：

| 宿主 | 必需 | 可选/不需要 |
|---|---|---|
| AI mod（游戏锁死环境） | `PiUnityAgent` 自有 Fetch 端口 + Host 适配器 + `Abstractions` + `Sse` + UWR + `Hosts.Unity` | — |
| 原生 Unity AI 游戏（自研、可控制构建） | 可走 `PiUnityAgent` + Fetch 端口 + 适配器；也可**直接**用 `Abstractions` + `Sse` + 任一传输 | 可用我们的 UWR/HttpClient 适配器，也可注入自写 transport；`Hosts.Unity` 仅按需 |
| .NET 8 后端 | pi.net + HttpClient | 不需要本组件；若与原生 Unity 客户端混用，`Abstractions` 可作统一端口 |

> **B 方案（2026-11-07）**：`PiUnityAgent.Ai` 不直接依赖本组件的 `Abstractions`；它拥有自己的 Fetch 端口，由 `PiUnityAgent.Hosts.Unity` / `Ext` 适配器映射到 `IStreamingHttpTransport`。本组件的 `Abstractions` 仍是其对外共享契约，但不是 PiUnityAgent 的公共 API。接缝定义见 `PiUnityAgent/docs/07-通讯层集成契约.md`。

---

## 四、硬约束

- C# `netstandard2.1`；不使用 .NET 6/7/8/9 专有 API；
- Unity 2021.2+（实际版本由游戏锁定）；
- Mono 运行时优先，IL2CPP/AOT 需兼容；
- 核心库零 `UnityEngine` 引用；
- 许可证：MIT/Apache 类；
- 尽量零/少第三方依赖；不引入原生插件（除非单独说明）。

---

## 五、架构方向（待设计确认）

```
UnityLlmTransport.Abstractions   // 纯接口/DTO，无 Unity/HttpClient 依赖
        ▲
        │
UnityLlmTransport.HttpClient     // .NET 8 / 桌面 Unity
UnityLlmTransport.UnityWebRequest// Unity 前端（DownloadHandlerScript）
UnityLlmTransport.RawTcp         // 兜底（可选）
UnityLlmTransport.WebSocket      // 可选
        ▲
        │
UnityLlmTransport.Hosts.Unity    // 主线程 dispatch、生命周期
```

B 方案（2026-11-07）：`PiUnityAgent.Ai` 的 provider adapter 只依赖 **PiUnityAgent 自有的 Fetch 端口**；`PiUnityAgent.Hosts.Unity / Ext` 的适配器负责 `FetchFunction ↔ IStreamingHttpTransport`；具体传输实现通过宿主注入。本组件不拥有 PiUnityAgent 的公共端口。

---

## 六、与 PiUnityAgent 的关系

| 项目 | 负责 |
|---|---|
| **PiUnityAgent.Ai** | provider 协议、消息/事件模型、模型注册表、流式事件、**自有 Fetch 端口** |
| **PiUnityAgent.Hosts.Unity / Ext** | 适配器：`FetchFunction` ↔ `IStreamingHttpTransport`；Unity 宿主 / 主线程 |
| **UnityLlmTransport**（本项目） | 字节怎么出去、怎么回来、怎么取消、怎么认证、怎么送到 Unity 主线程；`Abstractions` 是本组件对外契约 |

依赖方向（B 方案，2026-11-07）：

```
PiUnityAgent.Ai  →  自有 Fetch 端口（不依赖本组件）
PiUnityAgent.Hosts.Unity/Ext  →  UnityLlmTransport.Abstractions（经适配器）
UnityLlmTransport.*  →  不引用 PiUnityAgent 的任何类型
```

接缝唯一权威：`PiUnityAgent/docs/07-通讯层集成契约.md`。本组件版本锁：`87db637`（Phase 2 终审）。

---

## 七、目录（当前）

```
UnityLlmTransport/
  README.md
  docs/
    00-核心需求.md
    01-Unity通讯问题陈述.md
    02-调研问题.md
    03-pi通讯需求矩阵.md
    04-Unity-AI-mod通讯问题清单.md
    05-版本与兼容基线.md
    06-参考项目复盘-Yaolao-WorldTalk-江湖有灵.md
    07-参考项目阅读与评估方法.md
    08-阅读笔记/
    09-参考项目评估矩阵.md
    10-架构设计.md
    11-开发阶段划分.md
    12-开发执行模型-spec-plan-subagent.md
    superpowers/
      specs/2026-11-06-phase0-spike-design.md
      plans/2026-11-06-phase0-spike.md
    SOP/
      README.md
      Unity多版本通讯测试-SOP-2026-11-06.md
    spike/
  scripts/
    assemble-phase2-unity.sh
  spikes/
    SseTestServer/                   # UWR harness 的本地 SSE 服务（127.0.0.1:5199）
    WsTestServer/
    UnityUwrSpike/
  src/
    UnityLlmTransport.Abstractions/
    UnityLlmTransport.Sse/
    UnityLlmTransport.UnityWebRequest.Core/   # 纯桥接/分类，netstandard2.1，零 UnityEngine
  tests/
    UnityLlmTransport.Tests/
    UnityLlmTransport.UnityWebRequest.Tests/
  unity/
    Phase2Harness/                   # Phase 2 Unity 侧 runner + build 脚本资产
    UnityLlmTransport.UnityWebRequest/  # UWR 适配层 Runtime 源 + link.xml
    UnityLlmTransport.Hosts.Unity/
```

`src/` 与 `tests/` 已随 Phase 1/2 落地：`UnityLlmTransport.Abstractions`（端口/DTO/装饰器/Mock）、`UnityLlmTransport.Sse`（vendor SSE 解析器）、`UnityLlmTransport.UnityWebRequest.Core`（队列/流/分类/请求计划，纯逻辑）、`tests/UnityLlmTransport.Tests`、`tests/UnityLlmTransport.UnityWebRequest.Tests`。`unity/` 为 Phase 2 的 Unity 侧源码（适配层 + 协程 Host + harness，经 `scripts/assemble-phase2-unity.sh` 装配进 Unity 工程）；2021.3/6.3/6.6 三版本 Mono/IL2CPP 实机结果见 `docs/spike/phase2-results.md`，2022.3 主基线补测 pending。

---

## 八、待定决策

1. Unity 前端直连是首要形态；.NET 8 后端不作为本组件目标（已有 HttpClient/pi.net）——**已定**；
2. 传输实现顺序：UWR（Phase 2 已过 2021.3/6.3/6.6 + 2022.3.62f3c1）→ HttpClient（Phase 3）→ RawTcp/WebSocket（Phase 5）——**已定**；
3. WebGL 是否进 M1：**M2 再做**；
4. 认证范围：API Key 先行，OAuth/SigV4 的接口形状在 Phase 4 补；
5. 重试策略归属：传输层只做分类；策略在 provider adapter（PiUnityAgent S4）——**已定**；
6. 独立 git 仓库：**已独立**；NuGet/UPM 包名与发布形态留 Phase 6；
7. PiUnityAgent 集成：**B 方案**（PiUnityAgent 自有 Fetch 端口；适配器在 PiUnityAgent 侧维护）——**已定**（2026-11-07）。

---

## 九、下一步

1. **Phase 2 follow-up · 2022.3.62f3c1 测试主基线**：已补测通过（Mono/IL2CPP U1–U11 15/15）；证据见 `docs/spike/phase2-results.md` 与 `docs/spike/evidence/phase2/`；游戏实际 f2 的最终游戏内验收属 PiUnityAgent S4；
2. **Phase 3 · HttpClient**：显式代理/TLS/桌面路径；`Consumers` 可用其作为 Fetch 端口的 .NET 实现；
3. **Phase 4 · Auth**：API Key/OAuth 注入、401 refresh、Finalizer；PiUnityAgent 适配器由 PiUnityAgent 侧维护，本组件提供契约测试消费者；
4. **Phase 5 · 可选传输**：WebSocket / RawTcp / WebGL（M2）；
5. **Phase 6 · Hardening**：录制回放、打包、全矩阵、tag/版本发布（当前 PiUnityAgent 以 commit `87db637` 锁定）。
