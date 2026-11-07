using System.Net;

namespace UnityLlmTransport
{
    public sealed record ProxyOptions
    {
        public ProxyMode Mode { get; init; } = ProxyMode.System;
        public string? Url { get; init; }
        public NetworkCredential? Credentials { get; init; }
    }
}
