# IL2CPP / link.xml（Phase 0 Task 7）

> Spike 结论先行：**2021.3.45f1 / 6000.3.28f1 / 6000.6.8f1 三版本 IL2CPP 均已实测通过**：有 `link.xml` 时 `/sse` 回调 6 次、Abort 生效、本地 ws 可用；**2021.3 无 `link.xml` 对照构建虽成功，但运行卡死在首个 UWR 请求**（`Curl error 42: Callback aborted`）——证明 `link.xml` 是必要件（对照只在 2021.3 跑）。

## 环境（实测时点）

- **Unity 2021.3.45f1 + Windows IL2CPP**：已安装并执行；VS 2026 C++ 工作负载（MSVC 14.44/14.51 + Windows SDK）已就绪
- **6000.6.8f1**：已安装，但只有 Mono；缺 `Windows Build Support (IL2CPP)` 模块
- **6000.3.28f1**：已安装，但许可证未激活（exit 198）
- 仓库侧事实（本次已核，非实测游戏行为）：
  - `spikes/UnityUwrSpike/` 只提交资产（2 脚本 + 2 README），Unity 工程由人手动创建——步骤见 `spikes/UnityUwrSpike/README.md`「Unity 工程创建」节
  - `Assets/Scripts/` 下**无 `.asmdef`、无 `.meta`** → 两脚本编译进 **`Assembly-CSharp`**，brief 的 `<assembly fullname="Assembly-CSharp">` 与之对齐，无需改
  - `Assets/link.xml` 位于 Assets 根：Unity 构建时自动读取 Assets 下任意 `link.xml`；它只在构建管线里生效，对 Editor Play 无作用（见观察 1）
- 执行者需满足：Unity 2021.3 LTS（含 Windows IL2CPP 工具链）+ Task 3 的 Unity 工程已建好 + 本地 SSE 服务（Task 2）可达

## 资产与机制（为什么是 link.xml）

落盘资产（本提交新增 1 文件）：

```xml
<!-- spikes/UnityUwrSpike/Assets/link.xml，与 brief Step 7.1 逐字一致 -->
<linker>
  <assembly fullname="Assembly-CSharp">
    <type fullname="StreamingDownloadHandler" preserve="all" />
  </assembly>
</linker>
```

- 保护对象：`Assets/Scripts/StreamingDownloadHandler.cs`——`DownloadHandlerScript` 子类，override `ReceiveData` 记逐块 `(t, bytes)`
- 风险机制（**brief 前提 + 文献印象，未实测**）：IL2CPP 的 managed stripping（UnityLinker）按托管引用图裁码；Unity 内部 native→managed 的虚回调（DownloadHandler 的 override 属此类）对链接器不可见，可能被判"不可达"裁掉。症状是 Editor/Mono 正常、IL2CPP 构建里 `ReceiveData` 永不回调。**是否真剥、默认剥离等级是什么，一律以实机构建结果定稿**——矩阵里专设"无 link.xml 对照"格做归因
- `preserve="all"` 保住：类型 + 全部方法（含 `ReceiveData` override 与 `base(new byte[16384])` 构造链）+ 全部字段（`Chunks`/`CallbackCount`）。单个类型全保，体量代价可忽略
- ⚠️ 若将来给 `Assets/Scripts/` 加 `.asmdef`，程序集名不再是 `Assembly-CSharp`，此 link.xml 的 `fullname` 必须跟着改（否则保了个空名，等于没保）

## 复现命令（手动；Unity 侧无可脚本化命令）

```bash
# 0) 本地 SSE 服务（Task 2 产物，实验回包源）
dotnet run --project spikes/SseTestServer -c Release    # http://127.0.0.1:5199/（/sse、/sse-error、/sse-gzip）

# 1) Unity 工程一次性创建：照 spikes/UnityUwrSpike/README.md「Unity 工程创建」节（人手动）
# 2) 确认 Assets/link.xml 在 Assets 根（本提交已带；建工程时别把它挪出 Assets）
# 3) File → Build Settings → 平台 Windows → Scripting Backend 切 IL2CPP → Build
#    （记录 Unity 版本 + Managed Stripping Level 设置；产物落 Build/，仓库 .gitignore 已忽略）
# 4) 运行 Build 产物，挂 UwrSpike 跑 Task 3 手动清单（spikes/UnityUwrSpike/README.md「手动执行清单」）
# 5) 对照格：临时把 link.xml 改名/移出 Assets 再 Build 一次，同脚本重跑（归因用，见矩阵最后一格）
```

## 矩阵

固定表头：栈 | 网络 | 代理 | TLS | 结果 | 错误 | 证据

| 栈 | 网络 | 代理 | TLS | 结果 | 错误 | 证据 |
|---|---|---|---|---|---|---|
| Mono（2021.3 / 6.6，Task 3 基线） | http://127.0.0.1:5199/sse | – | 无 | **✅ 已通过** | – | 2021.3：callbacks=6/first=0.050/elapsed=0.615；6.6：6/0.017/0.592 |
| **IL2CPP 2021.3 × 有 link.xml** | /sse | – | 无 | **✅ 通过** | – | Success/200/callbacks=6/first=0.026/elapsed=0.626；`link.xml` 生效 |
| IL2CPP 2021.3 × Abort | /sse 0.15s 后 Abort | – | 无 | **✅ Abort 生效** | result=ConnectionError/error=Request aborted | 回调 2→2（1s 后不增加） |
| IL2CPP 2021.3 × 错误端口 | http://127.0.0.1:5198/sse | 系统代理开 | 无 | **✅ 可分类** | ProtocolError/502（系统代理返回） | elapsed≈2.9s；同 Mono |
| IL2CPP 2021.3 × 500 | /sse-error | – | 无 | **✅ 可读错误体** | ProtocolError/500 | body=`{"error":"boom"}` |
| IL2CPP 2021.3 × gzip | /sse-gzip | – | 无 | **✅ 仍逐块** | – | Success/200/callbacks=6/first=0.016/elapsed=0.600；内容未校验 |
| IL2CPP 2021.3 × **无 link.xml 对照** | /sse | – | 无 | **❌ 卡死/失败** | `Curl error 42: Callback aborted`；结果文件未更新 | 构建成功但运行卡在首个 UWR 请求 → **link.xml 是必要件** |
| IL2CPP 2021.3 × WebSocket | ws://127.0.0.1:5200/ws/ | 系统代理开 | 无 | **✅ 通过** | – | `type=Text bytes=10 text=echo:hello` |

## 执行记录（2026-11-06）

| 版本 | 状态 | 证据 |
|---|---|---|
| 2021.3.45f1 | ✅ IL2CPP 构建成功并运行通过；无 link.xml 对照卡死 | VS 2026 C++ 工作负载（MSVC 14.44/14.51 + Windows SDK）+ `link.xml` |
| 6000.6.8f1 | ✅ IL2CPP 模块已装、构建/运行通过 | Hub 独立安装包 + UAC 批准 |
| 6000.3.28f1 | ✅ 许可证恢复（D 盘 Unity Hub 3.3.7 注册后）、IL2CPP 构建/运行通过 | 见 `docs/spike/uwr-streaming.md` |
| Mono 基线 | ✅ 2021.3 / 6.3 / 6.6 已跑通（非 IL2CPP，仅对照） | 见 `docs/spike/uwr-streaming.md` |

## 观察（机制侧，非实测）

1. **link.xml 的作用域是构建管线，不是 Editor**：Editor Play 不走 managed stripping，Editor 里 `ReceiveData` 正常回调**不能证明** link.xml 有效——唯一有效证据是 IL2CPP 构建产物里实跑。
2. **Editor/Mono 绿只是基线不是结论**：本矩阵把 Mono 格单独列出就是防止"Editor 里好好的"被当成 IL2CPP 证据。brief 的降级路径写死：IL2CPP 失败 → 记错误原文 → "仅 Mono"，届时 Task 8 汇总表 S0-5 格照填"仅 Mono"。
3. **对照格是本矩阵的价值所在**：只跑"有 link.xml"一格，绿了也归因不出是 link.xml 的功劳还是默认剥离等级本来就不剥；有/无各 Build 一次才能把结论钉死。Build 两次的成本远小于事后争论。
4. **ws 侧 IL2CPP 与本任务不同轴**：`System.Net.WebSockets` 在 IL2CPP 下能否编译/连是另一问（Task 5 矩阵 C 表已挂 pending）；不要拿本任务的 link.xml 结果外推到 ws。
5. **程序集名绑定**：本 link.xml 写死 `Assembly-CSharp`，依赖"`Assets/Scripts/` 无 asmdef"这一仓库事实；任何人加 asmdef 就必须同步改 fullname——已在资产节立警告。

## 待办（Unity 手动部分，brief Step 7.2/7.3）

- [x] 2021.3.45f1：IL2CPP Windows Build 成功（VS 2026 C++ 工作负载 + `link.xml`）
- [x] 2021.3.45f1：`/sse` 回调 6 次、首块 0.026s、总时长 0.626s
- [x] 2021.3.45f1：Abort 生效（2→2）
- [x] 2021.3.45f1：错误端口 / `/sse-error` / `/sse-gzip` 三格
- [x] 2021.3.45f1：无 link.xml 对照卡死（`Curl error 42: Callback aborted`）→ link.xml 必要
- [x] 2021.3.45f1：IL2CPP 下本地 ws 通过
- [x] 6000.6.8f1：IL2CPP 模块安装、构建/运行通过
- [x] 6000.3.28f1：许可证恢复、IL2CPP 构建/运行通过
- [ ] 每格回填本矩阵；IL2CPP 失败 → 记错误 + 降级"仅 Mono"并在 Task 8 汇总注明
