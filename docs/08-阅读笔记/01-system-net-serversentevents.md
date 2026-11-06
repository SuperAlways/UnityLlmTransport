# 01 · dotnet/runtime `System.Net.ServerSentEvents`（SseParser）阅读笔记

- 参考仓库：`refs/01-dotnet-runtime-system-net-serversentevents`（sparse checkout，只有 `src/libraries/System.Net.ServerSentEvents` + 根配置）
- 快照：commit `a71484c`（2026-11-05，dotnet/runtime main），来源 github.com/dotnet/runtime
- 视角：Unity 2021 + netstandard2.1 + AI mod 真·流式 SSE 消费（HTTP/SSE、取消、主线程调度）

## 1. 目标框架 / 依赖 / 许可证

- csproj 多目标：`$(NetCoreAppCurrent);netstandard2.0;$(NetFrameworkMinimum)` 等（`src/.../System.Net.ServerSentEvents.csproj:4`）——即 NuGet 包以 **netstandard2.0** 出厂，天然可用于 netstandard2.1 工程。
- 非(netCoreApp) 目标的依赖（`.csproj:33-38`）：`Microsoft.Bcl.AsyncInterfaces`（ProjectReference）、`System.Memory`、`System.Threading.Tasks.Extensions`（PackageReference）；外加 3 个 polyfill 源码：`IsExternalInit.cs`、`ArrayPolyfills.cs`、`StreamMemoryPolyfills.cs`（`.csproj:27-31`）；`IncludeSpanPolyfills=true`（`.csproj:11`）再引入 5 个 Span 系 polyfill（`src/libraries/Directory.Build.targets:211-218`）。
- 许可证：MIT（`LICENSE.TXT:1-3`；`PACKAGE.md:73`）。可 vendor、可改名、可商用。

## 2. API 形状与增量状态机

工厂（`SseParser.cs:30,42,51`）：
```csharp
SseParser<string> p = SseParser.Create(stream);                          // 默认 UTF8.GetString
SseParser<T>      p = SseParser.Create(stream, (eventType, ReadOnlySpan<byte> data) => T); // SseItemParser.cs:11
await foreach (SseItem<T> item in p.EnumerateAsync(ct)) ...              // SseParser_1.cs:149
foreach (SseItem<T> item in p.Enumerate()) ...                           // SseParser_1.cs:101
```
- `SseItem<T>` readonly struct：`Data` / `EventType`（缺省 "message"，`SseParser.cs:12`）/ init-only `EventId`、`ReconnectionInterval`（`SseItem.cs:33-70`）。
- `SseParser<T>` **一次性使用**（`Interlocked` 守卫，`SseParser_1.cs:403-409`；测试 47-66）——重连要 new 新 parser。
- 跨 chunk 增量状态（`SseParser_1.cs:58-96`）：两个 ArrayPool 托管的 `ArrayBuffer`——`_lineBuffer`（读入 chunk、找换行、`Discard` 已消费）与 `_dataBuffer`（跨行累积 `data:` 值，行间插 LF，`SseParser_1.cs:335-343`）；字段 `_eventType/_eventId/_nextReconnectionInterval/_dataAppended` 只在事件派发时清空（`:250-258`），因此**半截事件可安全跨任意次 `MoveNextAsync` 续读**。`_lastSearchedForNewline`（`:60-66,219-225`）避免长行多次读时的 O(n²) 重扫。
- 取消：token 直通 `_stream.ReadAsync(memory, ct)`（`:441`）——阻塞读时取消立即生效；`[EnumeratorCancellation]` 让 `GetAsyncEnumerator(ct)` 也生效（测试 `SseParserTests.cs:755-772`）。parser 在两次 MoveNextAsync 之间不空转，正是"来一批 token 解析一批"的真流式形态。
- 客户端重连辅助：parser 级滚动属性 `LastEventId`（跨事件持久，`:391`）与 `ReconnectionInterval`（默认 `Timeout.InfiniteTimeSpan`，`:400`）；重发 `Last-Event-ID` 头的逻辑在用户侧。

## 3. 边界处理（对照 WHATWG SSE spec，`SseParser_1.cs:21` 引 spec）

| 场景 | 行为 | 证据 |
|---|---|---|
| CR / LF / CRLF | 三种全认；缓冲区尾部的孤立 CR 暂缓，等下一读配对 LF，避免把 CR 当成独立行 | `SseParser_1.cs:198-235` |
| UTF-8 BOM | 可选跳过；半截 BOM/中断 BOM 都处理 | `SseParser_1.cs:473-479`；测试 590-711 |
| 多行 `data:` | 每行追加值+LF，派发时不含尾 LF；spec 例 1/3/5 逐字对齐 | `SseParser_1.cs:335-343`；测试 148-230, 346-386 |
| 注释 `:xxx` 与未知字段 | 忽略 | `SseParser_1.cs:377-382` |
| `event:` | 设 `_eventType`，下个事件消费后复位；无 data 的事件行会复位避免"串味" | `:345-349,264-265`；测试 171-188 |
| `id:` | 含 NUL 忽略；空值重置为 ""；同时更新滚动 `LastEventId`；未设则继承上个 id | `:350-357`；测试 232-342 |
| `retry:` | 仅 ASCII 数字；负数/溢出/非法忽略；上限 TimeSpan.MaxValue | `:359-375`；测试 434-549 |
| 空行 | `_dataAppended` 才派发，否则 no-op；多空行安全 | `:246-267`；测试 84-145 |
| EOF 半截事件 | 按 spec 丢弃，不派发 | `:127-132,174-180` |
| `[DONE]` | **库不管**；官方测试 `MultipleItemParsers_OpenAI_StreamingResponse` 演示在 `SseItemParser` 里 `bytes.SequenceEqual("[DONE]"u8)` 分流 | 测试 791-817 |
| 超 buffer | `MaxBufferSize`（默认 1GB，`:47`）超限抛 `InvalidDataException` | `:324-327,456-466`；测试 940-1054 |
| 零拷贝快路径 | 单行 data 后紧跟空行 → 直接从 line buffer 解析，不进 dataBuffer | `:300-318` |

payload 一律以 `ReadOnlySpan<byte>` 交给 delegate，不自行解码；delegate 抛异常会向上传播（测试 733-753）。

## 4. 测试覆盖

`tests/`：SseParserTests.cs（1187 行）+ SseItemTests.cs（51）+ SseFormatterTests.cs（250，写侧）。几乎每条功能测试都是 Theory：换行符(3) × trickle 逐字节读(2) × 同步/异步(2)=12 轴（`SseParserTests.cs:1064-1068`）；`TrickleStream` 每次只给 1 字节 + `Task.Yield`（`:1113-1139`），专治 chunk 切分。覆盖 WHATWG spec 6 个官方示例逐字、BOM 4 变体、100 条渐长长行（压 buffer 平移逻辑，`:715-730`）、单次枚举守卫、取消、delegate 异常传播、MaxBufferSize 下限提升+超限、ArrayPool 两种租借姿势、OpenAI chat.completion.chunk+[DONE] 真实样例。测试基建（xunit+AssertExtensions+STJ 源生成）不随库走，但语义可直接移植。

## 5. vendor 到 netstandard2.1（Unity 2021）可行性

读侧需要的文件（写侧 `SseFormatter.cs`+`PooledByteBufferWriter.cs` 对 LLM 客户端可整个裁掉）：
1. `src/System/Net/ServerSentEvents/` 下 7 个 .cs：SseParser.cs、SseParser_1.cs、SseItem.cs、SseItemParser.cs、SseParserOptions.cs、ThrowHelper.cs、Helpers.cs（裁写侧后只剩 `ContainsLineBreaks` 一个方法，`Helpers.cs:56-57`，也可内联）。
2. **`ArrayBuffer.cs` 不在本 sparse checkout 里**——csproj 从 `$(CommonPath)System\Net\ArrayBuffer.cs` 引入（`.csproj:15`），需从同一 commit 的 `src/libraries/Common/src/System/Net/ArrayBuffer.cs` 另取，取回后要核它有无 netstandard2.1 缺的 API（⚠️推测：大概率干净，全是 ArrayPool/Span，两者 netstandard2.1 原生自带）。
3. 手写 4 条 SR 字符串（resx 仅 4 个 key：EnumerateOnlyOnce/CannotContainLineBreaks/CannotBeNegative/SseExceededMaxLength）。

netstandard2.1 原生覆盖 Span/Memory/IAsyncEnumerable/ValueTask/`Stream.ReadAsync(Memory,ct)`/ArrayPool → **不需要 System.Memory、Bcl.AsyncInterfaces 任何 NuGet**（csproj 列它们是因 netstandard2.0 目标）。

冲突点（全部机械可修）：
- `init` 访问器（`SseItem.cs:40-52,58-70`）→ 带上 1 个 `IsExternalInit` 空类 polyfill。
- C# 11 `"..."u8` 字面量：SseParser_1.cs 5 处（`\r\n`/`data`/`event`/`id`/`retry`）+ SseFormatter 5 处；C# 12 集合表达式 `[0xEF,0xBB,0xBF]`（`SseParser_1.cs:470`）。Unity 2021 默认 C# 9（⚠️推测）→ 替换为 `static readonly byte[]`/`Encoding.UTF8.GetBytes` 字段，约 11 处。
- `#if NET` 分支在 netstandard2.1 下自动走 else（byte[] 重载 Read、retry 用 GetString 解析，`:363-369,415-422`）——**零改动**。
- IL2CPP/AOT：纯托管、无反射，async 迭代器+struct+span 均可编译（⚠️推测，未实机验证）。
- NuGet 备选：包本身就发 netstandard2.0，可直接装进 netstandard2.1 工程；但会拖进 System.Memory/Bcl.AsyncInterfaces DLL，Unity 里与 profile 自带 Span 类型的程序集版本冲突是常见坑（⚠️推测，未实测），vendor 单文件反而零依赖图。

## 6. 结论

**vendor（读侧 8 文件 + IsExternalInit + SR 桩），评分 4.5/5。**
理由：MIT、WHATWG spec 对齐最权威（官方测试逐字覆盖 spec 6 例 + OpenAI [DONE] 样例）、状态机经 12 轴 trickle 测试、零游戏侧依赖、u8/集合表达式替换纯机械；扣分在 ArrayBuffer.cs 需另取、C#11 语法降级、以及它只是**解析器**——分层超时/代理/TLS/主线程调度全要在自己 HttpClient 封装层做。
若目标改成纯 .NET/服务端（非 Unity），则直接 NuGet `System.Net.ServerSentEvents` 即 5/5。
