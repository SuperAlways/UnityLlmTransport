using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
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
            _budget = new WebSocketBufferBudget(options.MaxMessageBytes ?? ClientWebSocketTransport.DefaultMaxMessageBytes);
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

        // 说明（2026-11-07，Phase 5 四版本门禁实测驱动）：
        // 这里不用 `async IAsyncEnumerable` 编译器迭代器，而是手写 IAsyncEnumerable/IAsyncEnumerator。
        // 原因：Unity 2022.3 IL2CPP 的 corelib 中 AsyncIteratorMethodBuilder/ManualResetValueTaskSourceCore
        // 存在缺陷——消费方在收到一条消息后 break（await foreach 触发迭代器 DisposeAsync 的续跑）会抛
        // "InvalidOperationException: Operation is not valid due to the current state of the object"
        //（栈：ManualResetValueTaskSourceCoreShared.CompletionSentinel → InvokeContinuation → 迭代器 MoveNext）。
        // 2022.3 IL2CPP 实机复现；2021.3 IL2CPP / 6.3 / 6.6 IL2CPP 与四版本 Mono 均正常。
        // 手写枚举器的 DisposeAsync 是普通代码（不触编译器续跑），语义与原迭代器逐条对齐。
        public IAsyncEnumerable<WebSocketMessage> ReceiveAsync(CancellationToken ct)
        {
            return new ReceiveEnumerator(this, ct);
        }

        private sealed class ReceiveEnumerator : IAsyncEnumerable<WebSocketMessage>, IAsyncEnumerator<WebSocketMessage>
        {
            private readonly ClientWebSocketSession _session;
            private readonly CancellationToken _ct;
            private readonly byte[] _buffer = new byte[ReceiveBufferSize];
            private CancellationTokenSource? _linkedCts;
            private CancellationToken _token;
            private WebSocketMessage? _current;
            private bool _firstMoveNext = true;
            private bool _resetBudget;
            private bool _done;
            private bool _disposed;

            public ReceiveEnumerator(ClientWebSocketSession session, CancellationToken ct)
            {
                _session = session;
                _ct = ct;
                _token = ct;
            }

            public WebSocketMessage Current => _current!;

            public IAsyncEnumerator<WebSocketMessage> GetAsyncEnumerator(CancellationToken cancellationToken)
            {
                if (cancellationToken.CanBeCanceled && cancellationToken != _ct)
                {
                    _linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ct, cancellationToken);
                    _token = _linkedCts.Token;
                }
                return this;
            }

            public async ValueTask<bool> MoveNextAsync()
            {
                if (_done || _disposed)
                {
                    return false;
                }
                if (_firstMoveNext)
                {
                    _firstMoveNext = false;
                    if (Interlocked.Exchange(ref _session._receiveStarted, 1) != 0)
                    {
                        throw new TransportException(
                            TransportErrorKind.Protocol,
                            "Only one ReceiveAsync enumeration is supported per session.");
                    }
                }
                if (_resetBudget)
                {
                    _resetBudget = false;
                    _session._budget.Reset();
                }

                while (true)
                {
                    _session.ThrowIfTerminal();
                    if (_session._socket.State != WebSocketState.Open &&
                        _session._socket.State != WebSocketState.CloseReceived)
                    {
                        _done = true;
                        return false;
                    }

                    CancellationTokenSource? idleCts = null;
                    CancellationTokenSource? linkedCts = null;
                    WebSocketReceiveResult result;
                    try
                    {
                        CancellationToken token = _token;
                        if (_session._options.IdleTimeout.HasValue &&
                            _session._options.IdleTimeout.Value > TimeSpan.Zero)
                        {
                            idleCts = new CancellationTokenSource(_session._options.IdleTimeout.Value);
                            linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_token, idleCts.Token);
                            token = linkedCts.Token;
                        }

                        result = await _session._socket.ReceiveAsync(
                            new ArraySegment<byte>(_buffer),
                            token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException ex) when (
                        !_token.IsCancellationRequested &&
                        idleCts != null &&
                        idleCts.IsCancellationRequested)
                    {
                        _done = true;
                        _session.AbortWith(TransportErrorKind.IdleTimeout);
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
                        _done = true;
                        _session.AbortWith(TransportErrorKind.Cancelled);
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
                        _done = true;
                        _session.ThrowIfTerminal();
                        _session.AbortWith(TransportErrorKind.Protocol);
                        throw WebSocketErrorClassifier.ClassifyOperation(ex, _token.IsCancellationRequested);
                    }
                    finally
                    {
                        linkedCts?.Dispose();
                        idleCts?.Dispose();
                    }

                    if (result.MessageType == System.Net.WebSockets.WebSocketMessageType.Close)
                    {
                        _current = new WebSocketMessage(
                            WebSocketMessageType.Close,
                            ReadOnlyMemory<byte>.Empty,
                            true,
                            result.CloseStatus);
                        _done = true;
                        return true;
                    }

                    var type = result.MessageType == System.Net.WebSockets.WebSocketMessageType.Text
                        ? WebSocketMessageType.Text
                        : WebSocketMessageType.Binary;
                    var payload = new ReadOnlyMemory<byte>(
                        _buffer.AsSpan(0, result.Count).ToArray());
                    if (!_session._budget.Add(result.Count))
                    {
                        _done = true;
                        _session.AbortWith(TransportErrorKind.Overflow);
                        throw new TransportException(
                            TransportErrorKind.Overflow,
                            "WebSocket message exceeded MaxMessageBytes.");
                    }

                    _current = new WebSocketMessage(type, payload, result.EndOfMessage);
                    if (result.EndOfMessage)
                    {
                        _resetBudget = true;
                    }
                    return true;
                }
            }

            // 早期 break（消费方拿到一条消息后停止枚举）走这里：纯标志位清理，不续跑状态机、
            // 不 Abort 连接（连接生命周期归 session 的 DisposeAsync/Abort 管）。
            public ValueTask DisposeAsync()
            {
                _disposed = true;
                _done = true;
                _linkedCts?.Dispose();
                _linkedCts = null;
                return default;
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
