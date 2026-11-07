# Phase 5 Unity Harness

## 前置

```bash
# 终端 A：本地 ws echo（5200）
dotnet run -c Release --project spikes/WsTestServer
```

W5 需要外网访问 `wss://ws.postman-echo.com/raw`（系统代理/系统 CA）。

## 装配/构建/运行

```bash
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -createProject D:/UnitySpikes/Phase5-<ver> -batchmode -quit -logFile D:/UnitySpikes/create-phase5-<ver>.log
bash scripts/assemble-phase5-unity.sh D:/UnitySpikes/Phase5-<ver>
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -batchmode -quit -projectPath D:/UnitySpikes/Phase5-<ver> -executeMethod Phase5Build.BuildMono -logFile D:/UnitySpikes/phase5-<ver>-mono.log
D:/UnitySpikes/Builds/Phase5/<unityVersion>/mono/Phase5.exe -logFile D:/UnitySpikes/phase5-<ver>-mono-player.log
```

判据：`PHASE5 W1_text|OK|...` 至 `W6_error_port|OK|...`；`W6 kind` 为 `Connect` 或 `Handshake`；结果文件 `%LOCALAPPDATA%Low\DefaultCompany\<工程名>\phase5-results.txt`。
服务端 PID 级清理；禁止 mass-kill dotnet。
