using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phase3TestSupport;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class AuthRetryPipelineTests
    {
        private static TransportRequest Request(string url = "http://localhost/test")
        {
            return new TransportRequest(
                "GET",
                url,
                new TransportHeaders(),
                ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public async Task Retry_ReRuns_Middleware_With_New_Token()
        {
            string token = "old";
            int attempts = 0;
            int refreshes = 0;

            var oauth = new OAuthTokenMiddleware(ct => new ValueTask<string>(token));
            var mock = new MockTransport(_ =>
            {
                attempts++;
                return attempts == 1
                    ? new MockResponse { StatusCode = 401 }
                    : new MockResponse { StatusCode = 200 };
            });
            var middleware = new MiddlewareTransport(mock, new IRequestMiddleware[] { oauth });
            var retry = new RetryTransport(
                middleware,
                new RetryOptions { MaxRetries = 1 },
                new ZeroDelayPolicy(),
                new DelegateRefreshHandler(() =>
                {
                    refreshes++;
                    token = "new";
                }));

            var session = await retry.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(2, attempts);
            Assert.Equal(1, refreshes);
            Assert.Equal("Bearer old", mock.Requests[0].Headers["Authorization"]);
            Assert.Equal("Bearer new", mock.Requests[1].Headers["Authorization"]);
        }

        [Fact]
        public async Task Refresh_Only_Once()
        {
            int attempts = 0;
            int refreshes = 0;
            var oauth = new OAuthTokenMiddleware(ct => new ValueTask<string>("token"));
            var mock = new MockTransport(_ =>
            {
                attempts++;
                return new MockResponse { StatusCode = 401 };
            });
            var middleware = new MiddlewareTransport(mock, new IRequestMiddleware[] { oauth });
            var retry = new RetryTransport(
                middleware,
                new RetryOptions { MaxRetries = 2 },
                new ZeroDelayPolicy(),
                new DelegateRefreshHandler(() => refreshes++));

            await Assert.ThrowsAsync<TransportException>(
                () => retry.OpenAsync(Request(), new TransportOptions(), CancellationToken.None));

            Assert.Equal(2, attempts);
            Assert.Equal(1, refreshes);
        }

        [Fact]
        public async Task HttpClient_Sees_Auth_And_Signature_Headers()
        {
            TransportRequirements.RequireRequestFinalizer(new HttpClientTransport().Capabilities);
            await using var server = Phase3TestServer.Start(5261, 5293, 5294);
            var oauth = new OAuthTokenMiddleware(ct => new ValueTask<string>("abc"));
            var finalizer = new FinalizerTransport(
                new HttpClientTransport(),
                new ITransportRequestFinalizer[]
                {
                    new SignerFinalizer(new HeaderSigner("x-signature", "signed"))
                });
            var middleware = new MiddlewareTransport(finalizer, new IRequestMiddleware[] { oauth });

            var session = await middleware.OpenAsync(
                Request(server.HttpBase + "/echo-headers"),
                new TransportOptions(),
                CancellationToken.None);
            var text = await ReadAllTextAsync(session.Body, CancellationToken.None);
            await session.DisposeAsync();

            using var json = JsonDocument.Parse(text);
            Assert.Equal("Bearer abc", json.RootElement.GetProperty("Authorization").GetString());
            Assert.Equal("signed", json.RootElement.GetProperty("x-signature").GetString());
        }

        [Fact]
        public async Task Repeated_Requests_Keep_Auth_Headers()
        {
            var oauth = new OAuthTokenMiddleware(ct => new ValueTask<string>("abc"));
            var mock = new MockTransport(_ => new MockResponse());
            var middleware = new MiddlewareTransport(mock, new IRequestMiddleware[] { oauth });

            var first = await middleware.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);
            await first.DisposeAsync();
            var second = await middleware.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);
            await second.DisposeAsync();

            Assert.Equal(2, mock.Requests.Count);
            Assert.Equal("Bearer abc", mock.Requests[0].Headers["Authorization"]);
            Assert.Equal("Bearer abc", mock.Requests[1].Headers["Authorization"]);
        }

        [Fact]
        public void ApiKey_Error_Does_Not_Leak_Secret()
        {
            const string secret = "super-secret";
            var error = Assert.Throws<ArgumentException>(() =>
                new ApiKeyMiddleware(secret, new ApiKeyMiddlewareOptions { HeaderName = string.Empty }));

            Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        }

        private static async Task<string> ReadAllTextAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[256];
            var builder = new StringBuilder();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            return builder.ToString();
        }

        private sealed class DelegateRefreshHandler : IAuthRefreshHandler
        {
            private readonly Action _onRefresh;

            public DelegateRefreshHandler(Action onRefresh)
            {
                _onRefresh = onRefresh;
            }

            public Task<bool> RefreshAsync(CancellationToken ct)
            {
                _onRefresh();
                return Task.FromResult(true);
            }
        }

        private sealed class ZeroDelayPolicy : IRetryPolicy
        {
            public bool ShouldRetry(TransportException error, int attempt)
            {
                return error.IsRetryable;
            }

            public TimeSpan GetDelay(TransportException error, int attempt)
            {
                return TimeSpan.Zero;
            }
        }

        private sealed class HeaderSigner : IRequestSigner
        {
            private readonly string _name;
            private readonly string _value;

            public HeaderSigner(string name, string value)
            {
                _name = name;
                _value = value;
            }

            public ValueTask<TransportRequest> SignAsync(TransportRequest request, CancellationToken ct)
            {
                var headers = new TransportHeaders();
                foreach (var pair in request.Headers)
                {
                    headers[pair.Key] = pair.Value;
                }
                headers[_name] = _value;
                return new ValueTask<TransportRequest>(request with { Headers = headers });
            }
        }
    }
}
