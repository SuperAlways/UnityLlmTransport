# Phase 6 · 稳定性与搬运发布结果

> 日期：2026-11-08。构建/运行命令见 plan `docs/superpowers/plans/2026-11-08-phase6-hardening-vendoring.md`。
> 本轮复跑，不修改 phase2/3/5 的历史结果文档；原始行在 `docs/spike/evidence/phase6/`（21 份 `p6-*-results.txt`，与 `D:/UnitySpikes/` 本轮产物同名）。

## 1. dotnet 稳定性

| 套件 | 结果 |
|---|---|
| Core（UnityLlmTransport.Tests） | 53/53 |
| UWR（UnityLlmTransport.UnityWebRequest.Tests） | 47/47 |
| HttpClient（含 StabilityTests） | 56/56 |
| Auth（含 StabilityTests） | 26/26 |
| WebSocket（含 StabilityTests + 默认上限） | 44/44 |

复跑实据（2026-11-08，`dotnet test --nologo` 尾行）：`失败: 0，通过: 53/47/56/26/44`，跳过 0。

## 2. 四版本矩阵复跑

全部为 2026-11-08 本轮复跑（Phase2/Phase3 复用既有构建重跑；Phase5 因 WS 默认 16MiB 改动产品代码，全部重新 assemble+build 后重跑）。

| 传输 | 2022.3.62f3c1 | 2021.3.45f1c2 | 6000.3.28f1 | 6000.6.8f1 | 判据 |
|---|---|---|---|---|---|
| Phase2 UWR Mono | 15/15 OK | 15/15 OK | 15/15 OK | 15/15 OK | U1–U11 15/15 |
| Phase2 UWR IL2CPP | 15/15 OK | 15/15 OK | 15/15 OK | 15/15 OK | U1–U11 15/15 |
| Phase3 HttpClient Mono | 12/12 OK | 12/12 OK | 12/12 OK | 12/12 OK | H1–H9 12/12 |
| Phase5 WebSocket Mono | 6/6 OK | 6/6 OK | 6/6 OK | 6/6 OK | W1–W6 6/6 |
| Phase5 WebSocket IL2CPP | 6/6 OK | 6/6 OK | 6/6 OK | 6/6 OK | W1–W6 6/6 |

- Phase5 重建：4 版本 × Mono/IL2CPP 共 8 次构建全部 `PHASE5_BUILD ... Succeeded errors=0`（`D:/UnitySpikes/p6-phase5-<ver>-{mono,il2cpp}.log`）。
- W5 远程 wss 本轮 8 组全过（`PHASE5 W5_remote_wss|OK|type=Text|payload=hello` ×8），外网/系统代理可用。
- 每份结果文件除判据行外仅 2 行头（`unity_version=`/`main_thread=`）+ 1 空行，无 FAIL/ERROR/SKIP。

## 3. 源码搬运验证

- 工程：`D:/UnitySpikes/Phase6Vendor-2022`（`Unity -createProject` 全新空工程，按 `docs/13` 手工搬运 8 目录共 78 个 `.cs`）
  - `Abstractions`(26)/`Sse`(10)/`HttpClient`(10)/`Auth`(7)/`WebSocket`(5)/`UwrCore`(11)/`UwrRuntime`(5)/`Hosts`(4)
  - 仅保留 `Abstractions/Internal/IsExternalInit.cs`（其余 5 处已删）；`link.xml` 置于 `Assets/link.xml`；Phase5 harness 复用为运行判据。
- 编译：`Phase5Build.BuildMono` → `PHASE5_BUILD mono Succeeded errors=0`（`D:/UnitySpikes/p6-vendor-build.log:3423`）。
- 运行（`D:/UnitySpikes/p6-vendor-results.txt`，构建产物即 `Builds/Phase5/2022.3.62f3c1/mono/` 最新一次=vendor 工程）：

```text
PHASE5 W1_text|OK|type=Text|payload=echo:hello
PHASE5 W2_utf8|OK|payload=echo:你好，世界
PHASE5 W3_close|OK|echo=echo:bye|type=Close|close=NormalClosure
PHASE5 W4_abort|OK|send_kind=Aborted
PHASE5 W5_remote_wss|OK|type=Text|payload=hello
PHASE5 W6_error_port|OK|kind=Connect
```

门线 W1/W3/W4 至少 OK —— 实际 W1–W6 全 6/6 OK。

## 4. 阶段门禁（Step 7）

- `dotnet build UnityLlmTransport.sln -c Release`：`OK: zero warnings/errors`（grep warning|error 无命中）。
- 五套件：53/47/56/26/44 全绿（见 §1）。
- `grep -rn "UnityEngine" src --include='*.cs'` → 无命中（OK: src zero UnityEngine）。
- `grep -rn "PiUnityAgent" src --include='*.cs'` → 无命中（OK: src no consumer references）。

## 5. 结论 / 已知遗留

- WS 默认 16MiB 上限已验证（产品代码 `ClientWebSocketTransport.DefaultMaxMessageBytes = 16 * 1024 * 1024`，四版本 8 组 Phase5 全绿 + WebSocket 套件 44/44）；
- Unity accept-all wss/客户端证书仍 dotnet-only（Phase 5 遗留）；
- 打包（NuGet/UPM/zip）后置，等 pi 验收；
- 版本：`VERSION=0.6.0`，无公共 tag。
