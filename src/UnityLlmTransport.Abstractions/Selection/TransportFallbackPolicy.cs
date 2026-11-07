using System.Collections.Generic;

namespace UnityLlmTransport
{
    public sealed record TransportFallbackPolicy(
        IReadOnlyList<TransportKind> Order,
        bool FallbackOnlyBeforeFirstMessage = true);
}
