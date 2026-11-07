using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace UnityLlmTransport
{
    public sealed class UnityWebRequestTransport : IStreamingHttpTransport
    {
        private readonly IUnityWebRequestRunner _runner;
        private readonly UnityWebRequestTransportOptions _options;

        public UnityWebRequestTransport(
            IUnityWebRequestRunner runner,
            UnityWebRequestTransportOptions options = null)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _options = options ?? new UnityWebRequestTransportOptions();
        }

        public TransportCapabilities Capabilities =>
            TransportCapabilities.StreamingBody |
            TransportCapabilities.SystemProxy |
            TransportCapabilities.TlsCertificateHandler |
            TransportCapabilities.Compression;

        public async Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
            CancellationToken cancellationToken)
        {
            var plan = UwrRequestPlanner.Create(request, options);
            var queue = new ChunkQueue(_options.MaxBufferedBytes);
            UnityWebRequest requestToDispose = null;

            try
            {
                var built = await _runner.InvokeAsync(
                    () => BuildRequest(request, options, plan, queue),
                    cancellationToken).ConfigureAwait(false);
                var uwr = built.Request;
                requestToDispose = uwr;

                UwrResponseSnapshot snapshot = await StartWithFirstByteTimeoutAsync(
                    uwr, options, cancellationToken, queue).ConfigureAwait(false);

                TransportException failure;
                if (cancellationToken.IsCancellationRequested || snapshot.Completion == UwrCompletion.Aborted)
                {
                    failure = UwrResponseClassifier.Classify(snapshot, true);
                }
                else if (plan.RedirectLimit == 0 && snapshot.StatusCode >= 300 && snapshot.StatusCode < 400)
                {
                    failure = new TransportException(
                        TransportErrorKind.Protocol,
                        "Redirect not allowed by RedirectPolicy.Error.",
                        null,
                        snapshot.StatusCode,
                        snapshot.Headers,
                        snapshot.Error,
                        false);
                }
                else
                {
                    failure = UwrResponseClassifier.Classify(snapshot, cancellationToken.IsCancellationRequested);
                }

                if (failure != null)
                {
                    queue.Abort(failure);
                    var disposeThis = uwr;
                    requestToDispose = null;
                    _runner.Post(() =>
                    {
                        disposeThis.Abort();
                        disposeThis.Dispose();
                    });
                    throw failure;
                }

                var session = new UnityWebRequestSession(
                    uwr,
                    built.Handler,
                    queue,
                    snapshot.StatusCode,
                    snapshot.Headers,
                    options.Decompress,
                    _runner,
                    cancellationToken);
                _ = WatchCompletionAsync(uwr, built.Handler, queue);
                return session;
            }
            catch
            {
                if (requestToDispose != null)
                {
                    var disposeThis = requestToDispose;
                    _runner.Post(() =>
                    {
                        disposeThis.Abort();
                        disposeThis.Dispose();
                    });
                }
                throw;
            }
        }

        private BuiltRequest BuildRequest(
            TransportRequest request,
            TransportOptions options,
            UwrRequestPlan plan,
            ChunkQueue queue)
        {
            var uwr = new UnityWebRequest(request.Url, request.Method);
            if (request.Body.Length > 0)
            {
                uwr.uploadHandler = new UploadHandlerRaw(request.Body.ToArray());
            }

            var handler = new StreamingDownloadHandler(queue, _options.DownloadBufferSize);
            uwr.downloadHandler = handler;
            uwr.timeout = options.ConnectTimeout.HasValue && options.ConnectTimeout.Value > TimeSpan.Zero
                ? (int)Math.Ceiling(options.ConnectTimeout.Value.TotalSeconds)
                : 0;
            uwr.redirectLimit = plan.RedirectLimit;
            if (plan.AcceptAllCertificates)
            {
                uwr.certificateHandler = new AcceptAllCertificateHandler();
            }
            foreach (var pair in plan.Headers)
            {
                uwr.SetRequestHeader(pair.Key, pair.Value);
            }

            return new BuiltRequest(uwr, handler);
        }

        private async Task<UwrResponseSnapshot> StartWithFirstByteTimeoutAsync(
            UnityWebRequest uwr,
            TransportOptions options,
            CancellationToken cancellationToken,
            ChunkQueue queue)
        {
            CancellationTokenSource timeoutCts = null;
            CancellationTokenSource linkedCts = null;
            try
            {
                CancellationToken token = cancellationToken;
                var firstByteTimeout = options.FirstByteTimeout;
                if (firstByteTimeout.HasValue && firstByteTimeout.Value > TimeSpan.Zero)
                {
                    timeoutCts = new CancellationTokenSource(firstByteTimeout.Value);
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    token = linkedCts.Token;
                }

                var snapshot = await _runner.StartAsync(uwr, token).ConfigureAwait(false);
                if (timeoutCts != null && timeoutCts.IsCancellationRequested && snapshot.Completion == UwrCompletion.Aborted)
                {
                    var timeoutError = new TransportException(TransportErrorKind.FirstByteTimeout, "First byte timeout.");
                    queue.Abort(timeoutError);
                    throw timeoutError;
                }
                return snapshot;
            }
            finally
            {
                linkedCts?.Dispose();
                timeoutCts?.Dispose();
            }
        }

        private async Task WatchCompletionAsync(UnityWebRequest request, StreamingDownloadHandler handler, ChunkQueue queue)
        {
            UwrResponseSnapshot snapshot;
            try
            {
                snapshot = await _runner.WaitForCompletionAsync(request).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (queue.IsTerminated)
            {
                return;
            }

            if (snapshot.Completion == UwrCompletion.Success)
            {
                queue.Complete();
                return;
            }

            var error = UwrResponseClassifier.Classify(snapshot, snapshot.Completion == UwrCompletion.Aborted);
            if (error == null)
            {
                if (snapshot.Completion == UwrCompletion.ProtocolError)
                {
                    // 已交付的非 2xx（协议错误 + 有状态码）：响应完整、body 可读，按干净收尾交付，
                    // 不得用伪 Protocol 中止把已缓冲的错误 body 清掉。
                    queue.Complete();
                    return;
                }
                error = new TransportException(
                    TransportErrorKind.Protocol,
                    "Stream ended before completion.",
                    null,
                    snapshot.StatusCode,
                    snapshot.Headers,
                    snapshot.Error,
                    false);
            }
            // 先用分类错误终止队列（first-wins），MarkAborted 自己的泛型 abort 随后 no-op，
            // 但其抑制旗仍会装上——否则状态/头/UWR 错误会被 "Response aborted (Kind)" 泛化信息顶掉。
            queue.Abort(error);
            handler.MarkAborted(error.Kind);
        }

        private sealed record BuiltRequest(UnityWebRequest Request, StreamingDownloadHandler Handler);
    }
}
