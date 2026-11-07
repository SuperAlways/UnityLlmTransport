using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class ApiKeyMiddlewareTests
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
            var middleware = new ApiKeyMiddleware("secret");
            var result = await middleware.InvokeAsync(Request(), CancellationToken.None);

            Assert.Equal("Bearer secret", result.Headers["Authorization"]);
        }

        [Fact]
        public async Task Custom_Header_Without_Scheme()
        {
            var middleware = new ApiKeyMiddleware("secret", new ApiKeyMiddlewareOptions
            {
                HeaderName = "x-api-key",
                Scheme = null
            });
            var result = await middleware.InvokeAsync(Request(), CancellationToken.None);

            Assert.Equal("secret", result.Headers["x-api-key"]);
            Assert.False(result.Headers.ContainsKey("Authorization"));
        }

        [Fact]
        public async Task Caller_Header_Wins()
        {
            var headers = new TransportHeaders { ["Authorization"] = "custom" };
            var middleware = new ApiKeyMiddleware("secret");

            var result = await middleware.InvokeAsync(Request(headers), CancellationToken.None);

            Assert.Equal("custom", result.Headers["Authorization"]);
        }

        [Fact]
        public async Task Suppression_Wins()
        {
            var headers = new TransportHeaders { ["Authorization"] = null };
            var middleware = new ApiKeyMiddleware("secret");

            var result = await middleware.InvokeAsync(Request(headers), CancellationToken.None);

            Assert.True(result.Headers.ContainsKey("Authorization"));
            Assert.Null(result.Headers["Authorization"]);
        }

        [Fact]
        public async Task Copy_Does_Not_Mutate_Original()
        {
            var original = Request(new TransportHeaders { ["X-Test"] = "1" });
            var middleware = new ApiKeyMiddleware("secret");

            var result = await middleware.InvokeAsync(original, CancellationToken.None);

            Assert.False(original.Headers.ContainsKey("Authorization"));
            Assert.Equal("1", result.Headers["X-Test"]);
            Assert.Equal("Bearer secret", result.Headers["Authorization"]);
        }

        [Fact]
        public void Null_Or_Empty_Key_Throws()
        {
            Assert.Throws<ArgumentException>(() => new ApiKeyMiddleware(null!));
            Assert.Throws<ArgumentException>(() => new ApiKeyMiddleware(string.Empty));
        }

        [Fact]
        public void Null_Or_Empty_HeaderName_Throws()
        {
            Assert.Throws<ArgumentException>(() => new ApiKeyMiddleware("secret", new ApiKeyMiddlewareOptions
            {
                HeaderName = string.Empty
            }));
        }
    }
}
