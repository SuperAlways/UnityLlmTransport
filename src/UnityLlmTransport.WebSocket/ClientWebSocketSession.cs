using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    internal sealed class ClientWebSocketSession : IWebSocketSession
    {
        private const int ReceiveBufferSize = 16 * 1024;

        private readonly ClientWebSocket _socket;
        private readonly WebSocketOptions _options;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly WebSocketBufferBudget _budget;
        private readonly CancellationTokenRegistration _registration;
        private TransportException? _terminalError;
        private int _receiveStarted;
        private int _disposed;

        public ClientWebSocketSession(
            ClientWebSocket socket,
            WebSocketOptions options,
            CancellationToken lifetimeToken)
        {
            _socket = socket;
            _options = options;
            _budget = new WebSocketBufferBudget(options.MaxMessageBytes);
            _registration = lifetimeToken.Register(() => AbortWith(TransportErrorKind.Cancelled));
        }

        public WebSocketState State => _socket.State;

        public async Task SendAsync(WebSocketMessage message, CancellationToken ct)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            if (message.Type == WebSocketMessageType.Close)
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Close must be sent with CloseAsync, not SendAsync.");
            }

            ThrowIfTerminal();
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ThrowIfTerminal();
                if (_socket.State != WebSocketState.Open)
                {
                    throw new TransportException(TransportErrorKind.Aborted, "WebSocket is not open.");
                }

                var type = message.Type == WebSocketMessageType.Text
                    ? System.Net.WebSockets.WebSocketMessageType.Text
                    : System.Net.WebSockets.WebSocketMessageType.Binary;
                await _socket.SendAsync(
                    new ArraySegment<byte>(message.Payload.ToArray()),
                    type,
                    message.EndOfMessage,
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (TransportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowIfTerminal();
                throw WebSocketErrorClassifier.ClassifyOperation(ex, ct.IsCancellationRequested);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async IAsyncEnumerable<WebSocketMessage> ReceiveAsync(
            [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Exchange(ref _receiveStarted, 1) != 0)
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Only one ReceiveAsync enumeration is supported per session.");
            }

            var buffer = new byte[ReceiveBufferSize];
            while (true)
            {
                ThrowIfTerminal();
                if (_socket.State != WebSocketState.Open && _socket.State != WebSocketState.CloseReceived)
                {
                    yield break;
                }

                CancellationTokenSource? idleCts = null;
                CancellationTokenSource? linkedCts = null;
                WebSocketReceiveResult result;
                try
                {
                    CancellationToken token = ct;
                    if (_options.IdleTimeout.HasValue && _options.IdleTimeout.Value > TimeSpan.Zero)
                    {
                        idleCts = new CancellationTokenSource(_options.IdleTimeout.Value);
                        linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, idleCts.Token);
                        token = linkedCts.Token;
                    }

                    result = await _socket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex) when (
                    !ct.IsCancellationRequested &&
                    idleCts != null &&
                    idleCts.IsCancellationRequested)
                {
                    AbortWith(TransportErrorKind.IdleTimeout);
                    throw new TransportException(
                        TransportErrorKind.IdleTimeout,
                        "WebSocket idle timeout.",
                        ex,
                        null,
                        null,
                        null,
                        false);
                }
                catch (OperationCanceledException ex)
                {
                    AbortWith(TransportErrorKind.Cancelled);
                    throw new TransportException(
                        TransportErrorKind.Cancelled,
                        "WebSocket receive cancelled.",
                        ex,
                        null,
                        null,
                        null,
                        false);
                }
                catch (Exception ex)
                {
                    ThrowIfTerminal();
                    AbortWith(TransportErrorKind.Protocol);
                    throw WebSocketErrorClassifier.ClassifyOperation(ex, ct.IsCancellationRequested);
                }
                finally
                {
                    linkedCts?.Dispose();
                    idleCts?.Dispose();
                }

                if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                {
                    yield return new WebSocketMessage(
                        WebSocketMessageType.Close,
                        ReadOnlyMemory<byte>.Empty,
                        true,
                        result.CloseStatus);
                    yield break;
                }

                var type = result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text
                    ? WebSocketMessageType.Text
                    : WebSocketMessageType.Binary;
                var payload = new ReadOnlyMemory<byte>(
                    buffer.AsSpan(0, result.Count).ToArray());
                if (!_budget.Add(result.Count))
                {
                    AbortWith(TransportErrorKind.Overflow);
                    throw new TransportException(
                        TransportErrorKind.Overflow,
                        "WebSocket message exceeded MaxMessageBytes.");
                }

                yield return new WebSocketMessage(type, payload, result.EndOfMessage);
                if (result.EndOfMessage)
                {
                    _budget.Reset();
                }
            }
        }

        public async Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            ThrowIfTerminal();
            if (_socket.State == WebSocketState.Closed || _socket.State == WebSocketState.Aborted)
            {
                return;
            }
            try
            {
                await _socket.CloseAsync(status, description, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                ThrowIfTerminal();
                throw WebSocketErrorClassifier.ClassifyOperation(ex, ct.IsCancellationRequested);
            }
        }

        public void Abort()
        {
            AbortWith(TransportErrorKind.Aborted);
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                AbortWith(TransportErrorKind.Aborted);
                _socket.Dispose();
                _registration.Dispose();
                _sendLock.Dispose();
            }
            return default;
        }

        private void AbortWith(TransportErrorKind kind)
        {
            var error = new TransportException(kind, "WebSocket " + kind + ".");
            if (Interlocked.CompareExchange(ref _terminalError, error, null) == null)
            {
                try { _socket.Abort(); } catch { }
            }
        }

        private void ThrowIfTerminal()
        {
            var error = Volatile.Read(ref _terminalError);
            if (error != null)
            {
                throw error;
            }
        }
    }
}
