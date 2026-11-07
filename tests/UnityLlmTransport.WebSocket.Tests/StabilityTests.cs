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

        [Fact]
        public async Task Sequential_Connections_No_Leak()
        {
            for (int i = 0; i < 30; i++)
            {
                var session = await Connect("/ws/echo");
                await session.SendAsync(
                    new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hello"), true),
                    CancellationToken.None);
                await foreach (var message in session.ReceiveAsync(CancellationToken.None))
                {
                    Assert.Equal("echo:hello", Encoding.UTF8.GetString(message.Payload.ToArray()));
                    break;
                }
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task Message_Size_Boundary_Exact_And_Overflow()
        {
            var exact = await Connect("/ws/echo", new WebSocketOptions { MaxMessageBytes = 8 });
            await exact.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("abc"), true),
                CancellationToken.None);
            await foreach (var message in exact.ReceiveAsync(CancellationToken.None))
            {
                Assert.Equal("echo:abc", Encoding.UTF8.GetString(message.Payload.ToArray()));
                break;
            }
            await exact.DisposeAsync();

            var overflow = await Connect("/ws/echo", new WebSocketOptions { MaxMessageBytes = 7 });
            await overflow.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("abc"), true),
                CancellationToken.None);
            var error = await Assert.ThrowsAsync<TransportException>(async () =>
            {
                await foreach (var _ in overflow.ReceiveAsync(CancellationToken.None))
                {
                }
            });
            await overflow.DisposeAsync();
            Assert.Equal(TransportErrorKind.Overflow, error.Kind);
        }

        [Fact]
        public async Task Abort_Repeated_No_Leak()
        {
            for (int i = 0; i < 20; i++)
            {
                var session = await Connect("/ws/echo");
                session.Abort();
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task Concurrent_Sends_Under_Load()
        {
            var session = await Connect("/ws/echo");
            var sends = new Task[10];
            for (int i = 0; i < sends.Length; i++)
            {
                sends[i] = session.SendAsync(
                    new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("a"), true),
                    CancellationToken.None);
            }
            await Task.WhenAll(sends);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Close_Handshake_Repeated()
        {
            for (int i = 0; i < 20; i++)
            {
                var session = await Connect("/ws/close");
                await foreach (var message in session.ReceiveAsync(CancellationToken.None))
                {
                    Assert.Equal(WebSocketMessageType.Close, message.Type);
                    break;
                }
                await session.DisposeAsync();
            }
        }
    }
}
