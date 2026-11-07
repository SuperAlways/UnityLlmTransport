using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("ws-server")]
    public class ClientWebSocketTransportTests
    {
        private readonly WsServerFixture _fixture;

        public ClientWebSocketTransportTests(WsServerFixture fixture)
        {
            _fixture = fixture;
        }

        private static WebSocketRequest Request(string url, TransportHeaders? headers = null)
        {
            return new WebSocketRequest(url, headers ?? new TransportHeaders());
        }

        [Fact]
        public async Task Echo_Text()
        {
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request(_fixture.Server.HttpBase + "/ws/echo"),
                new WebSocketOptions(),
                CancellationToken.None);
            await session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hello"), true),
                CancellationToken.None);

            var received = await ReceiveOne(session);
            Assert.Equal(WebSocketMessageType.Text, received.Type);
            Assert.Equal("echo:hello", Encoding.UTF8.GetString(received.Payload.ToArray()));
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Echo_Binary()
        {
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request(_fixture.Server.HttpBase + "/ws/echo"),
                new WebSocketOptions(),
                CancellationToken.None);
            var payload = new byte[] { 1, 2, 3 };
            await session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Binary, payload, true),
                CancellationToken.None);

            var received = await ReceiveOne(session);
            Assert.Equal(WebSocketMessageType.Binary, received.Type);
            Assert.Equal(payload, received.Payload.ToArray());
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Server_Close_Yields_Close_Message()
        {
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request(_fixture.Server.HttpBase + "/ws/close"),
                new WebSocketOptions(),
                CancellationToken.None);

            var message = await ReceiveOne(session);
            Assert.Equal(WebSocketMessageType.Close, message.Type);
            Assert.Equal(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, message.CloseStatus);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Info_Request_Headers_And_SubProtocol_Reach_Server()
        {
            var headers = new TransportHeaders
            {
                ["X-Test"] = "yes",
                ["X-Skip"] = null
            };
            var transport = new ClientWebSocketTransport();
            var session = await transport.ConnectAsync(
                Request(
                    _fixture.Server.HttpBase + "/ws/info",
                    headers),
                new WebSocketOptions { SubProtocol = "phase5" },
                CancellationToken.None);

            var message = await ReceiveOne(session);
            using var json = JsonDocument.Parse(Encoding.UTF8.GetString(message.Payload.ToArray()));
            var protocols = json.RootElement.GetProperty("protocols");
            Assert.Contains("phase5", protocols.EnumerateArray().Select(e => e.GetString()));
            var echoed = json.RootElement.GetProperty("headers");
            Assert.True(echoed.TryGetProperty("X-Test", out var test));
            Assert.Equal("yes", test.GetString());
            Assert.False(echoed.TryGetProperty("X-Skip", out _));
            await session.DisposeAsync();
        }

        private static async Task<WebSocketMessage> ReceiveOne(IWebSocketSession session)
        {
            await foreach (var message in session.ReceiveAsync(CancellationToken.None))
            {
                return message;
            }
            throw new InvalidOperationException("no message received");
        }
    }
}
