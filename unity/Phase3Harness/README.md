# Phase 3 Unity Harness

## 前置

```bash
# 终端 A：本地测试服务（HTTP 5201 / HTTPS 5233 / 显式代理 5234）
dotnet run -c Release --project spikes/Phase3TestServer
```

> 后台跑服务（脚本化时）两个实测坑：
> 1. `dotnet run` 包一层父进程，kill 打不到真实 apphost——直接跑 exe：`./spikes/Phase3TestServer/bin/Release/net10.0/Phase3TestServer.exe`；
> 2. 服务端 `Console.ReadLine()` 在无 stdin 时立即返回导致进程自退——后台运行必须挂住 stdin（如 `< <(sleep N)`）。
> 收尾：`netstat -ano | grep -E ":(5201|5233|5234)\s.*LISTENING"` 拿 PID → `taskkill //PID <pid> //T //F` 点杀，再核验端口清零。**禁止 mass-kill dotnet。**

## 装配/构建/运行

```bash
# 一次性建工程
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -createProject D:/UnitySpikes/Phase3-<ver> -batchmode -quit -logFile D:/UnitySpikes/create-phase3-<ver>.log
bash scripts/assemble-phase3-unity.sh D:/UnitySpikes/Phase3-<ver>
D:/Unity/Hub/Editor/<version>/Editor/Unity.exe -batchmode -quit -projectPath D:/UnitySpikes/Phase3-<ver> -executeMethod Phase3Build.BuildMono -logFile D:/UnitySpikes/phase3-<ver>-mono.log
D:/UnitySpikes/Builds/Phase3/<unityVersion>/mono/Phase3.exe -logFile D:/UnitySpikes/phase3-<ver>-mono-player.log
```

> 改了 `src/` 之后重跑：先重新 `assemble`（脚本会重写 `Assets/UnityLlmTransport`），再重建 player——Unity 工程里的拷贝不会自己跟进源码。
> 2021.3 产物目录是 `Builds/Phase3/2021.3.45f1c2/`（`Application.unityVersion` 带 c2 后缀）；6.3/6.6 与编辑器版本号一致。

判据：日志 `PHASE3 <name>|OK|...`；结果文件 `%LOCALAPPDATA%Low\DefaultCompany\<工程名>\phase3-results.txt`。
本阶段只跑 Mono（IL2CPP 不声明）；服务端为外部进程，结束后按 PID 关闭，禁止 mass-kill dotnet。
证据：`grep "PHASE3 " <player.log> > phase3-<ver>-mono-results.txt`（头 2 行 + 12 行 `|OK|`），入库 `docs/spike/evidence/phase3/`。
