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
                if (plan.RedirectLimit == 0 && snapshot.StatusCode >= 300 && snapshot.StatusCode < 400)
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

                return new UnityWebRequestSession(
                    uwr,
                    built.Handler,
                    queue,
                    snapshot.StatusCode,
                    snapshot.Headers,
                    options.Decompress,
                    _runner,
                    cancellationToken);
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

        private sealed record BuiltRequest(UnityWebRequest Request, StreamingDownloadHandler Handler);
    }
}
