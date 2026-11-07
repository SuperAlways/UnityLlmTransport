using System;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class OAuthTokenMiddlewareTests
    {
        private static TransportRequest Request(TransportHeaders? headers = null)
        {
            return new TransportRequest(
                "GET",
                "http://localhost/test",
                headers ?? new TransportHeaders(),
                ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public async Task Default_Header_And_Bearer()
        {
            var middleware = new OAuthTokenMiddleware(
                ct => new ValueTask<string>("token"));
            var result = await middleware.InvokeAsync(Request(), CancellationToken.None);

            Assert.Equal("Bearer token", result.Headers["Authorization"]);
        }

        [Fact]
        public async Task Custom_Header_And_Scheme()
        {
            var middleware = new OAuthTokenMiddleware(
                ct => new ValueTask<string>("token"),
                new OAuthTokenMiddlewareOptions { HeaderName = "X-Token", Scheme = "Token" });
            var result = await middleware.InvokeAsync(Request(), CancellationToken.None);

            Assert.Equal("Token token", result.Headers["X-Token"]);
            Assert.False(result.Headers.ContainsKey("Authorization"));
        }

        [Fact]
        public async Task Caller_Header_Skips_Provider()
        {
            int calls = 0;
            var middleware = new OAuthTokenMiddleware(ct =>
            {
                calls++;
                return new ValueTask<string>("token");
            });
            var headers = new TransportHeaders { ["Authorization"] = "custom" };

            var result = await middleware.InvokeAsync(Request(headers), CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.Equal("custom", result.Headers["Authorization"]);
        }

        [Fact]
        public async Task Suppression_Skips_Provider()
        {
            int calls = 0;
            var middleware = new OAuthTokenMiddleware(ct =>
            {
                calls++;
                return new ValueTask<string>("token");
            });
            var headers = new TransportHeaders { ["Authorization"] = null };

            var result = await middleware.InvokeAsync(Request(headers), CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.True(result.Headers.ContainsKey("Authorization"));
            Assert.Null(result.Headers["Authorization"]);
        }

        [Fact]
        public async Task Empty_Token_Is_Protocol_And_Does_Not_Touch_Request()
        {
            var middleware = new OAuthTokenMiddleware(ct => new ValueTask<string>(string.Empty));
            var original = Request();

            var error = await Assert.ThrowsAsync<TransportException>(
                async () => await middleware.InvokeAsync(original, CancellationToken.None));

            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
            Assert.False(original.Headers.ContainsKey("Authorization"));
        }

        [Fact]
        public async Task Provider_Exception_Propagates()
        {
            var failure = new InvalidOperationException("provider down");
            var middleware = new OAuthTokenMiddleware(ct => throw failure);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await middleware.InvokeAsync(Request(), CancellationToken.None));

            Assert.Same(failure, error);
        }

        [Fact]
        public void Null_Provider_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => new OAuthTokenMiddleware(null!));
        }
    }
}
