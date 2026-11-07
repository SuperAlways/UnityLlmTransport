using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed record OAuthTokenMiddlewareOptions
    {
        public string HeaderName { get; init; } = "Authorization";
        public string Scheme { get; init; } = "Bearer";
    }

    public sealed class OAuthTokenMiddleware : IRequestMiddleware
    {
        private readonly Func<CancellationToken, ValueTask<string>> _tokenProvider;
        private readonly OAuthTokenMiddlewareOptions _options;

        public OAuthTokenMiddleware(
            Func<CancellationToken, ValueTask<string>> tokenProvider,
            OAuthTokenMiddlewareOptions? options = null)
        {
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
            _options = options ?? new OAuthTokenMiddlewareOptions();
            if (string.IsNullOrEmpty(_options.HeaderName))
            {
                throw new ArgumentException("HeaderName must not be null or empty.", nameof(options));
            }
        }

        public async ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (!AuthHeaders.ShouldInject(request, _options.HeaderName))
            {
                return request;
            }

            string token = await _tokenProvider(ct).ConfigureAwait(false);
            if (string.IsNullOrEmpty(token))
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "OAuth token provider returned an empty token.");
            }

            var value = string.IsNullOrEmpty(_options.Scheme)
                ? token
                : _options.Scheme + " " + token;
            return AuthHeaders.WithHeader(request, _options.HeaderName, value);
        }
    }
}
