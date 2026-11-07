using System;
using System.Collections.Generic;

namespace UnityLlmTransport
{
    public sealed record MockResponse
    {
        public int StatusCode { get; init; } = 200;
        public TransportHeaders? Headers { get; init; }
        public IReadOnlyList<ReadOnlyMemory<byte>>? Chunks { get; init; }
        public TimeSpan ChunkDelay { get; init; }
        public Exception? Error { get; init; }
    }
}
