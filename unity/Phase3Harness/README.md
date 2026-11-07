# Phase 3 Unity Harness

## 前置

```bash
# 终端 A：本地测试服务（HTTP 5201 / HTTPS 5233 / 显式代理 5234）
dotnet run -c Release --project spikes/Phase3TestServer
```

## 装配/构建/运行

```bash
# 一次性建工程
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -createProject D:/UnitySpikes/Phase3-<ver> -batchmode -quit -logFile D:/UnitySpikes/create-phase3-<ver>.log
bash scripts/assemble-phase3-unity.sh D:/UnitySpikes/Phase3-<ver>
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -batchmode -quit -projectPath D:/UnitySpikes/Phase3-<ver> -executeMethod Phase3Build.BuildMono -logFile D:/UnitySpikes/phase3-<ver>-mono.log
D:/UnitySpikes/Builds/Phase3/<unityVersion>/mono/Phase3.exe -logFile D:/UnitySpikes/phase3-<ver>-mono-player.log
```

判据：日志 `PHASE3 <name>|OK|...`；结果文件 `%LOCALAPPDATA%Low\DefaultCompany\<工程名>\phase3-results.txt`。
本阶段只跑 Mono（IL2CPP 不声明）；服务端为外部进程，结束后按 PID 关闭，禁止 mass-kill dotnet。
