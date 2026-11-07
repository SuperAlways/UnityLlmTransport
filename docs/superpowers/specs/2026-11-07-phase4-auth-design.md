# Phase 4 · Auth / 签名（传输侧）设计 spec

> **状态**：设计稿，待用户 review。
> **阶段**：Phase 4（仅本仓库：认证/签名钩子；不含任何消费方集成）。
> **输入**：`docs/00-核心需求.md` R12、`docs/03-pi通讯需求矩阵.md` T6–T8/T17/T20、`docs/10-架构设计.md` §3.5/§8.1/§8.7、`docs/11-开发阶段划分.md` §6、Phase 1/2/3 契约。
> **参考**：pi `packages/ai/src/utils/provider-retry.ts`（401/刷新边界）、pi.net `Auth/*` 的 token 注入形状（仅 C# 映射参考）；本组件不实现任何 provider 签名算法。
> **日期**：2026-11-07

---

## 1. 背景与目标

Phase 0–3 已提供：

- 稳定端口：`IStreamingHttpTransport` / `ITransportSession` / 装饰器 / `RetryTransport`（401 → `IAuthRefreshHandler` 最多一次，Phase 1）；
- UWR 主路径与 HttpClient 第二实现；**只有 HttpClient 声明 `TransportCapabilities.RequestFinalizer`**（UWR 在发送时补 `Content-Length`/`Host`，不能做最终签名）；
- `IRequestMiddleware` / `ITransportRequestFinalizer` 空槽位，但本仓库还没有认证/签名的公共件。

Phase 4 目标：补上**认证与签名的传输侧公共件**——只负责“把凭据/签名应用到请求”，不负责凭据存储、登录流程、provider 签名算法，也不包含任何消费方集成。

---

## 2. 范围

### 2.1 包含

| 交付 | 内容 |
|---|---|
| `UnityLlmTransport.Auth` | netstandard2.1、零 UnityEngine、零第三方依赖：ApiKeyMiddleware、OAuthTokenMiddleware、IRequestSigner/SignerFinalizer、RequestFinalizer 能力守卫 |
| 认证↔Retry 管线约定 | 401 refresh 时 middleware/signer 必须能在重试 attempt 重新执行（管线顺序） |
| 测试 | `tests/UnityLlmTransport.Auth.Tests`：纯 dotnet（MockTransport + HttpClient 本地集成）；无 Unity 矩阵 |
| 文档 | `docs/11`/README 对应项由后续实现收尾 |

### 2.2 不包含（硬边界）

- **不包含任何消费方（PiUnityAgent 或其他）代码、适配器、联调或阶段门禁**；消费方通过自有 Fetch 端口/适配器消费本组件；
- 凭据存储、OAuth 登录/登出 UI、token 获取策略（env/store/options 优先级）——消费方负责；
- AWS SigV4 / GCP ADC / Azure AD 的具体签名算法——消费方实现 `IRequestSigner`；
- provider 协议/Deferred handle 语义；
- UWR 的最终签名支持（能力不声明，不做补丁）；
- Unity 实机矩阵（Auth 是纯 .NET 中间件；其效果经 HttpClient/UWR 既有矩阵覆盖）。

---

## 3. 包与文件结构

```
src/UnityLlmTransport.Auth/
  UnityLlmTransport.Auth.csproj       # netstandard2.1 / LangVersion 9 / Nullable / ImplicitUsings disable
  Internal/IsExternalInit.cs          # 仅 record/init 需要
  Internal/AssemblyInfo.cs            # InternalsVisibleTo("UnityLlmTransport.Auth.Tests")
  ApiKeyMiddleware.cs
  OAuthTokenMiddleware.cs
  IRequestSigner.cs
  SignerFinalizer.cs
  TransportRequirements.cs

tests/UnityLlmTransport.Auth.Tests/
  UnityLlmTransport.Auth.Tests.csproj  # net10 xunit; 引用 Auth/Abstractions/HttpClient/Phase3TestSupport
  ApiKeyMiddlewareTests.cs
  OAuthTokenMiddlewareTests.cs
  SignerFinalizerTests.cs
  AuthRetryPipelineTests.cs
  TransportRequirementsTests.cs
```

---

## 4. 公共类型面

```csharp
public sealed record ApiKeyMiddlewareOptions
{
    public string HeaderName { get; init; } = "Authorization";
    public string? Scheme { get; init; } = "Bearer";
}

public sealed class ApiKeyMiddleware : IRequestMiddleware
{
    public ApiKeyMiddleware(string apiKey, ApiKeyMiddlewareOptions? options = null);
    public ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct);
}

public sealed record OAuthTokenMiddlewareOptions
{
    public string HeaderName { get; init; } = "Authorization";
    public string Scheme { get; init; } = "Bearer";
}

public sealed class OAuthTokenMiddleware : IRequestMiddleware
{
    public OAuthTokenMiddleware(
        Func<CancellationToken, ValueTask<string>> tokenProvider,
        OAuthTokenMiddlewareOptions? options = null);

    public ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct);
}

public interface IRequestSigner
{
    /// <summary>消费方实现具体签名算法（SigV4/ADC/Azure AD 等）。</summary>
    ValueTask<TransportRequest> SignAsync(TransportRequest request, CancellationToken ct);
}

public sealed class SignerFinalizer : ITransportRequestFinalizer
{
    public SignerFinalizer(IRequestSigner signer);
    public ValueTask<TransportRequest> FinalizeAsync(TransportRequest request, CancellationToken ct);
}

public static class TransportRequirements
{
    /// <summary>签名路径要求 RequestFinalizer 能力；缺失 → TransportException(Unsupported)。</summary>
    public static void RequireRequestFinalizer(TransportCapabilities capabilities);
}
```

---

## 5. 行为

### 5.1 Header 注入规则（两个 middleware 共用）

- 值格式：`Scheme` 非空 → `Scheme + " " + secret`；`Scheme == null/""` → 原样 secret；
- **调用方已设置同名 header（非 null）→ 不覆盖**（调用方优先）；
- **调用方显式 null（抑制）→ 不注入**（抑制语义优先）；
- 头名大小写不敏感（`TransportHeaders`）；
- 不在异常/日志中输出 secret；middleware 自身不记录任何凭据；
- `ApiKeyMiddleware` 构造时 `apiKey` 为 null/空 → `ArgumentException`；
- `OAuthTokenMiddleware` 的 token provider 返回 null/空 token → `TransportException(TransportErrorKind.Protocol, "OAuth token provider returned an empty token.")`（**不**发起请求）；
- token provider 只在需要注入前调用；调用方已设置/抑制时**不调用** provider（避免无谓刷新/网络）。

### 5.2 SignerFinalizer

- `FinalizeAsync` 直接委托 `IRequestSigner.SignAsync`；
- 在管线中位于**最内层**（`FinalizerTransport` 已有语义）：签名发生在所有 middleware 之后、传输发送之前；
- signer 抛出的异常原样传播；
- 本组件不检查 header 是否被传输层改写；这是传输能力契约（`RequestFinalizer`）的责任。

### 5.3 能力守卫与签名路径

- `TransportRequirements.RequireRequestFinalizer(capabilities)`：缺少 `RequestFinalizer` → `TransportException(Unsupported)`，提示改用声明该能力的传输；
- HttpClient 声明 `RequestFinalizer`；UWR 不声明 → 签名路径不得选择 UWR；
- 消费方接线：`Timeout → Retry → Middleware → Observer → Finalizer → 具体 transport`（见 §5.4）。

### 5.4 Auth ↔ Retry 管线顺序（401 refresh 的关键）

Phase 1 的 `RetryTransport` 在每次 attempt 调用**其 inner** `OpenAsync`。为了让 401 refresh 后的重试带上**新 token/新签名**，认证 middleware 与 SignerFinalizer 必须位于 `RetryTransport` 的**内侧**：

```
TimeoutTransport
  → RetryTransport
      → MiddlewareTransport(ApiKey/OAuth)
          → ObserverTransport
              → FinalizerTransport(SignerFinalizer)
                  → 具体 transport
```

- 每次重试 attempt 都会重新执行 middleware（取 token）与 finalizer（重新签名）；
- `IAuthRefreshHandler.RefreshAsync` 由消费方实现（例如更新 token store），不得由 middleware 自行刷新；
- 该顺序写入文档/示例；测试必须覆盖“第一次 401 → refresh 更新 provider → 第二次请求带新 token”。

### 5.5 Deferred / 目录刷新 / 图片 / 分类

- 传输形状不新增：这些能力使用普通 HTTP + `Stream` 会话；
- 测试以两次普通请求演示“长轮询/再次请求”经同一 transport 携带认证头；
- handle 语义（DeferredHandle/cancelDeferred）不属于本组件。

### 5.6 安全与脱敏

- middleware 不在异常消息中拼接 secret；token 注入失败只报状态/原因；
- 不提供日志；`ITransportObserver` 观察的是响应/字节，不暴露请求头——无需额外脱敏；
- 凭据生命周期归消费方（token store/refresh handler）。

---

## 6. 测试计划（dotnet；无 Unity）

| 组 | 覆盖 |
|---|---|
| ApiKey | 默认 Bearer；自定义 header/scheme；Scheme null 原样；调用方已有头不覆盖；显式 null 抑制；null/空 key 构造失败 |
| OAuth | token 注入；自定义 header/scheme；调用方已有/抑制不走 provider；空 token → Protocol；provider 异常传播 |
| Signer | 委托调用；signer 异常传播；middleware+finalizer 栈中 signer 看到最终头 |
| 能力守卫 | MockTransport 无 `RequestFinalizer` → Unsupported；带该能力的 fake/HttpClient → 通过 |
| Auth↔Retry | middleware 在 Retry 内侧：第一次 401 → refresh 翻 token → 第二次带新 token；Refresh 最多一次；流已开始不重试（Phase 1 契约） |
| 集成 | HttpClient + 本地 `Phase3TestSupport`：ApiKey/OAuth 头到达服务端；SignerFinalizer 的最终头未被传输层覆盖 |
| Deferred 形状 | 同一 transport 两次请求均带认证头 |
| 脱敏 | 构造 secret 后触发的异常消息不含 secret（用 `Assert.DoesNotContain`） |

- **不跑** Unity 矩阵；不需要 Unity 编辑器。
- 测试项目引用 `Phase3TestSupport`（仅测试资产）以复用本地 HTTP/HTTPS 服务；不新增产品依赖。

---

## 7. 偏离表（pi / pi.net / 我们）

| 项 | pi 原版 | pi.net | 我们 | 类别 | 原因/代价 |
|---|---|---|---|---|---|
| 认证形状 | SDK auth/options | 注入 header | `IRequestMiddleware` 公共件 | B | 传输层只管注入，不绑定 provider |
| API Key 来源 | options/env/store | options | 调用方传入 key | C | 存储/优先级属消费方；midware 无状态 |
| OAuth 刷新 | SDK 内部/401 重试 | OAuth 客户端 | `IAuthRefreshHandler` + middleware 内侧重试 | B | 传输层提供钩子；登录/存储消费方 |
| 签名算法 | SDK | SDK | `IRequestSigner` 消费方实现 | C | 避免传输层绑死 SigV4/ADC/Azure |
| Finalizer 传输 | fetch 自定义 | handler | 仅 `RequestFinalizer` 能力传输（HttpClient） | B | UWR 发送时补头，签名会失效 |
| 管线顺序 | SDK | 调用方 | Retry 外、Middleware/Finalizer 内 | B | 保证重试带新 token/签名 |
| Deferred | SDK handle | SDK | 无新形状；通用 HTTP | C | handle 语义在消费方协议层 |
| Unity 矩阵 | — | — | 无（纯 .NET 中间件） | B | 认证已被 UWR/HttpClient 既有矩阵覆盖 |

---

## 8. 验收标准

- [ ] `UnityLlmTransport.Auth` netstandard2.1 编译 0 错误，零 UnityEngine、零第三方依赖；
- [ ] dotnet 测试全绿（ApiKey/OAuth/Signer/Guard/Auth↔Retry/集成/脱敏）；
- [ ] 管线顺序明确且测试覆盖“401 refresh → 新 token”；
- [ ] 签名路径要求 `RequestFinalizer`；UWR 被 `Unsupported` 拒绝；
- [ ] 不包含/不引用任何消费方代码或仓库；
- [ ] 文档更新（docs/11 Phase 4 状态、README 下一步）；
- [ ] 不修改 `Abstractions`/`Sse`/UWR/HttpClient 公共形状（仅新增 Auth 包/测试）。

---

## 9. 后置与风险

| 项 | 处理 |
|---|---|
| 凭据存储/env 优先级 | 消费方实现；出现公共需求再抽 |
| OAuth 登录/UI | 不在本组件 |
| SigV4/ADC/Azure 具体算法 | 消费方 `IRequestSigner`；本组件只验证时序 |
| UWR 签名 | 不声明；如需 UWR 签名，先解决引擎补头问题（另立） |
| Deferred handle | 消费方协议层 |
| Unity 矩阵 | 不需要；认证是纯 .NET 中间件 |

**必须由实现验证的风险**：

1. middleware 在 Retry 内侧时，每次 attempt 确实重新执行（不被缓存）；
2. `TransportHeaders` 的“已有头优先/null 抑制”与 `null` 值语义一致；
3. HttpClient + SignerFinalizer 组合中，最终签名头未被传输层覆盖；
4. OAuth provider 空 token/异常的错误分类（不发起请求）。

---

## 10. 参考

- Phase 1：`docs/superpowers/specs/2026-11-07-phase1-core-design.md`（Middleware/Retry/Finalizer）
- Phase 2/3：`2026-11-07-phase2-uwr-design.md`（UWR 不声明 RequestFinalizer）、`2026-11-07-phase3-httpclient-design.md`（HttpClient 能力）
- pi：`pi-study/pi/packages/ai/src/utils/provider-retry.ts`
- 架构：`docs/10-架构设计.md` §3.5/§8.1/§8.7
- 阶段：`docs/11-开发阶段划分.md` §6（本仓库 Auth/签名；消费方集成不在本仓库）
