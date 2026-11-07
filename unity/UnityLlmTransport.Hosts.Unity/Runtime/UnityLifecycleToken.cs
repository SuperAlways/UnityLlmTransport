using System;
using System.Threading;

namespace UnityLlmTransport
{
    public sealed class UnityLifecycleToken
    {
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private int _disposed;

        public CancellationToken Token
        {
            get { return Volatile.Read(ref _cts).Token; }
        }

        public void Invalidate()
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }
            var next = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _cts, next);
            previous.Cancel();
            previous.Dispose();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            Volatile.Read(ref _cts).Cancel();
        }
    }
}
