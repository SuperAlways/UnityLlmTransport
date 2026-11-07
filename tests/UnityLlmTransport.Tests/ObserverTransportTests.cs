using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class ObserverTransportTests
    {
        private static TransportRequest Request()
        {
            return new TransportRequest("GET", "http://localhost/obs", new TransportHeaders(), ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public async Task OnResponse_Fires_With_Status_And_Headers()
        {
            var observer = new RecordingObserver();
            var headers = new TransportHeaders { ["content-type"] = "text/event-stream" };
            var inner = new MockTransport(_ => new MockResponse { StatusCode = 202, Headers = headers });
            var transport = new ObserverTransport(inner, observer);

            await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            Assert.Equal(1, observer.ResponseCount);
            Assert.Equal(202, observer.LastResponse!.StatusCode);
            Assert.Equal("text/event-stream", observer.LastResponse.Headers["Content-Type"]);
        }

        [Fact]
        public async Task Body_Chunks_Are_Observed_Unchanged()
        {
            var observer = new RecordingObserver();
            var inner = new MockTransport(_ => new MockResponse
            {
                Chunks = new[]
                {
                    new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("你")),
                    new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("好"))
                }
            });
            var transport = new ObserverTransport(inner, observer);

            var session = await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);
            var text = await MockTransportTests.ReadAllAsync(session.Body);

            Assert.Equal("你好", text);
            Assert.Equal(2, observer.Chunks.Count);
            Assert.Equal("你", Encoding.UTF8.GetString(observer.Chunks[0]));
            Assert.Equal("好", Encoding.UTF8.GetString(observer.Chunks[1]));
        }

        [Fact]
        public async Task OnResponse_Not_Fired_When_Open_Fails()
        {
            var observer = new RecordingObserver();
            var inner = new MockTransport(_ => new MockResponse { Error = new InvalidOperationException("no connect") });
            var transport = new ObserverTransport(inner, observer);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None));

            Assert.Equal(0, observer.ResponseCount);
        }

        private sealed class RecordingObserver : ITransportObserver
        {
            public int ResponseCount { get; private set; }
            public TransportResponseInfo? LastResponse { get; private set; }
            public List<byte[]> Chunks { get; } = new List<byte[]>();

            public ValueTask OnResponseAsync(TransportResponseInfo response, CancellationToken ct)
            {
                ResponseCount++;
                LastResponse = response;
                return default;
            }

            public ValueTask OnBodyChunkAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct)
            {
                Chunks.Add(chunk.ToArray());
                return default;
            }
        }
    }
}
