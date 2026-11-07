# UnityLlmTransport

> **状态**：Phase 0（Spike）/ Phase 1（Core）已完成（2026-11-07，已合入 main）；Phase 2（UWR）待开工。
> **定位**：Unity 运行时（AI mod 与原生 LLM/Agent 驱动的游戏）的**独立通讯底座**——在 Unity + netstandard2.1 条件下，承载 pi-ai / pi-agent-core 的**全部通讯需求**。PiUnityAgent 是首个消费者，但不是唯一消费者。

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
| AI mod（游戏锁死环境） | `Abstractions` + `Sse` + UWR 适配器 + `Hosts.Unity` | — |
| 原生 Unity AI 游戏（自研、可控制构建） | `PiUnityAgent` + `Abstractions` + `Sse`；传输实现任选 | 可用我们的 UWR/HttpClient 适配器，也可注入自写 transport；`Hosts.Unity` 仅按需 |
| .NET 8 后端 | pi.net + HttpClient | 不需要本组件；若与原生 Unity 客户端混用，`Abstractions` 可作统一端口 |

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

PiUnityAgent.Ai 的 provider adapter 只依赖 `Abstractions`，不依赖具体传输实现。

---

## 六、与 PiUnityAgent 的关系

| 项目 | 负责 |
|---|---|
| **PiUnityAgent.Ai** | provider 协议、消息/事件模型、模型注册表、流式事件 |
| **UnityLlmTransport**（本项目） | 字节怎么出去、怎么回来、怎么取消、怎么认证、怎么送到 Unity 主线程 |
| **PiUnityAgent.Hosts.Unity** | 把 Transport 接到具体 Unity 宿主 |

依赖方向：`PiUnityAgent.Ai` → `UnityLlmTransport.Abstractions`；具体传输实现通过宿主注入。

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
```

`src/` 与 `tests/` 已随 Phase 1 落地：`UnityLlmTransport.Abstractions`（端口/DTO/装饰器/Mock）、`UnityLlmTransport.Sse`（vendor SSE 解析器）、`tests/UnityLlmTransport.Tests`（53 项单测）。

---

## 八、待定决策

1. Unity 前端直连是首要形态；.NET 8 后端不作为本组件目标（已有 HttpClient/pi.net）；
2. 首批传输实现：HttpClient + UWR，是否现在做 RawTcp；
3. WebGL 是否进 M1；
4. 认证范围：API Key 先行，OAuth/SigV4 的接口形状何时补；
5. 重试策略归属：传输层只分类，策略在 provider adapter，还是提供可复用策略；
6. 是否独立 git 仓库、NuGet 包名/命名空间。

---

## 九、下一步

1. 用 `docs/02-调研问题.md` 做外部检索；
2. 基于检索结果写设计 spec（接口、实现、平台矩阵、偏差与代价）；
3. 设计通过后再建 `src/` 与 `tests/`。
