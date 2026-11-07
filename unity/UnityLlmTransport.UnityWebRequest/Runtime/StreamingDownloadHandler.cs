using System;
using UnityEngine.Networking;

namespace UnityLlmTransport
{
    public sealed class StreamingDownloadHandler : DownloadHandlerScript, IStreamingDownloadHandler
    {
        private readonly ChunkQueue _queue;
        private volatile bool _aborted;

        public StreamingDownloadHandler(ChunkQueue queue, int downloadBufferSize)
            : base(AllocateBuffer(downloadBufferSize))
        {
            if (queue == null)
            {
                throw new ArgumentNullException(nameof(queue));
            }
            _queue = queue;
        }

        private static byte[] AllocateBuffer(int downloadBufferSize)
        {
            if (downloadBufferSize <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(downloadBufferSize));
            }
            return new byte[downloadBufferSize];
        }

        public bool FirstDataReceived { get; private set; }

        protected override bool ReceiveData(byte[] data, int dataLength)
        {
            if (_aborted)
            {
                return false;
            }
            if (dataLength <= 0)
            {
                return true;
            }
            if (!_queue.TryWrite(data, dataLength))
            {
                _aborted = true;
                return false;
            }
            FirstDataReceived = true;
            return true;
        }

        protected override void CompleteContent()
        {
            _queue.Complete();
        }

        public void MarkAborted(TransportErrorKind kind)
        {
            if (_aborted)
            {
                return;
            }
            _aborted = true;
            _queue.Abort(new TransportException(kind, "Response aborted (" + kind + ")."));
        }
    }
}
