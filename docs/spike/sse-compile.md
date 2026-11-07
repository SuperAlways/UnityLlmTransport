# S0-1 · System.Net.ServerSentEvents 在 netstandard2.1 编译（Phase 0 Task 1）

> 结论先行：**vendor 可行**。.NET 运行时仓库的 `System.Net.ServerSentEvents` 源码按 netstandard2.1 + C# 9 编译通过（0 警告 0 错误、零 NuGet 依赖），4/4 边界单测全绿。
> 详细过程/逐条降级明细：`.superpowers/sdd/phase0-task-1-report.md`（台账，不入仓库）；本文是入库事实版。

## 落盘资产

| 位置 | 内容 |
|---|---|
| `spikes/SseCompile/` | vendored 库：`System/Net/ServerSentEvents/`（9 个源文件）+ polyfill 补件 |
| `spikes/SseCompile.Tests/` | xUnit 测试工程（net10.0 宿主）+ `SseParserSmokeTests.cs` 4 条边界测试 |

Polyfill/shim 补件（vendor 源码之外的最小新增）：

| 文件 | 缘由 |
|---|---|
| `System/Net/ArrayBuffer.cs` | 从 runtime 仓库 `src/libraries/Common/src/System/Net/` 补拷（SseParser 依赖） |
| `System/Net/ServerSentEvents/SR.cs` | runtime 的 SR 由 resx 生成、不在拷贝源码里；四个资源串逐字取自 `Strings.resx` |
| `System/ArrayMaxLengthShim.cs` | netstandard2.1 面没有 `Array.MaxLength`（.NET Core 3.0 才进 BCL）；常量 `0x7FFFFFC7` 与 .NET 运行时一致 |
| `IsExternalInit.cs` | netstandard2.1 无 `IsExternalInit`，`SseItem<T>` 等用了 init 属性 |

## C# 9 降级（原文是 C# 11/12）

1. `u8` 字符串字面量（C# 11）→ `Encoding.UTF8.GetBytes("...")`（`SseParser_1.cs` / `SseFormatter.cs` 共 10 处）；
2. 集合表达式 `[]`（C# 12）→ `new byte[0]` / `new byte[] {...}`；
3. `Array.MaxLength` → `ArrayMaxLengthShim` 常量；
4. string→`ReadOnlySpan<char>` 隐式转换（.NET Core 3.0+）→ `Helpers.ContainsLineBreaks` 加 string 接收者重载；
5. 结构体字段显式置默认值（C# 11 自动置默认，C# 9 需手写，CS0171）；
6. span `Contains(byte)` → `IndexOf(byte) < 0`（netstandard2.1 的 span Contains 要求显式 comparer）。

## 测试与判据

4/4 通过（clean-slate 删 `bin/`/`obj/` 复验过，排除 Release 缓存假绿）：

| 测试 | 边界 |
|---|---|
| `MultiLine_Comment_Fields` | 注释行忽略、多 `data:` 以 `\n` 拼接、event/id、CRLF |
| `Lf_And_Done` | 纯 LF、`[DONE]` 哨兵按普通 data |
| `Cr_Only` | 孤 `\r` 行尾 |
| `Eof_Half_Event_Is_Dropped` | 半截事件 EOF 丢弃 |

## 复现命令

```bash
dotnet build spikes/SseCompile/SseCompile.csproj -c Release --nologo
dotnet test spikes/SseCompile.Tests/SseCompile.Tests.csproj -c Release --nologo
# 期望：已通过! - 失败: 0，通过: 4，已跳过: 0，总计: 4
```

## 已知注意点（对 Phase 1）

- **测试宿主侧 CS0433**：.NET 10 共享框架自带同名 `System.Net.ServerSentEvents`，测试工程用 `ProjectReference Aliases="SseVendored"` 消歧；Unity 的 netstandard2.1 profile 不存在该库，真实部署无此问题。
- **热路径分配**：`u8→GetBytes` 后每行最多 4 次 `byte[]` 分配（字段名比较）；进生产前改 `static readonly byte[]` 缓存（Phase 1 再议）。
- 证据路径：`spikes/SseCompile.Tests/`（测试原文 + alias 处理）。
