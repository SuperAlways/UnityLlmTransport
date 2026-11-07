# Phase 0 Summary · Unity 通讯事实与 go/no-go

> 日期：2026-11-06
> 输入：`docs/spike/uwr-streaming.md`、`websocket-matrix.md`、`proxy-tls-matrix.md`、`il2cpp.md`。
> 目标：判断 SseParser/UWR/HttpClient/WebSocket/IL2CPP 能否支撑后续实现。

---

## 1. 环境

| 版本 | Mono | IL2CPP | 许可证 |
|---|---|---|---|
| 2021.3.45f1 | ✅ | ✅ | ✅ |
| 6000.3.28f1（6.3 LTS） | ✅ | ✅ | ✅ |
| 6000.6.8f1（6.6） | ✅ | ✅ | ✅ |

- Unity Hub：`D:\tools\UnityHub`（3.3.7）
- C++ 工具链：VS 2026 `Desktop development with C++`（MSVC 14.44/14.51 + Windows SDK）
- 测试方式：`Phase0Runner` 自动构建 Windows Player，跑完写 `phase0-results.txt` 后退出。

---

## 2. 结论

### 2.1 UWR 真流式：通过

三版本 × Mono/IL2CPP 全部：

- `/sse`：callbacks=6，首块 0.011–0.050s，总时长 0.57–0.63s；
- `/sse-error`：500 + body 可读；
- `/sse-gzip`：仍逐块回调（时序与普通 SSE 一致）；
- Abort：回调 2→2，`Request aborted`；
- 本地 HTTP / 远程 HTTPS / 自签 + certificateHandler 均通过。

→ **Phase 2 使用 UWR + `DownloadHandlerScript` 作为 Unity 主路径。**

### 2.2 SseParser vendor：通过

`System.Net.ServerSentEvents` 在 netstandard2.1 编译通过，4/4 边界测试：
多行/注释/字段、LF+`[DONE]`、CR-only、EOF 半截丢弃。

→ **Phase 1 vendor 解析器**；需补 `ArrayBuffer.cs`、`SR.cs`、`ArrayMaxLength` shim，C# 11/12 语法降级。

### 2.3 link.xml：必需

2021.3 IL2CPP 有 link.xml → 通过；移走 link.xml 重新构建 → 构建成功但运行卡死（`Curl error 42: Callback aborted`）。

→ **UWR adapter/Unity 包必须自带 `link.xml` / `[Preserve]`**；无 link.xml 不可发布。

### 2.4 WebSocket：通过（WebGL 除外）

三版本 × Mono/IL2CPP：

- 本地 `ws://` 收发；
- Close：`NormalClosure/bye`，回握手 `CloseSent`；
- Abort：`state=Aborted`；
- 远程 `wss://ws.postman-echo.com/raw` 收发通过；
- 错误端口/非 ws 端点：`WebSocketException: Unable to connect`（可分类）。

→ **Phase 5 可用 ClientWebSocket；WebGL 仍 M2。**

### 2.5 代理 / TLS

- UWR：系统代理生效（错误端口返回 502）；**无显式代理 API**；
- UWR：正常 TLS 200；自签 + `CertificateHandler` 200；自签默认 `SSL CA certificate error`；
- HttpClient：系统/显式代理、自签 callback 均通过（Task 4）；
- ClientWebSocket：系统代理下本地/远程 wss 通过；关代理对照未做。

→ **显式代理走 HttpClient；UWR 只承诺系统代理；自签需 Handler。**

---

## 3. 未决 / 后置

| 项 | 处理 |
|---|---|
| gzip 内容是否已解压 | Phase 1/2 在 adapter 里验证；当前只证明逐块时序 |
| 系统代理关闭对照 | 可选，留 Phase 3 |
| WebGL | M2 |
| HTTP/2 行为 | 未单独测；6.3+ UWR 可能默认，但本测试服务是 HTTP/1.1；Phase 3 用真实端点补 |
| 无 link.xml 对照 | 只在 2021.3 跑；6.3/6.6 默认沿用 link.xml，不再重复破坏性对照 |

---

## 4. go/no-go

| 决策 | 结论 |
|---|---|
| Phase 1 Core | **GO**：SseParser vendor + Abstractions + Timeout/Mock 可开工 |
| Phase 2 UWR | **GO**：UWR adapter + ChannelReadStream + link.xml |
| Phase 3 HttpClient | **GO**：显式代理/TLS 有实测基础 |
| Phase 5 WebSocket | **GO**：本地/远程 wss/Close/Abort 已验证；WebGL 后置 |
| RawTcp | **NO-GO（非默认）**：UWR/HttpClient 已覆盖当前需求，保留设计位置 |

---

## 5. 对 Phase 1/2 的具体修订

1. `SseCompile` 的 polyfill 清单纳入 Phase 1（`ArrayBuffer`、`SR`、`ArrayMaxLength`、`IsExternalInit`）；
2. UWR 包必须携带 `link.xml`，并在文档/门禁中写死“无 link.xml 不可发布”；
3. UWR adapter 统一 `timeout=0` + 首字节/空闲看门狗；
4. gzip 解码策略在 Phase 2 补测后再定（先按 UWR 原始字节处理）；
5. `TransportCapabilities`: 2021.3 `Http2=false`；6.3/6.6 待真实端点验证后再设 true；
6. 显式代理只通过 HttpClient；UWR 能力声明 `ExplicitProxy=false`。

---

## 6. 验收

- [x] 三版本 Mono/IL2CPP UWR/SSE/Abort/gzip；
- [x] SseParser 编译与边界测试；
- [x] link.xml 必要性；
- [x] 本地/远程 WebSocket + Close/Abort；
- [x] TLS 正常/自签；
- [x] 代理矩阵（UWR 系统；HttpClient 显式）；
- [x] 文档落盘。

**Phase 0 核心验收通过，可进入 Phase 1 · Core。**
