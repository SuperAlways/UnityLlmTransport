# 阅读笔记 · 03c · com.utilities.rest（RageAgainstThePixel）

> 参考：`refs/03c-com-utilities-rest`（commit 8941b78, v5.1.1）。只读笔记，非实现。

## 1. 包结构 / 依赖 / 许可 / 版本

- 单包 UPM 布局：`Utilities.Rest/Packages/com.utilities.rest/`（Runtime + Editor Inspector + Tests）。仓库就是一个 Unity 工程，`ProjectSettings` 齐全。
- **Unity 2021.3**（package.json:7 `"unity": "2021.3"`）——与我们目标版本一致；README.md:9 同样声明 2021.3 LTS+。
- **MIT**（LICENSE.md；包内 LICENSE.md 同）。
- 依赖（package.json:17-26）：`com.utilities.async 3.0.2`、`com.utilities.extensions 1.3.8`、5 个 `unitywebrequest*` 模块、`com.unity.nuget.newtonsoft-json 3.2.1`。**vendor 会连带拖 2 个第三方 UPM 包**。
- 代码量小而集中：核心全在 `Runtime/Rest.cs`（2139 行）单文件；其余为 `RestParameters`/`Response`/`DownloadHandlerCallback`/`DiskDownloadCache`/`ServerSentEvent`（~500 行）+ `BaseClient`（鉴权壳）。
- 血统重要：这是 **OpenAI Unity SDK（com.utilities.ai）同源的 REST 底座**，SSE 流式在 Unity 上被大规模用过。

## 2. UWR 请求组装 / 流式 / 下载器

**静态门面 + 单漏斗**：`Rest.GetAsync/PostAsync/PutAsync/PatchAsync/DeleteAsync` 每个先 `await Awaiters.UnityMainThread`（主线程保险），`using var webRequest = ...` 组装，再调统一扩展 `Rest.SendAsync`（Rest.cs:1593-2057）。所有 timeout/头/证书/dispose/进度/SSE 都在 SendAsync 一处（符合"每 API 只映射一处"）。
- POST JSON：`new UnityWebRequest(query, kHttpVerbPOST)` + `UploadHandlerRaw` + `DownloadHandlerBuffer` + `SetRequestHeader("Content-Type","application/json")`（Rest.cs:274-284）。
- 请求头统一在 SendAsync 循环 `SetRequestHeader`（Rest.cs:1621-1627），并修 multipart boundary 多余引号的 HACK（Rest.cs:1634-1644）。

**两条流式通道**：
1. **真 DownloadHandlerScript**：`DownloadHandlerCallback : DownloadHandlerScript`（DownloadHandlerCallback.cs:10），默认 512B chunk（:19），`ReceiveData` 重写（:35-60）把字节写 MemoryStream，凑满 chunk 触发 `OnDataReceived(Action<Response>)`，`CompleteContent` 收尾 flush 残余（:62-85）。由带 `dataReceivedEventCallback` 的 Get/Post 走（Rest.cs:151-175, 360-390）。⚠️ `GetText()=>null`（:33）——Body 拿不到，只有分块字节。
2. **SSE**：**不走 DownloadHandlerScript**。SendAsync 里 `Task.Run(CallbackThread)` 帧循环（每帧 `await Awaiters.UnityMainThread`，Rest.cs:1701-1774），轮询 `webRequest.downloadHandler.text`（Rest.cs:1872）增量解析：按行读（CRLF 兼容 ：1980-2006）、去 BOM（:2026-2039）、多行 data 聚合（:2041-2055）、空行=事件边界、识别 `[DONE]`（:1964-1972），入 `ConcurrentQueue`，由第二个主线程泵逐条 `await handler(...)`（Rest.cs:1776-1799）。索引越界有回退保护（:1883-1888）。

**下载器**：`DownloadFileAsync` → `DownloadHandlerFile` + `removeFileOnAbort = true`（Rest.cs:1489-1496）；贴图/音频/bundle 走专用 DownloadHandler + 磁盘缓存（DiskDownloadCache.cs，WebGL 用 NoOpDownloadCache，Rest.cs:677-683）；`StreamAudioAsync` 按字节阈值回调"可播放"（Rest.cs:1285-1309）。

## 3. timeout / certificateHandler / Abort / Dispose

- **timeout**：`if (restParams.Timeout > 0) webRequest.timeout = restParams.Timeout`（Rest.cs:1616-1619）；默认 -1=不限（RestParameters.cs:31）。可经 `Clone(timeout:)` 覆盖（Rest.cs:2094-2114）。
- **certificateHandler**：`webRequest.certificateHandler = restParams.CertificateHandler`（Rest.cs:1646），并配 `disposeCertificateHandlerOnDispose` + 另两个 handler 的 dispose 开关（Rest.cs:1647-1649）——**谁负责释放由参数表显式声明**，避免二次释放。
- **Abort/取消**：仅协作式——帧循环里 `if (cancellationToken.IsCancellationRequested) webRequest.Abort()`（Rest.cs:1762-1765）。**关键缺口：该循环只在 `Progress != null || serverSentEventHandler != null` 时才启动（Rest.cs:1701）**——裸 `Rest.GetAsync(url, token)` 无进度回调时 token 实际不会 Abort（Unity 2021.3 `SendWebRequest()` 也不收 token）；Rest.cs:1820-1822 只是事后转译异常。
- **Dispose**：全库 `using var webRequest`（Rest.cs:87,121,159,207,241,275,320,369,422,462,508,543,582,618,654,959,1126,1277,1408-1423,1489,1575）；`DownloadHandlerCallback.Dispose` 顺带 dispose MemoryStream（DownloadHandlerCallback.cs:87-91）。与 Clone(disposeX:false) 配合防止 using 抢先释放仍在用的 handler。

## 4. 对我们 UWR 传输的适用性

**可借鉴（模式级，file:line）**：
- `DownloadHandlerScript` 子类做真流式接收（DownloadHandlerCallback.cs:35-60）——UWR 侧最正宗的"每块数据回调"原语，我们 SSE 增量解析应以它为骨。
- 单一 `SendAsync` 漏斗集中 timeout/头/证书/dispose 开关（Rest.cs:1613-1649）——一次接线处处生效。
- `disposeDownloadHandlerOnDispose/disposeCertificateHandlerOnDispose` 显式释放权声明（Rest.cs:1647-1649）+ Clone 携带释放权（RestParameters.cs:53-75）。
- SSE 行解析状态机：CRLF/BOM/多行 data/`[DONE]`（Rest.cs:1870-2056）——口径可直接抄。
- `DownloadHandlerFile.removeFileOnAbort = true`（Rest.cs:1491）。
- 响应成功判据：`ConnectionError or ProtocolError && responseCode is 0 or >= 400`（Rest.cs:1860-1864）。

**不可照搬 / 坑**：
- SSE 用"整段 text 每帧重读 + 字符索引"（Rest.cs:1872, 1892 起）：DownloadHandlerBuffer 每次取 text 都拷贝全量累计串，长 LLM 流 O(n²) 内存抖动——我们应改用 DownloadHandlerScript/自维护环形缓冲。
- 裸调用不可取消（见 §3），我们 LLM 请求必须保证随时 Abort。
- 双后台泵 + `Awaiters.UnityMainThread` + 两个依赖包——在太吾 mod 的单 DLL 环境里 vendor 成本高（见主报告 03a com.utilities.async 结论）。
- `DownloadHandlerCallback.GetText()=>null`（DownloadHandlerCallback.cs:33）：用分块通道就拿不到完整 Body，需自拼。

## 5. 结论

**reference**（不 vendor / 不 wrap）。理由：MIT + Unity 2021.3 + OpenAI SDK 同源，SSE/流式/取消/证书/释放全案齐备，是 UWR 流式模式库；但依赖链、O(n²) SSE 读法与取消缺口使它不宜直接当底座——抄结构、不抄实现。

**评分：4/5**（作为 UWR 传输参考底稿价值高；扣分项恰是我们最在意的流式读法与取消可靠性）。
