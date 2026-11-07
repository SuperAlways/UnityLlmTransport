using System;
using System.Net.Http;

namespace UnityLlmTransport
{
    internal static class HttpClientRequestBuilder
    {
        public static HttpRequestMessage Build(TransportRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (request.BodyFactory != null)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "HttpClient transport does not support BodyFactory (streaming request body).");
            }

            HttpMethod method;
            try
            {
                method = new HttpMethod(request.Method);
            }
            catch (Exception ex)
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Invalid HTTP method: " + request.Method,
                    ex,
                    null,
                    null,
                    null,
                    false);
            }

            Uri uri;
            try
            {
                uri = new Uri(request.Url, UriKind.Absolute);
            }
            catch (Exception ex)
            {
                throw new TransportException(
                    TransportErrorKind.Protocol,
                    "Invalid request URI: " + request.Url,
                    ex,
                    null,
                    null,
                    null,
                    false);
            }

            var message = new HttpRequestMessage(method, uri);
            if (request.Body.Length > 0)
            {
                message.Content = new ByteArrayContent(request.Body.ToArray());
            }

            foreach (var pair in request.Headers)
            {
                if (pair.Value == null)
                {
                    continue;
                }
                if (pair.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 由实际 body 决定
                }
                if (IsContentHeader(pair.Key))
                {
                    if (message.Content != null)
                    {
                        message.Content.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
                    }
                    continue;
                }
                message.Headers.TryAddWithoutValidation(pair.Key, pair.Value);
            }

            return message;
        }

        private static bool IsContentHeader(string name)
        {
            if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Language", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Location", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Range", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Expires", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Last-Modified", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }
    }
}
