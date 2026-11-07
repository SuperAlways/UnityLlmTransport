using System;
using System.Collections.Generic;

namespace UnityLlmTransport
{
    /// <summary>大小写不敏感的请求/响应头。值为 null 表示抑制默认头。</summary>
    public sealed class TransportHeaders : Dictionary<string, string?>
    {
        public TransportHeaders() : base(StringComparer.OrdinalIgnoreCase) { }

        public TransportHeaders(IDictionary<string, string?> values)
            : base(values, StringComparer.OrdinalIgnoreCase) { }
    }
}
