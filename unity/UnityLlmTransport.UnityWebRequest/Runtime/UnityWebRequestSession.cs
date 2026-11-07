using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace UnityLlmTransport
{
    public sealed class UnityWebRequestSession : ITransportSession
    {
        private readonly UnityWebRequest _request;
        private readonly StreamingDownloadHandler _handler;
        private readonly ChunkQueue _queue;
        private readonly IUnityWebRequestRunner _runner;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _disposed;

        internal UnityWebRequestSession(
            UnityWebRequest request,
            StreamingDownloadHandler handler,
            ChunkQueue queue,
            int statusCode,
            TransportHeaders headers,
            bool decompress,
            IUnityWebRequestRunner runner,
            CancellationToken lifetimeToken)
        {
            _request = request;
            _handler = handler;
            _queue = queue;
            _runner = runner;
            StatusCode = statusCode;
            Headers = headers;

            Stream body = new ChunkQueueReadStream(queue);
            if (decompress && IsGzip(headers))
            {
                // Mono 的 GZipStream 内部只走同步 Read；UWR 又可能已自动解压 gzip（头仍标 gzip）。
                // 同步队列视图 + 按 magic 嗅探 + 线程池 offload：Body 保持异步只读契约，调用线程永不阻塞。
                body = new AsyncOffloadStream(
                    new GzipSniffReadStream(new SyncQueueReadStream(queue)));
            }
            Body = body;

            _cancellationRegistration = lifetimeToken.Register(AbortFromCancellation);
        }

        public int StatusCode { get; }
        public TransportHeaders Headers { get; }
        public Stream Body { get; }

        public void Abort()
        {
            AbortWith(TransportErrorKind.Aborted);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                AbortWith(TransportErrorKind.Aborted);
                _cancellationRegistration.Dispose();
                _runner.Post(() => _request.Dispose());
            }
            return default;
        }

        private void AbortFromCancellation()
        {
            AbortWith(TransportErrorKind.Cancelled);
        }

        private void AbortWith(TransportErrorKind kind)
        {
            _handler.MarkAborted(kind);
            _queue.Abort(new TransportException(kind, "Session aborted (" + kind + ")."));
            _runner.Post(() => _request.Abort());
        }

        private static bool IsGzip(TransportHeaders headers)
        {
            return headers.TryGetValue("Content-Encoding", out var value) &&
                   value != null &&
                   value.IndexOf("gzip", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
