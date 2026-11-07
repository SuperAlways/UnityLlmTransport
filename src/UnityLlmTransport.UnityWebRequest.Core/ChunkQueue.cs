using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    /// <summary>单写者（Unity 主线程）/单读者（后台）有界字节块队列。</summary>
    public sealed class ChunkQueue
    {
        private readonly object _gate = new object();
        private readonly int _maxBufferedBytes;
        private readonly Queue<byte[]> _chunks = new Queue<byte[]>();
        private int _bufferedBytes;
        private bool _completed;
        private Exception? _completionError;
        private TaskCompletionSource<bool>? _readSignal;

        public ChunkQueue(int maxBufferedBytes)
        {
            if (maxBufferedBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxBufferedBytes));
            }
            _maxBufferedBytes = maxBufferedBytes;
        }

        public int BufferedBytes
        {
            get { lock (_gate) { return _bufferedBytes; } }
        }

        public bool IsTerminated
        {
            get { lock (_gate) { return _completed; } }
        }

        public Exception? CompletionError
        {
            get { lock (_gate) { return _completionError; } }
        }

        public bool TryWrite(byte[] data, int length)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (length < 0 || length > data.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(length));
            }

            TaskCompletionSource<bool>? signal;
            bool ok;
            bool signalValue;
            lock (_gate)
            {
                if (_completed)
                {
                    return false;
                }
                if (length == 0)
                {
                    return true;
                }

                if (_bufferedBytes + length > _maxBufferedBytes)
                {
                    _completed = true;
                    _completionError = new TransportException(
                        TransportErrorKind.Overflow,
                        "Response exceeded ChunkQueue capacity of " + _maxBufferedBytes + " bytes.");
                    _chunks.Clear();
                    _bufferedBytes = 0;
                    ok = false;
                    signalValue = false;
                }
                else
                {
                    var copy = new byte[length];
                    Array.Copy(data, 0, copy, 0, length);
                    _chunks.Enqueue(copy);
                    _bufferedBytes += length;
                    ok = true;
                    signalValue = true;
                }

                signal = TakeSignal();
            }

            if (signal != null)
            {
                signal.TrySetResult(signalValue);
            }
            return ok;
        }

        public bool TryRead(out ReadOnlyMemory<byte> chunk)
        {
            lock (_gate)
            {
                if (_chunks.Count > 0)
                {
                    var next = _chunks.Dequeue();
                    _bufferedBytes -= next.Length;
                    chunk = next;
                    return true;
                }

                chunk = default;
                return false;
            }
        }

        public async ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken)
        {
            Task<bool> signalTask;
            lock (_gate)
            {
                if (_chunks.Count > 0)
                {
                    return true;
                }
                if (_completed)
                {
                    return false;
                }
                if (_readSignal == null)
                {
                    _readSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                signalTask = _readSignal.Task;
            }

            return await WaitForSignalAsync(signalTask, cancellationToken).ConfigureAwait(false);
        }

        public void Complete(Exception? error = null)
        {
            TaskCompletionSource<bool>? signal;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }
                _completed = true;
                _completionError = error;
                if (error != null)
                {
                    _chunks.Clear();
                    _bufferedBytes = 0;
                }
                signal = TakeSignal();
            }

            if (signal != null)
            {
                signal.TrySetResult(false);
            }
        }

        public void Abort(Exception error)
        {
            if (error == null)
            {
                throw new ArgumentNullException(nameof(error));
            }
            Complete(error);
        }

        private TaskCompletionSource<bool>? TakeSignal()
        {
            var signal = _readSignal;
            _readSignal = null;
            return signal;
        }

        private static async ValueTask<bool> WaitForSignalAsync(Task<bool> signal, CancellationToken cancellationToken)
        {
            if (!cancellationToken.CanBeCanceled)
            {
                return await signal.ConfigureAwait(false);
            }

            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)))
            {
                var completed = await Task.WhenAny(signal, cancelled.Task).ConfigureAwait(false);
                if (completed != signal)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            return await signal.ConfigureAwait(false);
        }
    }
}
