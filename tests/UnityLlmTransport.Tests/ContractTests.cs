using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using UnityLlmTransport.Sse;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class ContractTests
    {
        private const string SsePayload =
            "data: 你\n\n" +
            "data: 好\n\n";

        private static TransportRequest Request()
        {
            return new TransportRequest("POST", "http://localhost/contract", new TransportHeaders(), ReadOnlyMemory<byte>.Empty);
        }

        private static MockTransport CreateMock()
        {
            var bytes = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(SsePayload));
            return new MockTransport(_ => new MockResponse
            {
                Headers = new TransportHeaders { ["content-type"] = "text/event-stream" },
                Chunks = new[] { bytes }
            });
        }

        private static IStreamingHttpTransport CreateStack(MockTransport mock)
        {
            var finalizer = new FinalizerTransport(mock, new ITransportRequestFinalizer[]
            {
                new SignatureFinalizer()
            });
            var observer = new ObserverTransport(finalizer, new NullObserver());
            var retry = new RetryTransport(observer, new RetryOptions { MaxRetries = 0 }, new NoRetryPolicy());
            var middleware = new MiddlewareTransport(retry, new IRequestMiddleware[]
            {
                new ApiKeyMiddleware()
            });
            return new TimeoutTransport(middleware);
        }

        private static async Task<List<string>> ParseAsync(Stream stream)
        {
            var items = new List<string>();
            await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            {
                items.Add(item.Data);
            }
            return items;
        }

        [Fact]
        public async Task Same_Bytes_Through_Stack_Produce_Same_Sse_Items()
        {
            var options = new TransportOptions { FirstByteTimeout = TimeSpan.FromSeconds(5) };

            var mock = CreateMock();
            var direct = await mock.OpenAsync(Request(), options, CancellationToken.None);
            var directItems = await ParseAsync(direct.Body);

            var stack = CreateStack(CreateMock());
            var piped = await stack.OpenAsync(Request(), options, CancellationToken.None);
            var pipedItems = await ParseAsync(piped.Body);

            Assert.Equal(new[] { "你", "好" }, directItems);
            Assert.Equal(directItems, pipedItems);
        }

        [Fact]
        public async Task Stack_Passes_Finalized_And_Middleware_Headers_To_Inner()
        {
            var mock = CreateMock();
            var stack = CreateStack(mock);

            await stack.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            var seen = mock.Requests[0].Headers;
            Assert.Equal("test-key", seen["x-api-key"]);
            Assert.Equal("signed", seen["x-signature"]);
        }

        private sealed class ApiKeyMiddleware : IRequestMiddleware
        {
            public ValueTask<TransportRequest> InvokeAsync(TransportRequest request, CancellationToken ct)
            {
                var headers = new TransportHeaders();
                foreach (var pair in request.Headers)
                {
                    headers[pair.Key] = pair.Value;
                }
                headers["x-api-key"] = "test-key";
                return new ValueTask<TransportRequest>(request with { Headers = headers });
            }
        }

        private sealed class SignatureFinalizer : ITransportRequestFinalizer
        {
            public ValueTask<TransportRequest> FinalizeAsync(TransportRequest request, CancellationToken ct)
            {
                var headers = new TransportHeaders();
                foreach (var pair in request.Headers)
                {
                    headers[pair.Key] = pair.Value;
                }
                headers["x-signature"] = "signed";
                return new ValueTask<TransportRequest>(request with { Headers = headers });
            }
        }

        private sealed class NullObserver : ITransportObserver
        {
            public ValueTask OnResponseAsync(TransportResponseInfo response, CancellationToken ct) => default;
            public ValueTask OnBodyChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct) => default;
        }

        private sealed class NoRetryPolicy : IRetryPolicy
        {
            public bool ShouldRetry(TransportException error, int attempt) => false;
            public TimeSpan GetDelay(TransportException error, int attempt) => TimeSpan.Zero;
        }
    }
}
