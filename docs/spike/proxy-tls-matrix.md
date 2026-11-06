# 代理 / TLS 矩阵（Phase 0 Task 4）

> Spike 结论先行：**HttpClient 在 Windows 上系统代理/显式代理/自签 TLS 三条路全部可控**——系统代理自动吃 WinINET 设置（含 bypass），显式代理 `WebProxy` 生效，自签证书默认拒（`UntrustedRoot`）但 `ServerCertificateCustomValidationCallback` 可绕。Unity 侧 UWR / ClientWebSocket 待实机回填。

## 环境（实测时点）

- OS Windows 11；.NET SDK `10.0.301`（裸 `dotnet`）
- 系统代理：**开**（注册表 `HKCU\...\Internet Settings`：`ProxyEnable=0x1`，`ProxyServer=127.0.0.1:7897`，`ProxyOverride=localhost;127.*;192.168.*;10.*;172.16-31.*;<local>`）
- 本地代理进程 `127.0.0.1:7897` **在运行**（Clash 类，实测时监听中）
- 本地明文端点：Task 2 `SseTestServer` → `http://127.0.0.1:5199/sse`
- 本地自签端点：临时 Kestrel（不入仓库）→ `https://127.0.0.1:5233/`，证书 `CN=spike-self-signed`（SAN: `localhost`/`127.0.0.1`，仅存于 `CurrentUser\My`，**非信任根**；`curl -k` 200 / 严格校验失败已证实不可信）

## 复现命令

```bash
# 终端 A：起本地明文 SSE
dotnet run --project spikes/SseTestServer

# 终端 B：起本地自签 TLS（临时件，仓库外；证书生成见附录）
dotnet run --project "$TEMP/spike-selfsigned-server"

# 探针（三 handler × 全部端点；-- 后可追加判别 URL）
dotnet run --project spikes/ProxyTlsSpike
PROXY_TLS_SPIKE_ALLOW_UNTRUSTED=1 dotnet run --project spikes/ProxyTlsSpike -- https://127.0.0.1:5233/
dotnet run --project spikes/ProxyTlsSpike -- https://www.google.com/
```

## 矩阵

固定表头：栈 | 网络 | 代理 | TLS | 结果 | 错误 | 证据

| 栈 | 网络 | 代理 | TLS | 结果 | 错误 | 证据 |
|---|---|---|---|---|---|---|
| HttpClient | HTTP `http://127.0.0.1:5199/sse` | no-proxy（直连） | 无（明文） | **200** | – | 3 轮全绿，≈547-596ms，249B（完整 [DONE] 流） |
| HttpClient | HTTP 本地 | system-proxy（WinINET `127.*` bypass → 实为直连） | 无 | **200** | – | `GetProxy(5199) -> (direct)` 解析日志 + 3 轮 200 |
| HttpClient | HTTP 本地 | explicit-proxy `WebProxy(7897)` | 无 | **200** | – | 3 轮 200（7897 对 loopback 建立 CONNECT 隧道后转发） |
| HttpClient | HTTPS `https://example.com/` | no-proxy（直连） | 系统信任 CA | **200** | – | 3 轮 200 ≈670-840ms；curl 直连 200 交叉印证 |
| HttpClient | HTTPS example.com | system-proxy（解析出 `7897`） | 系统信任 CA | **200** | – | `GetProxy(example.com) -> http://127.0.0.1:7897/` + 3 轮 200 |
| HttpClient | HTTPS example.com | explicit-proxy 7897 | 系统信任 CA | **200** | – | 3 轮 200 ≈189-1500ms；curl `-x 7897` 200 交叉印证 |
| HttpClient | HTTPS `https://www.google.com/`（判别格） | no-proxy | – | **FAIL** | 超时 20s（直连不可达） | 该网直连被墙 → 反证前两行走代理者确实走了代理 |
| HttpClient | HTTPS google.com | system-proxy | 系统信任 CA | **200** | – | 1967ms——系统代理真实生效的铁证 |
| HttpClient | HTTPS google.com | explicit-proxy 7897 | 系统信任 CA | **200** | – | 2299ms——`WebProxy` 真实生效的铁证 |
| HttpClient | HTTPS `https://127.0.0.1:5233/`（自签·严格） | no-proxy | 自签·不受信 | **FAIL** | `AuthenticationException: ... UntrustedRoot` | 37ms 即拒（握手层），curl 严格校验同步失败 |
| HttpClient | HTTPS 自签·严格 | system-proxy（bypass→直连） | 自签·不受信 | **FAIL** | `AuthenticationException: UntrustedRoot` | 6ms 即拒 |
| HttpClient | HTTPS 自签·严格 | explicit-proxy 7897 | 自签·不受信 | **FAIL** | `AuthenticationException: UntrustedRoot` | 8ms 即拒——代理只隧道不干预证书校验，责任全在客户端 |
| HttpClient | HTTPS 自签 + `DangerousAcceptAnyServerCertificateValidator`（`PROXY_TLS_SPIKE_ALLOW_UNTRUSTED=1`） | no-proxy / system / explicit 三格 | 自签·callback 绕过 | **200 ×3** | – | 7-30ms——HttpClient 有官方绕过口子 |
| UWR | HTTP 本地 / HTTPS 远程 | 系统代理开/关 | – | **Unity 手动执行待回填** | – | brief Step 4.3：系统代理开/关 × 本地/远程 |
| UWR | HTTPS 自签 | – | 自签 | **Unity 手动执行待回填** | – | brief Step 4.3：需 `CertificateHandler` 子类（`CertificateHandler` 默认亦校验） |
| UWR | 任意 | explicit-proxy | – | **不可用**（UWR 无显式代理 API）+ **Unity 手动执行待回填** | – | brief Step 4.3 预判：UWR 没有设代理的 API；Unity 实机确认后定稿 |
| ClientWebSocket | ws:// 本地 | – | 无 | **Unity 手动执行待回填** | – | brief Step 4.3 |
| ClientWebSocket | wss:// 远程 | – | 系统信任 CA | **Unity 手动执行待回填** | – | brief Step 4.3 |
| ClientWebSocket | 任意 | system-proxy | – | **Unity 手动执行待回填** | – | brief Step 4.3：ClientWebSocket 是否吃系统代理未实测 |

### 每格耗时（brief Step 4.3 口径：结果/异常/耗时）

| 格 | 耗时 |
|---|---|
| local-sse（任一 handler） | 543-596ms（含 SSE 5 段 ×100ms 体内时间，直连与走代理无差异） |
| example.com 直连 | 671-840ms |
| example.com 走代理 | 189-1980ms（波动大，代理节点主导） |
| google.com 直连 | 20s 超时 |
| google.com 走代理 | 1967/2299ms |
| 自签严格拒 | 6-37ms（握手即拒，不发请求） |
| 自签 callback 绕过 | 7-30ms |

## 观察

1. **HttpClient 系统代理是"白拿"的**：`UseProxy=true`（默认）自动吃 WinINET 设置，且 `ProxyOverride` 的 `127.*`/`localhost` bypass 一并被尊重——本地 SSE 不会被误送进代理（`GetProxy -> (direct)` 实证）。**将来 S3 走本机 LLM 端口时这是天然豁免。**
2. **显式代理 `WebProxy` 生效且不分青红皂白全代理**：与系统代理不同，`new WebProxy(...)` 没有 bypass，`127.0.0.1:5199` 也走 CONNECT 隧道（同机 loopback，代理能回连成功）。若显式代理指到别的机器，loopback 目标会失败——要留心。
3. **自签证书：默认三层全拒（直连/系统/显式一致）**，错误统一在 TLS 握手层 `UntrustedRoot`；绕过口子只有 `ServerCertificateCustomValidationCallback`（探针用 `PROXY_TLS_SPIKE_ALLOW_UNTRUSTED=1` 验证 200）。生产代码若要支持自签 LLM 端点，UWR 侧对应物是 `CertificateHandler`，ClientWebSocket 侧对应物是 `ServicePointManager.ServerCertificateValidationCallback`/`RemoteCertificateValidationCallback`——Unity 侧行为待回填。
4. **代理路径有一次瞬时抖动**：某单轮中 system-proxy 与 explicit-proxy 同时对 example.com 20s 超时（同一时刻两条代理配置一起挂 → 指向代理节点本身抖动，非 HttpClient 问题）；随后 3 轮全绿 + curl 双向交叉验证 200。记录在此，不隐瞒。
5. **判别格方法论**：`google.com`（该网直连必挂、走代理可达）作为"代理是否真被走"的试金石，比只看 200 更硬——建议后续 Unity 手动格沿用（需换用户网可达的目标）。

## 待办（Unity 手动部分，brief Step 4.3）

- [ ] UWR：系统代理开/关 × 本地 http / 远程 https
- [ ] UWR：自签证书（含 `CertificateHandler` 绕过与否）
- [ ] UWR：显式代理无 API → Unity 实机确认后把矩阵"不可用"格定稿
- [ ] ClientWebSocket：本地 ws / 远程 wss / 系统代理
- [ ] 每格回填：成功/失败、`result`/`responseCode`/异常、耗时

## 附录：自签端点临时件（仓库外）

- 证书：PowerShell `New-SelfSignedCertificate -Subject "CN=spike-self-signed" -TextExtension @("2.5.29.17={text}dns=localhost&ipaddress=127.0.0.1") -CertStoreLocation "Cert:\CurrentUser\My"` → `Export-PfxCertificate` 到 `%TEMP%\spike-self-signed.pfx`（密码 `spike`，2 天有效）。只入 `CurrentUser\My`、不入信任根 → 天然不受信。
- 服务：`$TEMP/spike-selfsigned-server/`（`Microsoft.NET.Sdk.Web` + Kestrel `UseHttps(pfx)`，`https://127.0.0.1:5233/`）。测完可整目录删除。
