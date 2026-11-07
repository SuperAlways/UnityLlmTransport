using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class FinalizerTransportTests
    {
        private static TransportRequest Request()
        {
            return new TransportRequest("POST", "http://localhost/final", new TransportHeaders(), ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public async Task Finalizers_Run_In_Order_Before_Open()
        {
            var seen = new List<string>();
            var inner = new MockTransport(_ => new MockResponse());
            var transport = new FinalizerTransport(inner, new ITransportRequestFinalizer[]
            {
                new HeaderFinalizer("x-first", "1", seen),
                new HeaderFinalizer("x-second", "2", seen)
            });

            await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            Assert.Equal(new[] { "first", "second" }, seen);
            Assert.Equal("1", inner.Requests[0].Headers["x-first"]);
            Assert.Equal("2", inner.Requests[0].Headers["x-second"]);
        }

        [Fact]
        public async Task Empty_Finalizer_List_Still_Opens()
        {
            var inner = new MockTransport(_ => new MockResponse());
            var transport = new FinalizerTransport(inner, Array.Empty<ITransportRequestFinalizer>());

            var session = await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            Assert.Equal(200, session.StatusCode);
            Assert.Single(inner.Requests);
        }

        private sealed class HeaderFinalizer : ITransportRequestFinalizer
        {
            private readonly string _header;
            private readonly string _value;
            private readonly List<string> _seen;

            public HeaderFinalizer(string header, string value, List<string> seen)
            {
                _header = header;
                _value = value;
                _seen = seen;
            }

            public ValueTask<TransportRequest> FinalizeAsync(TransportRequest request, CancellationToken ct)
            {
                _seen.Add(_header.StartsWith("x-") ? _header.Substring(2) : _header);
                var headers = new TransportHeaders();
                foreach (var pair in request.Headers)
                {
                    headers[pair.Key] = pair.Value;
                }
                headers[_header] = _value;
                return new ValueTask<TransportRequest>(request with { Headers = headers });
            }
        }
    }
}
