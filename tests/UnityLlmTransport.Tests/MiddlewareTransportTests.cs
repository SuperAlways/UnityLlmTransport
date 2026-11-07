using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class MiddlewareTransportTests
    {
        private static TransportRequest Request()
        {
            return new TransportRequest("POST", "http://localhost/mw", new TransportHeaders(), ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public async Task Applies_Middlewares_In_Order()
        {
            var seen = new List<string>();
            var inner = new MockTransport(request => new MockResponse());
            var first = new RecordingMiddleware("first", seen);
            var second = new RecordingMiddleware("second", seen);
            var transport = new MiddlewareTransport(inner, new IRequestMiddleware[] { first, second });

            await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            Assert.Equal(new[] { "first", "second" }, seen);
            Assert.Equal("first,second", inner.Requests[0].Headers["x-order"]);
        }

        [Fact]
        public async Task Later_Middleware_Sees_Earlier_Changes()
        {
            var inner = new MockTransport(_ => new MockResponse());
            var transport = new MiddlewareTransport(inner, new IRequestMiddleware[]
            {
                new HeaderMiddleware("x-a", "1"),
                new HeaderMiddleware("x-b", "2")
            });

            await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            Assert.Equal("1", inner.Requests[0].Headers["x-a"]);
            Assert.Equal("2", inner.Requests[0].Headers["x-b"]);
        }

        [Fact]
        public async Task Middleware_Exception_Propagates_Without_Opening()
        {
            var inner = new MockTransport(_ => new MockResponse());
            var transport = new MiddlewareTransport(inner, new IRequestMiddleware[]
            {
                new ThrowingMiddleware()
            });

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None));

            Assert.Empty(inner.Requests);
        }

        private sealed class RecordingMiddleware : IRequestMiddleware
        {
            private readonly string _name;
            private readonly List<string> _seen;

            public RecordingMiddleware(string name, List<string> seen)
            {
                _name = name;
                _seen = seen;
            }

            public ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct)
            {
                _seen.Add(_name);
                var headers = new TransportHeaders();
                string? existing = request.Headers.TryGetValue("x-order", out var value) ? value : null;
                headers["x-order"] = existing == null ? _name : existing + "," + _name;
                return new ValueTask<TransportRequest>(request with { Headers = headers });
            }
        }

        private sealed class HeaderMiddleware : IRequestMiddleware
        {
            private readonly string _name;
            private readonly string _value;

            public HeaderMiddleware(string name, string value)
            {
                _name = name;
                _value = value;
            }

            public ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct)
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

        private sealed class ThrowingMiddleware : IRequestMiddleware
        {
            public ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct)
            {
                throw new InvalidOperationException("middleware failed");
            }
        }
    }
}
