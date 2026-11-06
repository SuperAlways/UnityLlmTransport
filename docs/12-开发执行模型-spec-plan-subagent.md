# 12 · 开发执行模型（spec → plan → subagent）

> **目的**：规定每个阶段怎么从设计走到代码：谁写 spec、谁写 plan、谁执行、谁审查、用什么模型、门禁是什么。
> **依据**：`docs/11-开发阶段划分.md`；superpowers 流程（brainstorming / writing-plans / subagent-driven-development / requesting-code-review）。
> **日期**：2026-11-06

---

## 1. 总流程

```
阶段开工
  → 读上一阶段门禁/文档/参考结论
  → 阶段 spec（设计变更时；用户 review）
  → 阶段 plan（writing-plans；任务级 TDD）
  → SDD：每任务 implementer → task reviewer → 修复/复审
  → 阶段末 final review（whole phase）
  → 门禁：构建/单测/实机矩阵
  → 用户验收 → merge
  → 更新 README/阶段文档/复盘
```

**Phase 0（Spike）例外**：不写生产代码、不走完整 SDD；time-box 探索 → 事实报告 → go/no-go。

---

## 2. 阶段产物与路径

```
docs/superpowers/specs/YYYY-MM-DD-<phase>-design.md   // 阶段 spec
docs/superpowers/plans/YYYY-MM-DD-<phase>.md          // 阶段 plan
.superpowers/sdd/progress.md                          // SDD 台账
docs/13-阶段复盘-<phase>.md                            // 阶段末复盘
```

- spec 必须包含：接口变化、行为、测试、平台矩阵、偏离/代价、许可证约束。
- plan 必须包含：任务拆分（3–7 个）、每任务文件、TDD 步骤、命令、验收。
- 每个任务由独立 implementer 执行；任务 reviewer 检查 spec 合规 + 代码质量。
- 阶段末由强模型做 whole-phase review；通过后进入门禁。

---

## 3. 模型路由

| 工作 | 模型 | 理由 |
|---|---|---|
| 架构/接口设计、spec 评审 | **Opus** | 边界、能力面、长期兼容 |
| 安全/签名/并发/生命周期评审 | **Opus** | 错一次代价高 |
| 阶段末 whole-phase review | **Opus** | 全局一致性 |
| UWR/HttpClient/WebSocket 实现 | **Sonnet** | 多文件、平台差异、调试 |
| 认证中间件、重试/超时装饰器 | **Sonnet** | 语义与边界 |
| 任务 reviewer | **Sonnet**（高风险任务可 Opus） | 需要判断 spec/质量 |
| 机械 DTO/接口文件、vendor 移植、测试夹具、文档搬运 | **Haiku** | 低判断、可验证 |
| 简单单文件修复 | **Haiku**，两次失败升 Sonnet | 成本 |

**升级阶梯**：Haiku 两次失败 → Sonnet；Sonnet 两次失败 → Opus；仍失败 → 停下来找根因，不原地重试。

> 轮次成本 > token 单价：多文件集成、调试、平台适配不要硬用 Haiku；机械搬运才用。

---

## 4. 子 agent 角色

### Implementer

- 输入：task brief（唯一需求来源）、仓库路径、上一任务接口、报告文件路径；
- 必须 TDD：先失败测试 → 实现 → 通过 → 提交；
- 必须跑覆盖测试并报告命令/输出；
- 只改任务范围；不复制无 LICENSE 代码；
- 返回：状态、commit、测试摘要、concern。

### Task reviewer

- 输入：brief、report、review package（base..head diff）；
- 输出两个结论：**spec 合规** + **代码质量**；
- 发现 Critical/Important → 派 fix subagent → 复审；
- Minor 记入 ledger，留给阶段末 review 分诊。

### Final reviewer（阶段末）

- 输入：阶段 merge-base..head 的 review package；
- 检查：接口一致性、平台矩阵、测试缺口、许可证、偏离表；
- 输出：Ready / With fixes / No。

---

## 5. 分支与合并

- 每个阶段一个分支：`phase/N-<name>`（或 worktree）；
- `main` 保持可发布；
- 阶段内可多个 commit；阶段末 squash 或保留；
- 合并前提：
  1. 单测全绿；
  2. Release 构建 0/0；
  3. 阶段门禁（实机/矩阵）通过；
  4. final review 通过；
  5. **用户明确同意**。

---

## 6. 测试门禁

| 阶段 | 必须 |
|---|---|
| Core | 纯 .NET 单测；无 Unity 引用；契约测试（SSE/header/超时/重试） |
| UWR | Unity Mono 实机：真流式、取消、首字节/空闲、溢出、responseCode=0；IL2CPP 冒烟 |
| HttpClient | 桌面：代理/自签/非 2xx/慢流/取消 |
| Auth | API Key、401 refresh、finalizer header 不被改写 |
| WebSocket | 本地 ws、远程 wss、IL2CPP、WebGL **分别**验收 |
| Release | 全矩阵 + Release 0/0 + 许可证/NOTICE |

---

## 7. 参考与许可证纪律

- 只读参考仓库，不修改；
- **vendor 允许**：System.Net.ServerSentEvents（MIT）、NativeWebSocket（Apache-2.0，保留 NOTICE）；
- **只参考模式**：LaunchDarkly、com.utilities.rest、com.openai.unity、Azure fetch、SsePulse；
- **禁止复制**：Yaolao、WorldTalk、江湖有灵、mikito（无 LICENSE/反编译）；
- 任何新增依赖必须在 spec 的“依赖/许可证”一节登记。

---

## 8. 阶段执行示例：Phase 1 · Core

1. **Spec**（Opus 主笔，用户 review）：
   - 接口、header 语义、超时/重试/observer/finalizer/selector 形状；
   - 测试矩阵；偏离表。
2. **Plan**（Sonnet 写，用户 review）：
   - S1-1 Abstractions；
   - S1-2 Sse vendor；
   - S1-3 Timeout；
   - S1-4 Middleware/Headers；
   - S1-5 Retry；
   - S1-6 Observer/Finalizer/Selector；
   - S1-7 Mock + tests。
3. **SDD**：
   - 每个任务：Haiku（机械接口/DTO）或 Sonnet（装饰器/语义）；
   - task reviewer Sonnet；
   - fix 循环。
4. **Final review**（Opus）+ Release 门禁。
5. **用户验收** → merge。

---

## 9. 反模式

1. 没 spec 先写代码；
2. 一个任务塞多个关注点；
3. 用 Haiku 做并发/签名/平台适配；
4. 跳过实机，只靠单测宣称“Unity 可用”；
5. 把本地 `ws://` 当远程 `wss://` 证据；
6. 复制无 LICENSE 代码；
7. 阶段末不更新文档/门禁记录；
8. 没经用户同意就 merge main。
