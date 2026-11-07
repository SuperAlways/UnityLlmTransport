using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using UnityLlmTransport.Sse;

namespace UnityLlmTransport.Tests
{
    internal static class HttpClientTestHelpers
    {
        public static TransportRequest Request(
            string url,
            TransportHeaders? headers = null,
            string method = "POST",
            string body = "{}")
        {
            return new TransportRequest(
                method,
                url,
                headers ?? new TransportHeaders(),
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(body)));
        }

        public static async Task<List<string>> ParseAsync(Stream stream, CancellationToken cancellationToken = default)
        {
            var items = new List<string>();
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
            {
                items.Add(item.Data);
            }
            return items;
        }

        public static async Task<string> ReadAllTextAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[256];
            var builder = new StringBuilder();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            {
                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            return builder.ToString();
        }
    }
}
