# Phase 2 Unity Harness

## 装配

```bash
# 一次性建工程（每版本一个）
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -createProject D:/UnitySpikes/Phase2-<ver> -batchmode -quit -logFile D:/UnitySpikes/create-phase2-<ver>.log

# 每次改源后装配
bash scripts/assemble-phase2-unity.sh D:/UnitySpikes/Phase2-<ver>
```

## 构建 / 运行

```bash
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -batchmode -quit \
  -projectPath D:/UnitySpikes/Phase2-<ver> \
  -executeMethod Phase2Build.BuildMono \
  -logFile D:/UnitySpikes/phase2-<ver>-mono.log

D:/UnitySpikes/Builds/Phase2/<unityVersion>/mono/Phase2.exe \
  -logFile D:/UnitySpikes/phase2-<ver>-mono-player.log
```

结果文件：`%LOCALAPPDATA%Low\DefaultCompany\<工程名>\phase2-results.txt`（以日志 `PHASE2_RESULTS_PATH` 为准）；日志里 `PHASE2 <name>|OK|...` 为判据。
`link.xml` 目标程序集为 `Assembly-CSharp`；若把源码放入自定义 asmdef，必须同步改 `fullname`。

## 运行前置（2026-11-07 实测补充）

```bash
# 1. 起本地 SSE 服务（15 场景全部依赖 5199；U7 两格另需外网 self-signed.badssl.com 可达）
dotnet run -c Release --project spikes/SseTestServer

# 2. 跑玩家（产物目录按 Application.unityVersion；2021.3 实际是 2021.3.45f1c2）
D:/UnitySpikes/Builds/Phase2/<unityVersion>/<backend>/Phase2.exe \
  -logFile D:/UnitySpikes/phase2-<ver>-<backend>-player.log

# 3. 摘结果
grep "PHASE2 " D:/UnitySpikes/phase2-<ver>-<backend>-player.log \
  > D:/UnitySpikes/phase2-<ver>-<backend>-results.txt
```

- 玩家自跑 15 场景后自动退出（`Application.Quit`），无需手动关闭；一轮约 30–60 秒。
- 进程纪律：SSE 服务用 PID 级 kill（`kill $SERVER_PID` / `taskkill //PID <pid>`），**禁止 `taskkill /IM dotnet.exe`**。
- 结果与证据落盘：`docs/spike/phase2-results.md`（2026-11-07 三版本 × Mono/IL2CPP 全 15/15 OK）。
