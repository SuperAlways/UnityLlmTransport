using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Net.WebSockets;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class WebSocketMockTests
    {
        [Fact]
        public async Task Connect_Returns_Open_Session()
        {
            var transport = new MockWebSocketTransport();

            var session = await transport.ConnectAsync(
                new WebSocketRequest("ws://localhost/ws", new TransportHeaders()),
                new WebSocketOptions(),
                CancellationToken.None);

            Assert.Equal(WebSocketState.Open, session.State);
            Assert.Single(transport.Requests);
        }

        [Fact]
        public async Task Send_Is_Recorded()
        {
            var transport = new MockWebSocketTransport();
            var session = await transport.ConnectAsync(
                new WebSocketRequest("ws://localhost/ws", new TransportHeaders()),
                new WebSocketOptions(),
                CancellationToken.None);

            var message = new WebSocketMessage(
                WebSocketMessageType.Text,
                new System.ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("hi")),
                true);
            await session.SendAsync(message, CancellationToken.None);

            Assert.Single(transport.Sent);
            Assert.Equal(WebSocketMessageType.Text, transport.Sent[0].Type);
        }

        [Fact]
        public async Task Receive_Yields_Scripted_Messages()
        {
            var scripted = new[]
            {
                new WebSocketMessage(WebSocketMessageType.Text, new System.ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes("one")), true),
                new WebSocketMessage(WebSocketMessageType.Close, System.ReadOnlyMemory<byte>.Empty, true, WebSocketCloseStatus.NormalClosure)
            };
            var transport = new MockWebSocketTransport(scripted);
            var session = await transport.ConnectAsync(
                new WebSocketRequest("ws://localhost/ws", new TransportHeaders()),
                new WebSocketOptions(),
                CancellationToken.None);

            var received = new List<WebSocketMessage>();
            await foreach (var message in session.ReceiveAsync(CancellationToken.None))
            {
                received.Add(message);
            }

            Assert.Equal(2, received.Count);
            Assert.Equal(WebSocketMessageType.Close, received[1].Type);
            Assert.Equal(WebSocketCloseStatus.NormalClosure, received[1].CloseStatus);
        }

        [Fact]
        public async Task Close_Sets_State()
        {
            var transport = new MockWebSocketTransport();
            var session = await transport.ConnectAsync(
                new WebSocketRequest("ws://localhost/ws", new TransportHeaders()),
                new WebSocketOptions(),
                CancellationToken.None);

            await session.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

            Assert.Equal(WebSocketState.Closed, session.State);
        }
    }
}
