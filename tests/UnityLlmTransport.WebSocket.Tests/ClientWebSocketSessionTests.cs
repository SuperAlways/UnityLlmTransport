using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("ws-server")]
    public class ClientWebSocketSessionTests
    {
        private readonly WsServerFixture _fixture;

        public ClientWebSocketSessionTests(WsServerFixture fixture)
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

        private static async Task<WebSocketMessage> ReceiveOne(IWebSocketSession session, CancellationToken ct = default)
        {
            await foreach (var message in session.ReceiveAsync(ct))
            {
                return message;
            }
            throw new InvalidOperationException("no message");
        }

        [Fact]
        public async Task Fragment_Yields_EndOfMessage_Frames()
        {
            var session = await Connect("/ws/fragment");
            var messages = new List<WebSocketMessage>();
            await foreach (var message in session.ReceiveAsync(CancellationToken.None))
            {
                messages.Add(message);
                if (messages.Count == 2)
                {
                    break;
                }
            }
            await session.DisposeAsync();

            Assert.False(messages[0].EndOfMessage);
            Assert.Equal("hel", Encoding.UTF8.GetString(messages[0].Payload.ToArray()));
            Assert.True(messages[1].EndOfMessage);
            Assert.Equal("lo", Encoding.UTF8.GetString(messages[1].Payload.ToArray()));
        }

        [Fact]
        public async Task Client_Close_Handshake_Closes_State()
        {
            var session = await Connect("/ws/echo");
            await session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("hello"), true),
                CancellationToken.None);
            await ReceiveOne(session);
            await session.CloseAsync(
                System.Net.WebSockets.WebSocketCloseStatus.NormalClosure,
                "bye",
                CancellationToken.None);

            Assert.Equal(System.Net.WebSockets.WebSocketState.Closed, session.State);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Abort_Makes_Send_And_Receive_Throw_Aborted()
        {
            var session = await Connect("/ws/echo");
            session.Abort();

            var sendError = await Assert.ThrowsAsync<TransportException>(() => session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("x"), true),
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Aborted, sendError.Kind);

            var receiveError = await Assert.ThrowsAsync<TransportException>(() =>
                ReceiveOne(session));
            Assert.Equal(TransportErrorKind.Aborted, receiveError.Kind);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task ConnectTimeout_Is_Retryable_Connect()
        {
            var transport = new ClientWebSocketTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.ConnectAsync(
                new WebSocketRequest(_fixture.Server.HttpBase + "/ws/slow", new TransportHeaders()),
                new WebSocketOptions { ConnectTimeout = TimeSpan.FromMilliseconds(100) },
                CancellationToken.None));

            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public async Task IdleTimeout_Is_Idle_Timeout()
        {
            var session = await Connect("/ws/idle", new WebSocketOptions
            {
                IdleTimeout = TimeSpan.FromMilliseconds(100)
            });
            var error = await Assert.ThrowsAsync<TransportException>(() => ReceiveOne(session));
            Assert.Equal(TransportErrorKind.IdleTimeout, error.Kind);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task MaxMessageBytes_Overflow_Aborts()
        {
            var session = await Connect("/ws/fragment", new WebSocketOptions
            {
                MaxMessageBytes = 3
            });
            var received = new List<WebSocketMessage>();
            var error = await Assert.ThrowsAsync<TransportException>(async () =>
            {
                await foreach (var message in session.ReceiveAsync(CancellationToken.None))
                {
                    received.Add(message);
                }
            });
            await session.DisposeAsync();

            Assert.Single(received);
            Assert.Equal(TransportErrorKind.Overflow, error.Kind);
        }

        [Fact]
        public async Task Caller_Cancel_Receive_Is_Cancelled()
        {
            var session = await Connect("/ws/idle");
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(100);

            var error = await Assert.ThrowsAsync<TransportException>(() =>
                ReceiveOne(session, cts.Token));
            Assert.True(
                error.Kind == TransportErrorKind.Cancelled ||
                error.Kind == TransportErrorKind.Aborted);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Server_Drop_Is_Protocol()
        {
            var session = await Connect("/ws/drop");
            var error = await Assert.ThrowsAsync<TransportException>(() => ReceiveOne(session));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Send_Close_Type_Is_Protocol()
        {
            var session = await Connect("/ws/echo");
            var error = await Assert.ThrowsAsync<TransportException>(() => session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Close, ReadOnlyMemory<byte>.Empty, true),
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
            await session.DisposeAsync();
        }

        [Fact]
        public async Task Lifetime_Cancel_Aborts_Session()
        {
            using var lifetime = new CancellationTokenSource();
            var session = await Connect("/ws/echo", null, lifetime.Token);
            lifetime.Cancel();

            var error = await Assert.ThrowsAsync<TransportException>(() => session.SendAsync(
                new WebSocketMessage(WebSocketMessageType.Text, Encoding.UTF8.GetBytes("x"), true),
                CancellationToken.None));
            Assert.True(
                error.Kind == TransportErrorKind.Cancelled ||
                error.Kind == TransportErrorKind.Aborted);
            await session.DisposeAsync();
        }
    }
}
