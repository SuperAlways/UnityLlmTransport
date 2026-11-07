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
