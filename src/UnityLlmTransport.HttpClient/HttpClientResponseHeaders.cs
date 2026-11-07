using System.Net.Http;

namespace UnityLlmTransport
{
    internal static class HttpClientResponseHeaders
    {
        public static TransportHeaders From(HttpResponseMessage response)
        {
            var headers = new TransportHeaders();
            foreach (var header in response.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }
            if (response.Content != null)
            {
                foreach (var header in response.Content.Headers)
                {
                    headers[header.Key] = string.Join(",", header.Value);
                }
            }
            return headers;
        }
    }
}
