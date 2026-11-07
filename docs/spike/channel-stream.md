# S0-6 · ChannelReadStream 桥接（Phase 0 Task 6）

> 结论先行：**桥接链路成立**。`Channel<byte[]>` → `ChannelReadStream : Stream` → vendored `SseParser.Create(stream).EnumerateAsync()`，byte-by-byte 分片也能拼回完整事件——**单测 1/1 通过**（UWR `ReceiveData` 回调块喂 parser 的前提成立）。
> **但本 spike 只测了"能消费"，brief/spec 里的取消/Abort 与无死锁判据未测**，Phase 2 adapter 实现时必须补（见下"未测判据"）。汇总见 `docs/spike/phase0-summary.md` §2/§5。

## 落盘资产

| 位置 | 内容 |
|---|---|
| `spikes/ChannelBridge/ChannelReadStream.cs` | brief 原文代码，一字未改（netstandard2.1 零改动可编） |
| `spikes/ChannelBridge/ChannelBridge.csproj` | netstandard2.1 + `LangVersion=9.0`；NuGet `System.Threading.Channels 8.0.0` |
| `spikes/ChannelBridge.Tests/` | xUnit（net10.0 宿主）+ `ChannelReadStreamTests.cs` |

依赖注意：`System.Threading.Channels` **不在** netstandard2.1 ref pack 内，必须显式 NuGet 引用（该包 netstandard2.0 目标，Unity 2021.2+ 兼容面）。

## 测试内容（1/1 passed）

UTF-8 `"data: 你\r\n\r\ndata: 好\r\n\r\n"` 逐字节（每 chunk 1 字节）写入 unbounded Channel → `ChannelReadStream` → parser 枚举出恰好 2 条 item（Data = `"你"`/`"好"`）——证明 `byte[]` 分片跨 chunk 拼接 + parser 消费 `Stream` 全链路可用。

```bash
dotnet test spikes/ChannelBridge.Tests/ChannelBridge.Tests.csproj -c Release --nologo
# 期望：已通过! - 失败: 0，通过: 1，已跳过: 0，总计: 1
```

## 未测判据（spec 有、spike 没测——Phase 2 必补）

| 判据 | 状态 |
|---|---|
| 取消 / Abort（`CancellationToken` 贯通 `ReadAsync`，取消后 `WaitToReadAsync` 立即退出不挂死） | **❌ 未测**——测试里 `TryComplete()` 走的是"正常收尾"路径 |
| 无死锁（多生产者/连续大块、消费端慢读时 `WaitToReadAsync`/`TryRead` 交互） | **❌ 未测**——单测试单线程写入后消费 |

Phase 2 UWR adapter 的 `ReceiveData` 是主线程回调 + 消费在别的线程/协程，这两条是真实风险面。

## 已知限制（代码现状，进生产前要改）

1. **`TryRead` 失败会伪造 EOF**：`WaitToReadAsync` 返回 true 后 `TryRead` 返回 false（多消费者竞争时可能发生）会 `return 0`（= 流结束）。单消费者现状不会触发；Phase 2 若有多读者或写侧竞争，应循环重试而不是当 EOF。
2. **同步 `Read` 会阻塞**：`Read` 是 `ReadAsync(...).GetAwaiter().GetResult()`，channel 空时同步等写侧——在 Unity 主线程上同步读会卡帧甚至死锁。约定：只在专用线程/Task 里读，主线程只做 dispatch。
3. **`Dispose` 是 no-op**：只调 `base.Dispose`，不会 `TryComplete` 也不会唤醒等待者。写侧要先 `Complete()` 再弃流，否则读者永远挂在 `WaitToReadAsync`。

---

> 汇总归位：`docs/spike/phase0-summary.md` §2.1（桥接结论）与 §5（对 Phase 2 的修订项）已同步本结论。
