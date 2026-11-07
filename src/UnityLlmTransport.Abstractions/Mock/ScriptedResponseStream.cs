using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    internal sealed class ScriptedResponseStream : Stream
    {
        private readonly List<ReadOnlyMemory<byte>> _chunks;
        private readonly TimeSpan _chunkDelay;
        private int _chunkIndex;
        private int _offset;
        private bool _aborted;

        public ScriptedResponseStream(
            IReadOnlyList<ReadOnlyMemory<byte>>? chunks,
            TimeSpan chunkDelay,
            CancellationToken cancellationToken)
        {
            _chunks = new List<ReadOnlyMemory<byte>>();
            if (chunks != null)
            {
                for (int i = 0; i < chunks.Count; i++)
                {
                    if (chunks[i].Length > 0)
                    {
                        _chunks.Add(chunks[i]);
                    }
                }
            }
            _chunkDelay = chunkDelay;
        }

        public void Abort()
        {
            _aborted = true;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("Use ReadAsync.");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_aborted || _chunkIndex >= _chunks.Count)
            {
                return 0;
            }

            if (buffer.Length == 0)
            {
                return 0;
            }

            if (_chunkDelay > TimeSpan.Zero)
            {
                await Task.Delay(_chunkDelay, cancellationToken).ConfigureAwait(false);
            }

            if (_aborted)
            {
                return 0;
            }

            var chunk = _chunks[_chunkIndex];
            int remaining = chunk.Length - _offset;
            int toCopy = remaining < buffer.Length ? remaining : buffer.Length;
            chunk.Slice(_offset, toCopy).CopyTo(buffer);
            _offset += toCopy;
            if (_offset == chunk.Length)
            {
                _chunkIndex++;
                _offset = 0;
            }
            return toCopy;
        }
    }
}
