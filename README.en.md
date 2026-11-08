# UnityLlmTransport

> **A standalone LLM communication foundation for the Unity runtime** — carrying the full communication needs of pi-ai / pi-agent-core under the hard constraints of Unity + netstandard2.1.

[中文](README.md) | English

| | |
|---|---|
| **Version** | `0.6.0` (see `VERSION`) |
| **Status** | Phases 0–6 all complete, field-tested (full 4-version Mono/IL2CPP matrix passed on real Unity builds) |
| **Distribution** | Source vendoring — consumers pin a commit (see [docs/13](docs/13-源码搬运与集成指引.md)); NuGet/UPM packaging deferred |
| **License** | MIT (`LICENSE`; vendor attribution in `NOTICE`) |
| **Compatibility** | Unity 2021.3.45f1 → 6000.6.8f1 (4-version baseline); C# 9 / netstandard2.1 |
| **First consumer** | PiUnityAgent (via its own Fetch port + host adapter; not the only consumer) |

> Note: most linked docs are written in Chinese; type/command names are universal.

---

## 1. Vision

AI mod authors and native AI-game developers share the same problem: **there is no ready-made LLM communication foundation inside Unity**.

- The .NET server-side stack (`HttpClient` + SSE libraries) is unusable or unreliable in Unity: Mono/IL2CPP runtime, the UWR network backend, the main-thread model, and the game lifecycle (pause / scene switch / quit / domain reload) all violate server-side assumptions;
- Existing mods each roll their own UWR / raw-TCP wrappers — inconsistent quality, no reuse, no unit testing;
- Coupling directly to a provider SDK (OpenAI/Anthropic…) welds protocol, auth, and transport together; switching providers means a rewrite.

**This project's answer**: put "how bytes go out, how they come back, how cancellation works, how auth is attached, how results reach the Unity main thread" into one **replaceable, unit-testable, UnityEngine-free** communication abstraction. Upper layers (provider protocols, agent loop, UI) depend only on abstract ports — never on a concrete network stack.

> In one sentence: not an "OpenAI SSE client", but a **Unity host implementation of pi's communication surface**.

**Three host types, one abstraction set**:

| Host | Usage |
|---|---|
| AI mod (game-locked Unity version) | PiUnityAgent's own Fetch port + host adapter → this transport stack |
| Native Unity AI game | Use `Abstractions` + `Sse` + any transport directly, or go through PiUnityAgent as above |
| .NET 8 backend | Doesn't need this component (HttpClient/pi.net already exist); `Abstractions` can serve as the shared port |

---

## 2. What works today

All figures re-verified 2026-11-08 (evidence: `docs/spike/` + `docs/spike/evidence/`):

**dotnet tests** (`dotnet test`, 226 green):

| Suite | Coverage |
|---|---|
| Core (Abstractions/Sse/decorators) | 53 — port contracts, SSE parsing, timeout/retry/middleware decorators, Mocks |
| UWR (UnityWebRequest) | 47 — streaming download, queueing, error classification, Abort/dispose |
| HttpClient | 56 — streaming/cancel/timeouts/explicit proxy/TLS/redirects + stability soak |
| Auth | 26 — ApiKey/OAuth injection, 401 refresh pipeline, Signer + soak |
| WebSocket | 44 — session edges/timeouts/message budget/concurrency + default 16MiB cap + soak |

**Unity field matrix** (Mono + IL2CPP):

| Transport | 2022.3.62f3c1 (primary baseline) | 2021.3.45f1 | 6000.3.28f1 | 6000.6.8f1 |
|---|---|---|---|---|
| UWR streaming (U1–U11) | ✅ Mono + IL2CPP 15/15 | ✅ same | ✅ same | ✅ same |
| HttpClient (H1–H9) | ✅ Mono 12/12 | ✅ same | ✅ same | ✅ same |
| WebSocket (W1–W6) | ✅ Mono + IL2CPP 6/6 | ✅ same | ✅ same | ✅ same |

Includes live remote `wss://` (Phase 5 W5). Known reservation: UWR accept-all-certificate / client-certificate paths are dotnet-only (not yet field-tested in Unity).

**Source-vendoring verification**: a clean empty Unity 2022 project, 78 `.cs` files hand-copied per docs/13 (8 directories) → compiles with 0 errors → W1–W6 all pass.

**Gates**: Release build 0 warnings / 0 errors; `grep UnityEngine src/` zero hits; `grep PiUnityAgent src/` zero hits (no consumer coupling).

---

## 3. Architecture

```
              Consumers (PiUnityAgent.Ai / game code / mods)
                              │  depends only on abstract ports
                              ▼
        ┌─────────────────────────────────────────────┐
        │       UnityLlmTransport.Abstractions        │  pure interfaces/DTOs/decorators, zero UnityEngine
        │  IStreamingHttpTransport · ITransportSession │
        │  IWebSocketTransport · IWebSocketSession     │
        │  TransportRequest/Options/Capabilities/...   │
        └─────────────────────────────────────────────┘
              ▲                    ▲                    ▲
   ┌──────────┴─────┐   ┌──────────┴──────┐   ┌────────┴────────┐
   │ UnityWebRequest │   │    HttpClient   │   │    WebSocket    │
   │ (unity/ runtime) │   │ (desktop/backend)│   │ (ClientWebSocket)│
   └─────────────────┘   └─────────────────┘   └─────────────────┘
              ▲
   ┌──────────┴──────────────────────────────────────┐
   │            UnityLlmTransport.Hosts.Unity         │  coroutine host / main-thread dispatch / lifecycle
   └──────────────────────────────────────────────────┘
```

### Packages

| Package | Location | Responsibility | UnityEngine | Tests |
|---|---|---|---|---|
| `Abstractions` | `src/` | Ports, DTOs, capability flags, error classification, decorators, Mocks | ✅ none | 53 |
| `Sse` | `src/` | Vendored `System.Net.ServerSentEvents` (MIT) parser, netstandard2.0 | ✅ none | (in Core) |
| `HttpClient` | `src/` | `HttpClientTransport`/`Session` (ResponseHeadersRead streaming; explicit proxy/RequestFinalizer) | ✅ none | 56 |
| `Auth` | `src/` | `ApiKeyMiddleware`, `OAuthTokenMiddleware` (401 refresh), `IRequestSigner`+`SignerFinalizer` | ✅ none | 26 |
| `WebSocket` | `src/` | `ClientWebSocketTransport`/`Session`, error classification, 16MiB message budget; hand-written receive enumerator (works around an IL2CPP corelib defect) | ✅ none | 44 |
| `UnityWebRequest.Core` | `src/` | Pure UWR bridging/queueing/classification (netstandard2.1) | ✅ none | (in UWR suite) |
| `UnityWebRequest` | `unity/` | UWR adapter runtime (`DownloadHandlerScript` streaming) + `link.xml` (IL2CPP strip protection) | uses | 47 (with Core) |
| `Hosts.Unity` | `unity/` | `UnityCoroutineHost`: main-thread dispatch, `ShutdownToken` lifecycle | uses | field-tested |

### Decorator stack (HTTP path)

```
TimeoutTransport → RetryTransport → MiddlewareTransport → ObserverTransport → FinalizerTransport → concrete transport
 (first-byte/idle/    (401 refresh/     (ApiKey/OAuth        (observation)      (signing)
  response cap)        retry hooks)      injection)
```

Each layer is independent, trimmable, replaceable. Retry policy is injected by upper layers via `IRetryPolicy`; the transport only classifies errors (`TransportErrorKind`: network/HTTP/protocol/parse/idle/cancelled).

### Key design decisions

- **Capability flags instead of version checks**: `TransportCapabilities` (`Http2`/`SystemProxy`/`RequestFinalizer`/…) declares what each transport can do; unsupported features (e.g. explicit proxy) report `Unsupported`, and consumers pick transports accordingly.
- **Streaming is real streaming**: `ITransportSession.Body` is a `Stream`, read as it arrives; whole-body buffering is never marketed as streaming.
- **Plan-B seam (decided 2026-11-07)**: `PiUnityAgent.Ai` does not depend on this component; it owns its Fetch port, and adapters in `PiUnityAgent.Hosts.Unity/Ext` map it onto `IStreamingHttpTransport`. Sole authority for the seam: `PiUnityAgent/docs/07-通讯层集成契约.md` (in that repo).

---

## 4. Quick start

Full vendoring steps (copy scope, single-IsExternalInit rule, link.xml, asmdef, commit pin): **[docs/13-源码搬运与集成指引.md](docs/13-源码搬运与集成指引.md)** (Chinese). Minimal integration (UWR + SSE):

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

WebSocket: `new ClientWebSocketTransport()` (default 16MiB message cap; explicit `WebSocketOptions.MaxMessageBytes` wins). HttpClient (desktop/backend): `new HttpClientTransport()`.

Executable usage examples: the tests under `tests/*Tests/` double as usage docs; Unity-side see `unity/Phase*Harness/`.

---

## 5. Navigation for development agents

**Read the referenced doc before touching code; treat the referenced doc as the source of truth for numbers and contracts — never write from memory.**

| Task | Read first | Entry / command |
|---|---|---|
| Vendor sources into a Unity project | `docs/13` (copy scope, single IsExternalInit, link.xml, asmdef forms) | 8 dirs under `src/` + `unity/`, pin commit |
| Understand/modify architecture & abstractions | `docs/10-架构设计.md` | `src/UnityLlmTransport.Abstractions/` |
| Add/modify a transport | `docs/10` + `docs/05` (capability rules) | matching `src/UnityLlmTransport.*`; capability matrix in `docs/05` §3 |
| Touch auth/signing | `docs/14-可观测与脱敏指引.md` (credentials never logged) | `src/UnityLlmTransport.Auth/` |
| Add logging/observability | `docs/14` (redaction rules) | `ITransportObserver` + `ObserverTransport` |
| Run tests | — | commands below |
| Add tests | existing suite style + `tests/Phase3TestSupport/` (in-process Kestrel) | matching `tests/*Tests/` |
| Unity field verification | `docs/SOP/Unity多版本通讯测试-SOP-2026-11-06.md` | `unity/Phase*Harness/` + `scripts/assemble-phase*-unity.sh` |
| Version compatibility / support definition | `docs/05-版本与兼容基线.md` | 4-version baseline + compatibility matrix |
| Requirements & boundaries | `docs/00-核心需求.md` + `docs/03-pi通讯需求矩阵.md` | — |
| Start a new phase | `docs/12-开发执行模型-spec-plan-subagent.md` | `docs/superpowers/specs|plans/` (brainstorming→writing-plans→subagent-driven-development skill chain) |
| pi-side seam | `PiUnityAgent/docs/07-通讯层集成契约.md` (that repo) | this repo contains no PiUnityAgent code |

**Test commands** (repo root, bare `dotnet`):

```bash
dotnet build UnityLlmTransport.sln -c Release          # gate: 0 warnings / 0 errors
dotnet test tests/UnityLlmTransport.Tests/             # 53
dotnet test tests/UnityLlmTransport.UnityWebRequest.Tests/   # 47
dotnet test tests/UnityLlmTransport.HttpClient.Tests/  # 56
dotnet test tests/UnityLlmTransport.Auth.Tests/        # 26
dotnet test tests/UnityLlmTransport.WebSocket.Tests/   # 44
```

Note: fixed test ports (5199/5201/5203/5211/5233/5234/5271/5273) — do not run suites in parallel on one machine.

---

## 6. Hard constraints (non-negotiable)

1. **C# 9 / netstandard2.1** — no .NET 6+ -only APIs (`System.Threading.Lock`, `Task.WaitAsync`, etc. forbidden);
2. **Zero UnityEngine references in `src/`** — gate-verified by grep; Unity adaptation lives only in `unity/`;
3. **Zero/minimal third-party dependencies** — SSE is vendored source (MIT, see `NOTICE`); no native plugins;
4. **Unity 2021.3.45f1 floor**, 4-version baseline (2022.3.62f3c1 primary / 2021.3.45f1 / 6000.3.28f1 / 6000.6.8f1); Mono first, IL2CPP must work (`link.xml`/`[Preserve]`);
5. **Core code must ship with unit tests** — changes to `src/` run all five suites + the 0-warning Release gate;
6. **WebGL unsupported** (revisit in M2 via `.jslib` + fetch); explicit proxy goes through HttpClient, UWR is system-proxy only.

---

## 7. Scope boundaries: what this is not

- **No provider protocol parsing** — OpenAI/Anthropic/Gemini JSON mapping belongs to `PiUnityAgent.Ai`;
- **No agent loop / tool execution** — belongs to `PiUnityAgent.Core`;
- **No UI** — belongs to the host/mod;
- **Not for non-Unity engines** — Unreal/Godot etc. belong to upstream pi / pi.net;
- **Not version-locked to Unity** — provides adaptation only, never demands an engine upgrade;
- **No references to PiUnityAgent types** — dependency direction is one-way: adapters live in the consumer's repo.

---

## 8. Layout

```
UnityLlmTransport/
  LICENSE · NOTICE · VERSION
  README.md / README.en.md
  docs/                # 00-14 design & guides + SOP + spike evidence (index below)
  src/                 # six pure .NET packages (zero UnityEngine): Abstractions / Sse / HttpClient /
                       #   Auth / WebSocket / UnityWebRequest.Core
  unity/               # Unity side: UnityLlmTransport.UnityWebRequest(Runtime+link.xml) /
                       #   Hosts.Unity / Phase2·3·5Harness
  tests/               # five test projects + Phase3TestSupport (in-process Kestrel)
  scripts/             # assemble-phase2/3/5-unity.sh (assemble into a Unity project)
  spikes/              # local test servers (SseTestServer 5199 / Phase3TestServer 5201·5233·5234 / WsTestServer)
```

---

## 9. Doc index

| Doc | Content |
|---|---|
| `docs/00-核心需求.md` | Requirements & scope (numbered R-requirements) |
| `docs/01-Unity通讯问题陈述.md` | Full statement of Unity communication problems |
| `docs/02-调研问题.md` / `docs/07` / `docs/08` / `docs/09` | Reference-project research method, reading notes, evaluation matrix |
| `docs/03-pi通讯需求矩阵.md` | pi's requirements on the transport layer (authoritative consumer needs) |
| `docs/04-Unity-AI-mod通讯问题清单.md` | Communication problems of existing AI mods |
| `docs/05-版本与兼容基线.md` | 4-version baseline, compatibility matrix, support definition |
| `docs/06-参考项目复盘-*.md` | Yaolao/WorldTalk/江湖有灵 post-mortems |
| `docs/10-架构设计.md` | Architecture authority (layering/threads/lifecycle/test strategy) |
| `docs/11-开发阶段划分.md` | Phase 0–6 breakdown & status |
| `docs/12-开发执行模型-spec-plan-subagent.md` | spec→plan→subagent development process |
| `docs/13-源码搬运与集成指引.md` | **Consumer vendoring guide (copy scope/IsExternalInit/link.xml/pin)** |
| `docs/14-可观测与脱敏指引.md` | `ITransportObserver` observation points + redaction rules |
| `docs/SOP/` | Unity multi-version field-testing SOP |
| `docs/spike/` | Per-phase field results + `evidence/` raw records |

---

## 10. Status & deferred items

**Complete**: Phase 0 (spike / IL2CPP stripping proof) → 1 (core abstractions + SSE) → 2 (UWR) → 3 (HttpClient) → 4 (Auth) → 5 (WebSocket) → 6 (hardening / vendoring guide / release verification). Details in `docs/11`.

**Deferred (awaiting need, unscheduled)**:

| Item | Trigger |
|---|---|
| NuGet / UPM / zip packaging | consumer acceptance clarifies distribution needs |
| Record/replay fixtures | provider protocols stabilize |
| RawTcp fallback | proven transport-unusable scenario |
| WebGL (`.jslib` + fetch) | M2 kickoff |
| WS→SSE fallback / auto-reconnect / `WebSocketCached` | consumer demand |
| telemetry shared package | `ITransportObserver` suffices today |
| Consumer E2E / in-game acceptance | executed in the PiUnityAgent repo |
