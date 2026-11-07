using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    /// <summary>守卫连接后的首字节、流空闲与响应体大小；超时/超限即 Abort 内层会话。</summary>
    public sealed class TimeoutTransport : IStreamingHttpTransport
    {
        private readonly IStreamingHttpTransport _inner;

        public TimeoutTransport(IStreamingHttpTransport inner)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        }

        public TransportCapabilities Capabilities => _inner.Capabilities;

        public async Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
            CancellationToken cancellationToken)
        {
            var session = await _inner.OpenAsync(request, options, cancellationToken).ConfigureAwait(false);
            return new TimedSession(session, options);
        }

        private sealed class TimedSession : ITransportSession
        {
            private readonly ITransportSession _inner;
            private readonly bool _hasFirstByteBudget;
            private readonly DateTimeOffset _firstByteDeadline;
            private readonly TimeSpan _idleTimeout;
            private readonly int _maxResponseBytes;
            private readonly TimedBodyStream _body;
            private long _receivedBytes;

            public TimedSession(ITransportSession inner, TransportOptions options)
            {
                _inner = inner;
                _hasFirstByteBudget = IsActiveBudget(options.FirstByteTimeout);
                _firstByteDeadline = _hasFirstByteBudget
                    ? DateTimeOffset.UtcNow + options.FirstByteTimeout!.Value
                    : DateTimeOffset.MaxValue;
                _idleTimeout = IsActiveBudget(options.IdleTimeout) ? options.IdleTimeout!.Value : Timeout.InfiniteTimeSpan;
                _maxResponseBytes = options.MaxResponseBytes ?? -1;
                _body = new TimedBodyStream(this, inner.Body);
            }

            /// <summary>哨兵值（Timeout.InfiniteTimeSpan=-1ms）与其它非正值不构成真实预算，视为未配置。</summary>
            private static bool IsActiveBudget(TimeSpan? value)
            {
                return value.HasValue && value.Value > TimeSpan.Zero;
            }

            public int StatusCode => _inner.StatusCode;
            public TransportHeaders Headers => _inner.Headers;
            public Stream Body => _body;
            public bool FirstByteReceived { get; private set; }

            public void Abort() => _inner.Abort();

            public ValueTask DisposeAsync() => _inner.DisposeAsync();

            public TimeSpan GetReadBudget()
            {
                if (!FirstByteReceived)
                {
                    if (!_hasFirstByteBudget)
                    {
                        return Timeout.InfiniteTimeSpan;
                    }
                    var remaining = _firstByteDeadline - DateTimeOffset.UtcNow;
                    return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
                }
                return _idleTimeout;
            }

            public void MarkFirstByte()
            {
                FirstByteReceived = true;
            }

            public void AddReceivedBytes(int count)
            {
                _receivedBytes += count;
                if (_maxResponseBytes >= 0 && _receivedBytes > _maxResponseBytes)
                {
                    _inner.Abort();
                    throw new TransportException(
                        TransportErrorKind.Overflow,
                        "Response exceeded MaxResponseBytes (" + _maxResponseBytes + ").");
                }
            }

            public void AbortForTimeout(TransportErrorKind kind)
            {
                _inner.Abort();
            }
        }

        private sealed class TimedBodyStream : Stream
        {
            private readonly TimedSession _session;
            private readonly Stream _inner;

            public TimedBodyStream(TimedSession session, Stream inner)
            {
                _session = session;
                _inner = inner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override void Flush() { }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                var budget = _session.GetReadBudget();
                if (budget == TimeSpan.Zero)
                {
                    var kindBeforeRead = _session.FirstByteReceived
                        ? TransportErrorKind.IdleTimeout
                        : TransportErrorKind.FirstByteTimeout;
                    _session.AbortForTimeout(kindBeforeRead);
                    throw new TransportException(kindBeforeRead, "Read timed out.");
                }

                CancellationTokenSource? timeoutCts = null;
                CancellationTokenSource? linkedCts = null;
                CancellationTokenRegistration timeoutRegistration = default;
                try
                {
                    if (budget != Timeout.InfiniteTimeSpan)
                    {
                        timeoutCts = new CancellationTokenSource(budget);
                        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                        // Unity Mono 的 HttpClient（WebRequest 栈）读中不观察取消令牌：
                        // 超时必须主动 Abort 内层会话解堵读，否则读会拖到数据到来才返回。
                        timeoutRegistration = timeoutCts.Token.Register(
                            state =>
                            {
                                var timed = (TimedSession)state;
                                timed.AbortForTimeout(timed.FirstByteReceived
                                    ? TransportErrorKind.IdleTimeout
                                    : TransportErrorKind.FirstByteTimeout);
                            },
                            _session);
                    }

                    var token = linkedCts != null ? linkedCts.Token : cancellationToken;
                    int read;
                    try
                    {
                        read = await _inner.ReadAsync(buffer, token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (
                        !cancellationToken.IsCancellationRequested &&
                        timeoutCts != null &&
                        timeoutCts.IsCancellationRequested)
                    {
                        var kind = _session.FirstByteReceived
                            ? TransportErrorKind.IdleTimeout
                            : TransportErrorKind.FirstByteTimeout;
                        _session.AbortForTimeout(kind);
                        throw new TransportException(kind, "Read timed out.", ex);
                    }

                    if (read == 0)
                    {
                        return 0;
                    }

                    if (!_session.FirstByteReceived)
                    {
                        _session.MarkFirstByte();
                    }

                    _session.AddReceivedBytes(read);
                    return read;
                }
                finally
                {
                    if (timeoutRegistration != default)
                    {
                        timeoutRegistration.Dispose();
                    }
                    if (linkedCts != null)
                    {
                        linkedCts.Dispose();
                    }
                    if (timeoutCts != null)
                    {
                        timeoutCts.Dispose();
                    }
                }
            }
        }
    }
}
