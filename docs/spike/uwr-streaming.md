# Phase 0 · UWR 流式实测

> 日期：2026-11-06
> 测试资产：`spikes/UnityUwrSpike/Assets/Scripts` + `Phase0Runner`（自动跑完即退出）
> 服务：`spikes/SseTestServer`（`/sse`、`/sse-error`、`/sse-gzip`）
> 构建：Windows 64-bit；Mono；Development Build

---

## 1. 结果（2021.3.45f1 / 6000.6.8f1）

| 测试 | 2021.3.45f1c2 (Mono) | 6000.6.8f1 (Mono) | 判据 |
|---|---|---|---|
| `/sse` | Success / 200 / callbacks=6 / first=0.050s / elapsed=0.615s | Success / 200 / callbacks=6 / first=0.017s / elapsed=0.592s | ✅ 逐块；首块远小于总时长 |
| `/sse-error` | ProtocolError / 500 / body=`{"error":"boom"}` | ProtocolError / 500 / body=`{"error":"boom"}` | ✅ 错误体可读（DownloadHandlerBuffer） |
| 错误端口 5198 | ProtocolError / **502** / elapsed≈3.0s | ProtocolError / **502** / elapsed≈1.7s | ⚠️ 系统代理返回 502，不是 ConnectionError/0 |
| `/sse-gzip` | Success / 200 / callbacks=6 / first=0.017s / elapsed=0.600s | Success / 200 / callbacks=6 / first=0.017s / elapsed=0.583s | ✅ 仍逐块；内容未校验（gzip） |
| Abort（运行 0.15s 后） | ConnectionError / code=200 / error=`Request aborted`；回调 2→2（1s 后不再增加） | 同左（2→2） | ✅ Abort 停止回调 |
| 本地 ws | Text / 10 bytes / `echo:hello` | 同左 | ✅ ClientWebSocket 本地可用 |
| 错误 ws 端口 | WebSocketException: Unable to connect | 同左 | ✅ 可分类 |
| ws 指向非 ws 端点 | WebSocketException: Unable to connect | 同左 | ✅ 可分类（未暴露 HTTP 状态） |

### 1.1 2021.3 IL2CPP（新增）

| 测试 | 结果 |
|---|---|
| `/sse` | Success / 200 / callbacks=6 / first=0.026s / elapsed=0.626s |
| `/sse-error` | ProtocolError / 500 / body=`{"error":"boom"}` |
| 错误端口 5198 | ProtocolError / 502（系统代理）/ elapsed≈2.9s |
| `/sse-gzip` | Success / 200 / callbacks=6 / first=0.016s / elapsed=0.600s |
| Abort | ConnectionError / code=200 / error=`Request aborted`；回调 2→2 |
| 本地 ws | Text / 10 bytes / `echo:hello` |

**无 link.xml 对照**：构建成功，但运行卡在首个 UWR 请求，日志 `Curl error 42: Callback aborted`，结果文件未更新 → `link.xml` 是必要件。

### 1.2 三版本汇总（Mono + IL2CPP）

| 测试 | 2021.3 Mono | 2021.3 IL2CPP | 6.3 Mono | 6.3 IL2CPP | 6.6 Mono | 6.6 IL2CPP |
|---|---|---|---|---|---|---|
| `/sse` callbacks/first/elapsed | 6 / 0.050 / 0.615 | 6 / 0.026 / 0.626 | 6 / 0.012 / 0.597 | 6 / 0.013 / 0.596 | 6 / 0.017 / 0.592 | 6 / 0.027 / 0.594 |
| `/sse-error` | 500 + body | 500 + body | 500 + body | 500 + body | 500 + body | 500 + body |
| 错误端口 5198 | 502 | 502 | 502 | 502 | 502 | 502 |
| `/sse-gzip` callbacks | 6 | 6 | 6 | 6 | 6 | 6 |
| Abort 回调 | 2→2 | 2→2 | 2→2 | 2→2 | 2→2 | 2→2 |
| 本地 ws | echo:hello | echo:hello | echo:hello | echo:hello | echo:hello | echo:hello |

> 六种组合全部通过；错误端口统一是系统代理返回 502，不是 ConnectionError/0。

### 1.3 扩展矩阵（三版本 Mono + IL2CPP）

| 测试 | 2021.3 | 6.3 | 6.6 |
|---|---|---|---|
| ws Close（收 Close + 回握手） | `NormalClosure/bye`，`finalState=CloseSent` | 同左 | 同左 |
| ws Abort | `abortedState=Aborted` | 同左 | 同左 |
| 远程 wss（Postman echo） | `type=Text bytes=5 text=hello` | 同左 | 同左 |
| TLS 正常（example.com） | Success / 200 | 同左 | 同左 |
| TLS 自签 + certificateHandler | Success / 200 | 同左 | 同左 |
| TLS 自签 + 默认校验 | ConnectionError / code=0 / `SSL CA certificate error` | 同左 | 同左 |

> 以上均在 Mono 与 IL2CPP 下分别通过；每版本至少各跑一次。

### 关键结论

1. **UWR + `DownloadHandlerScript` 在两个版本都真流式**：6 次回调、首块 ~17–50ms、总时长 ~0.6s。
2. **gzip SSE 仍逐块回调**：时序与普通 SSE 一致；UWR 是否解压未验证（本次只看时序）。
3. **Abort 生效**：回调数在 abort 时 2，1 秒后仍 2；`result=ConnectionError`、`error="Request aborted"`。
4. **系统代理影响本地错误端口**：`127.0.0.1:5198` 返回 **502 Bad Gateway**，说明 UWR 走了系统代理，而不是直接 ConnectionError/0。这一点与“本地通、远程不通”的直觉相反——本地错误端口也会经过代理。
5. **错误体**：`/sse-error` 500 的 body 可在 `DownloadHandlerBuffer` 读到。

---

## 2. 曾阻塞项的解决记录 / 仍后置项

早前列为"未测/阻塞"的四项均已解除并实测（2026-11-06/07），解决方式留档免重查：

| 项 | 曾的状态 | 解决方式 → 结果 |
|---|---|---|
| 6000.3.28f1（6.3 LTS） | ❌ `No valid Unity Editor license found`（exit 198） | 在 `D:\tools\UnityHub`（Unity Hub 3.3.7）登录并激活 Personal License 后恢复 → Mono/IL2CPP 全通过（§1.2） |
| IL2CPP（2021.3） | ⏳ 等 VS C++ 工作负载 | VS 2026 `Desktop development with C++`（MSVC 14.44/14.51 + Windows SDK）就绪后构建/运行通过（§1.1） |
| IL2CPP（6.6） | ⏳ 缺 `Windows Build Support (IL2CPP)` 模块 | Hub 在线模块 404（中国 CDN），改本地独立安装包手动安装 + UAC 批准 → 构建/运行通过（§1.2） |
| 远程 wss / TLS / ws Close/Abort | ⏳ 未测 | `Phase0Runner` 扩展后三版本 Mono/IL2CPP 全部实测通过（§1.3；远程 wss 走 Postman echo） |

仍后置（不阻塞 go/no-go）：

| 项 | 状态 |
|---|---|
| gzip 内容是否已解压 | Phase 1/2 在 adapter 里验证；当前只证明逐块时序 |
| 系统代理关闭对照 | 可选，留 Phase 3 |
| WebGL | M2 |
| HTTP/2 行为 | 未单独测；6.3+ UWR 可能默认，但本测试服务是 HTTP/1.1；Phase 3 用真实端点补 |
| 无 link.xml 对照 | 只在 2021.3 跑；6.3/6.6 默认沿用 link.xml，不再重复破坏性对照 |

---

## 3. 复现

```bash
# 服务
dotnet run -c Release --project spikes/SseTestServer

# 构建 Mono
D:\Unity\Hub\Editor\2021.3.45f1\Editor\Unity.exe -batchmode -quit \
  -projectPath D:\UnitySpikes\Uwr2021 \
  -executeMethod Phase0Build.BuildMono \
  -logFile D:\UnitySpikes\build-mono.log

# 运行
D:\UnitySpikes\Builds\2021.3.45f1c2\mono\Phase0.exe -logFile D:\UnitySpikes\player-mono.log
```

结果文件：`%USERPROFILE%\AppData\LocalLow\DefaultCompany\<Product>\phase0-results.txt`；
原始留档见 `docs/spike/evidence/<版本>-results.txt`（2026-11-07 复跑的 Mono 一轮；IL2CPP 数字以 §1.1/1.2 为准）。
`Phase0Runner.cs` / `Phase0Build.cs` 已版本化在 `spikes/UnityUwrSpike/Assets/`（Scripts / Editor）。
