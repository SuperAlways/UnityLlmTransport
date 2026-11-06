# 01 · Unity 通讯问题陈述

> **用途**：把“为什么要单独做一个通讯组件”讲清楚，供检索、设计和评审共用。
> **日期**：2026-11-06

---

## 1. 需求复述

我们要的不是“Unity 里接 OpenAI SSE”的单一客户端，而是：

> 在 **Unity + netstandard2.1 + AI mod 运行条件**（Mono/IL2CPP、主线程、游戏生命周期、不能升级引擎）下，承载 `pi-ai` / `pi-agent-core` 的**全部通讯需求**的独立组件。

“全部通讯需求”包括：

### 对上

- OpenAI Chat Completions、OpenAI Responses、Azure Responses、Codex Responses；
- Anthropic Messages；
- Google Gemini / Vertex；
- Bedrock Converse（SigV4）；
- Mistral、Cloudflare、OpenRouter、本地 OpenAI-compatible；
- SSE、chunked、NDJSON、WebSocket。

### 对内

- 流式请求/响应、双向流（如需要）；
- headers、auth 注入、请求签名（API Key / OAuth / SigV4 / ADC / Azure AD）；
- 代理、TLS、重定向、连接池、HTTP/2（可选）、压缩；
- 连接超时、首字节超时、流空闲超时、取消传播；
- 重试分类/钩子（策略由上层决定）；
- Deferred / 长任务、图片、分类等通讯形态；
- 错误分类：网络、HTTP、协议、解析、空闲、取消。

### 对下

- 核心库不引用 `UnityEngine`；
- 后台网络/解析 → Unity 主线程事件；
- 游戏暂停/切场景/退出/域重载可取消、无泄漏；
- 可注入 mock、录制回放、CI 无网可测。

---

## 2. 问题不在“Unity 不会发 HTTP”

而在**没有一种 Unity 原生传输能覆盖 pi 的整个通讯面**，同时满足主线程、netstandard2.1、IL2CPP 与游戏生命周期约束。

| 需求 | UnityWebRequest | HttpClient（Unity Mono/IL2CPP） | 裸 TCP/SslStream | .NET 8 后端 |
|---|---|---|---|---|
| HTTP/1.1 | ✅ | ✅（平台差异） | ✅（手写） | ✅ |
| 真流式 SSE | ⚠️ 仅 `DownloadHandlerScript`；默认整包缓冲 | ⚠️ 桌面可行；Unity 下流式/TLS/代理有差异 | ✅ | ✅ |
| chunked 解码 | UWR 处理 | HttpClient 处理 | ❌ 自己写 | ✅ |
| NDJSON | 可 | 可 | 可（手写） | 可 |
| WebSocket | ❌ | ⚠️ `ClientWebSocket` 平台差异 | 需自己实现 | ✅ |
| HTTP/2 | ❌ 不可控 | ⚠️ 版本/平台差异 | ❌ | ✅ |
| 代理 | ⚠️ 受 Unity 后端限制 | ⚠️ handler 配置差异 | ❌ | ✅ |
| TLS/证书 | ⚠️ Unity 后端策略 | ⚠️ Mono/IL2CPP 差异 | ❌ 自己实现 | ✅ |
| 取消 | `Abort()` 相对可靠 | token 传播不一定彻底 | `tcp.Close()` | ✅ |
| 超时分层 | ❌ 只有整请求 timeout | ✅ 可做 | ✅ 自己实现 | ✅ |
| 主线程 | 回调主线程，解析不能重 | 后台线程，需 marshaling | 后台线程，需 marshaling | 与 Unity 无关 |
| IL2CPP/AOT | ✅ | ⚠️ 裁剪/TLS 需验证 | ✅（依赖 BCL） | N/A |
| WebGL | ✅（UWR） | ⚠️ 基本不可用 | ❌ | N/A |
| 请求签名（SigV4 等） | 可加 header | 可加 header | 需自己实现 | ✅ |
| 证书/代理/重定向/连接池 | 受 Unity 限制 | 较全 | 全部自维护 | 全套 |

---

## 3. 实证：三种真实路线

| 项目 | 做法 | 结果 |
|---|---|---|
| **TaiWuYaoLao** | UWR + `DownloadHandlerBuffer` | ❌ 整包缓冲，假流式；生成期间无输出 |
| **WorldTalk** | UWR + `DownloadHandlerScript` | ✅ 真流式；增量 UTF-8/SSE 行缓冲；主线程回调；`Abort()` 取消 |
| **江湖有灵** | 流式走裸 `TcpClient + SslStream` + 手写 HTTP/1.1 + 自解 chunked；非流式才用 HttpClient | ✅ 可控、真流式；❌ 无 HTTP/2/系统代理/重定向/连接池，TLS 自维护 |
| **pi.net** | net9/10 HttpClient | ❌ 不在问题域：假设完整 .NET 运行时，不解决 Unity 宿主 |

---

## 4. 核心问题定义

> **问题不是“Unity 怎么接 OpenAI SSE”，而是“如何定义一个不泄漏 Unity 依赖的通讯抽象，使 pi 的全部 provider/协议/认证/流式/长任务都能在 Unity 宿主下被实现和替换，并且可测试。”**

由此推出：

1. **传输必须抽象**，不能把 `HttpClient` 或 UWR 写死进 provider adapter；
2. **认证与签名必须是钩子**，不能混进传输实现；
3. **Unity 差异必须关在 Host/实现层**，核心只依赖 Abstractions；
4. **主线程/生命周期必须显式建模**，不能靠调用方自觉；
5. **mock/录制回放必须是第一等能力**，否则 CI 无法覆盖；
6. **netstandard2.1 + Mono/IL2CPP + 游戏锁版本**是硬约束，不能以“升级”解决。

---

## 5. 由此产生的设计问题

- 传输接口应该长什么样，才能同时容纳 HTTP/SSE、NDJSON、WebSocket、raw TCP？
- 认证/签名/代理/TLS/超时/取消分别属于哪一层？
- Unity 主线程分发与游戏生命周期怎么表达？
- 多实现（HttpClient/UWR/RawTcp/后端 RPC）如何选择与回退？
- 如何在不引入大依赖的前提下，复用现成 SSE/HTTP 解析器？
- 如何做 mock、录制、回放、错误注入？
