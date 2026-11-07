using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed record RetryOptions
    {
        public int MaxRetries { get; init; } = 2;
        public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(60);
        public TimeSpan BaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);
    }

    public interface IRetryPolicy
    {
        bool ShouldRetry(TransportException error, int attempt);
        TimeSpan GetDelay(TransportException error, int attempt);
    }

    public interface IAuthRefreshHandler
    {
        Task<bool> RefreshAsync(CancellationToken ct);
    }

    /// <summary>仅在任何响应体字节交给调用者之前重试：失败必须发生在 OpenAsync 之内（连接错误或状态码分类），会话一旦返回调用者即不再重试。</summary>
    public sealed class RetryTransport : IStreamingHttpTransport
    {
        private readonly IStreamingHttpTransport _inner;
        private readonly RetryOptions _options;
        private readonly IRetryPolicy _policy;
        private readonly IAuthRefreshHandler? _authRefresh;

        public RetryTransport(
            IStreamingHttpTransport inner,
            RetryOptions? options = null,
            IRetryPolicy? policy = null,
            IAuthRefreshHandler? authRefresh = null)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _options = options ?? new RetryOptions();
            _policy = policy ?? new ExponentialBackoffRetryPolicy(_options);
            _authRefresh = authRefresh;
        }

        public TransportCapabilities Capabilities => _inner.Capabilities;

        public async Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
            CancellationToken cancellationToken)
        {
            int retryCount = 0;
            bool refreshed = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var session = await _inner.OpenAsync(request, options, cancellationToken).ConfigureAwait(false);
                    var failure = ClassifyStatus(session);
                    if (failure == null)
                    {
                        return session;
                    }

                    await session.DisposeAsync().ConfigureAwait(false);
                    throw failure;
                }
                catch (TransportException error)
                {
                    if (error.Kind == TransportErrorKind.Cancelled || error.Kind == TransportErrorKind.Aborted)
                    {
                        throw;
                    }

                    if (error.StatusCode == 401 && _authRefresh != null && !refreshed)
                    {
                        refreshed = true;
                        if (await _authRefresh.RefreshAsync(cancellationToken).ConfigureAwait(false))
                        {
                            continue;
                        }
                        throw;
                    }

                    if (retryCount >= _options.MaxRetries || !_policy.ShouldRetry(error, retryCount))
                    {
                        throw;
                    }

                    var delay = _policy.GetDelay(error, retryCount);
                    if (delay > _options.MaxRetryDelay)
                    {
                        throw;
                    }

                    retryCount++;
                    if (delay > TimeSpan.Zero)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private static TransportException? ClassifyStatus(ITransportSession session)
        {
            int status = session.StatusCode;
            if (status >= 200 && status < 300)
            {
                return null;
            }

            bool retryable = status == 408 || status == 409 || status == 429 || status >= 500;
            var headers = session.Headers;
            if (headers.TryGetValue("x-should-retry", out var value) && value != null)
            {
                if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    retryable = false;
                }
                else if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    retryable = true;
                }
            }

            return new TransportException(
                TransportErrorKind.HttpStatus,
                "HTTP " + status,
                null,
                status,
                headers,
                null,
                retryable);
        }

        private sealed class ExponentialBackoffRetryPolicy : IRetryPolicy
        {
            private readonly RetryOptions _options;
            private readonly Random _random = new Random();

            public ExponentialBackoffRetryPolicy(RetryOptions options)
            {
                _options = options;
            }

            public bool ShouldRetry(TransportException error, int attempt)
            {
                if (error.Headers != null && error.Headers.TryGetValue("x-should-retry", out var value) && value != null)
                {
                    if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                    if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                return error.IsRetryable;
            }

            public TimeSpan GetDelay(TransportException error, int attempt)
            {
                var retryAfter = ParseRetryAfter(error.Headers);
                if (retryAfter.HasValue)
                {
                    return retryAfter.Value;
                }

                var exponential = _options.BaseDelay.TotalMilliseconds * Math.Pow(2, attempt);
                var jitter = _random.NextDouble() * _options.BaseDelay.TotalMilliseconds;
                return TimeSpan.FromMilliseconds(exponential + jitter);
            }

            private static TimeSpan? ParseRetryAfter(TransportHeaders? headers)
            {
                if (headers == null)
                {
                    return null;
                }

                if (headers.TryGetValue("retry-after-ms", out var millisecondsText) &&
                    millisecondsText != null &&
                    int.TryParse(millisecondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds))
                {
                    return TimeSpan.FromMilliseconds(milliseconds);
                }

                if (headers.TryGetValue("Retry-After", out var secondsText) && secondsText != null)
                {
                    if (int.TryParse(secondsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                    {
                        return TimeSpan.FromSeconds(seconds);
                    }

                    if (DateTimeOffset.TryParse(
                        secondsText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal,
                        out var absolute))
                    {
                        var delay = absolute - DateTimeOffset.UtcNow;
                        return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
                    }
                }

                return null;
            }
        }
    }
}
