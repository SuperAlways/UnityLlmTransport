using System;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed record ApiKeyMiddlewareOptions
    {
        public string HeaderName { get; init; } = "Authorization";
        public string? Scheme { get; init; } = "Bearer";
    }

    public sealed class ApiKeyMiddleware : IRequestMiddleware
    {
        private readonly string _apiKey;
        private readonly ApiKeyMiddlewareOptions _options;

        public ApiKeyMiddleware(string apiKey, ApiKeyMiddlewareOptions? options = null)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                throw new ArgumentException("API key must not be null or empty.", nameof(apiKey));
            }
            _apiKey = apiKey;
            _options = options ?? new ApiKeyMiddlewareOptions();
            if (string.IsNullOrEmpty(_options.HeaderName))
            {
                throw new ArgumentException("HeaderName must not be null or empty.", nameof(options));
            }
        }

        public ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (!AuthHeaders.ShouldInject(request, _options.HeaderName))
            {
                return new ValueTask<TransportRequest>(request);
            }

            var value = string.IsNullOrEmpty(_options.Scheme)
                ? _apiKey
                : _options.Scheme + " " + _apiKey;
            return new ValueTask<TransportRequest>(
                AuthHeaders.WithHeader(request, _options.HeaderName, value));
        }
    }
}
