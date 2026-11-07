using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class MockTransportTests
    {
        private static TransportRequest Request(string url = "http://localhost/test")
        {
            return new TransportRequest("POST", url, new TransportHeaders(), ReadOnlyMemory<byte>.Empty);
        }

        private static ReadOnlyMemory<byte> Bytes(string text)
        {
            return new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(text));
        }

        internal static async Task<string> ReadAllAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var buffer = new byte[256];
            var builder = new StringBuilder();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            return builder.ToString();
        }

        [Fact]
        public async Task Returns_Scripted_Chunks()
        {
            var transport = new MockTransport(_ => new MockResponse
            {
                Chunks = new[] { Bytes("你"), Bytes("好") }
            });

            var session = await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);
            var text = await ReadAllAsync(session.Body);

            Assert.Equal("你好", text);
        }

        [Fact]
        public async Task Error_Is_Surfaced_From_Open()
        {
            var failure = new InvalidOperationException("boom");
            var transport = new MockTransport(_ => new MockResponse { Error = failure });

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None));

            Assert.Same(failure, error);
        }

        [Fact]
        public async Task Records_Requests()
        {
            var transport = new MockTransport(_ => new MockResponse());
            var request = Request("http://localhost/recorded");

            await transport.OpenAsync(request, new TransportOptions(), CancellationToken.None);

            Assert.Single(transport.Requests);
            Assert.Equal("POST", transport.Requests[0].Method);
            Assert.Equal("http://localhost/recorded", transport.Requests[0].Url);
        }

        [Fact]
        public async Task Preserves_Status_And_Headers()
        {
            var headers = new TransportHeaders { ["content-type"] = "text/event-stream" };
            var transport = new MockTransport(_ => new MockResponse { StatusCode = 206, Headers = headers });

            var session = await transport.OpenAsync(Request(), new TransportOptions(), CancellationToken.None);

            Assert.Equal(206, session.StatusCode);
            Assert.Equal("text/event-stream", session.Headers["Content-Type"]);
        }
    }
}
