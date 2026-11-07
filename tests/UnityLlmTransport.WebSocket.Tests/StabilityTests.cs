using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("ws-server")]
    public class StabilityTests
    {
        private readonly WsServerFixture _fixture;

        public StabilityTests(WsServerFixture fixture)
        {
            _fixture = fixture;
        }

        private async Task<IWebSocketSession> Connect(
            string path,
            WebSocketOptions? options = null,
            CancellationToken lifetime = default)
        {
            var transport = new ClientWebSocketTransport();
            return await transport.ConnectAsync(
                new WebSocketRequest(_fixture.Server.HttpBase + path, new TransportHeaders()),
                options ?? new WebSocketOptions(),
                lifetime);
        }

        [Fact]
        public async Task Default_MaxMessageBytes_Overflows_Huge_Message()
        {
            var session = await Connect("/ws/huge");
            var error = await Assert.ThrowsAsync<TransportException>(async () =>
            {
                await foreach (var _ in session.ReceiveAsync(CancellationToken.None))
                {
                }
            });
            await session.DisposeAsync();

            Assert.Equal(TransportErrorKind.Overflow, error.Kind);
        }
    }
}
