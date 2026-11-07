using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class StabilityTests
    {
        private static TransportRequest Request()
        {
            return new TransportRequest(
                "GET",
                "http://localhost/test",
                new TransportHeaders(),
                ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public async Task Retry_Refresh_Repeated()
        {
            for (int iteration = 0; iteration < 20; iteration++)
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
                    new ActionRefreshHandler(() =>
                    {
                        refreshes++;
                        token = "new";
                    }));

                var session = await retry.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);
                await session.DisposeAsync();

                Assert.Equal(2, attempts);
                Assert.Equal(1, refreshes);
                Assert.Equal("Bearer new", mock.Requests[1].Headers["Authorization"]);
            }
        }

        [Fact]
        public async Task Middleware_Many_Requests()
        {
            int calls = 0;
            var middleware = new OAuthTokenMiddleware(ct =>
            {
                calls++;
                return new ValueTask<string>("token");
            });

            for (int i = 0; i < 50; i++)
            {
                var result = await middleware.InvokeAsync(Request(), CancellationToken.None);
                Assert.Equal("Bearer token", result.Headers["Authorization"]);
            }
            Assert.Equal(50, calls);
        }

        private sealed class ActionRefreshHandler : IAuthRefreshHandler
        {
            private readonly Action _action;

            public ActionRefreshHandler(Action action)
            {
                _action = action;
            }

            public Task<bool> RefreshAsync(CancellationToken ct)
            {
                _action();
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
    }
}
