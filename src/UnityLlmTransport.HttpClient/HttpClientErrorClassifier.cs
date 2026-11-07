using System;
using System.Net;
using System.Net.Http;
using System.Security.Authentication;

namespace UnityLlmTransport
{
    internal static class HttpClientErrorClassifier
    {
        public static TransportException Classify(Exception exception, bool callerCancelled, bool connectTimeout)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }

            if (exception is OperationCanceledException)
            {
                if (callerCancelled)
                {
                    return new TransportException(
                        TransportErrorKind.Cancelled,
                        "Request cancelled.",
                        exception,
                        null,
                        null,
                        null,
                        false);
                }
                if (connectTimeout)
                {
                    return new TransportException(
                        TransportErrorKind.Connect,
                        "Connect/headers timeout.",
                        exception,
                        null,
                        null,
                        null,
                        true);
                }
                return new TransportException(
                    TransportErrorKind.Connect,
                    "Request canceled before response headers.",
                    exception,
                    null,
                    null,
                    null,
                    true);
            }

            if (exception is AuthenticationException)
            {
                return new TransportException(
                    TransportErrorKind.Tls,
                    "TLS handshake failed: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            var requestException = exception as HttpRequestException;
            if (requestException != null)
            {
                var inner = requestException.InnerException;
                if (inner is AuthenticationException)
                {
                    return new TransportException(
                        TransportErrorKind.Tls,
                        "TLS handshake failed: " + inner.Message,
                        exception,
                        null,
                        null,
                        null,
                        false);
                }

                // Unity Mono 的 HttpClient（WebRequest 栈）把证书信任失败包成
                // WebException(TrustFailure/SecureChannelFailure)，不是 AuthenticationException。
                var webException = inner as WebException;
                if (webException != null && IsTlsWebExceptionStatus(webException.Status))
                {
                    return new TransportException(
                        TransportErrorKind.Tls,
                        "TLS handshake failed: " + webException.Message,
                        exception,
                        null,
                        null,
                        null,
                        false);
                }

                string text = requestException.Message + " " + (inner != null ? inner.Message : string.Empty);
                if (Contains(text, "proxy") || Contains(text, "tunnel") || Contains(text, "407"))
                {
                    return new TransportException(
                        TransportErrorKind.Proxy,
                        "Proxy failure: " + text,
                        exception,
                        null,
                        null,
                        null,
                        false);
                }

                return new TransportException(
                    TransportErrorKind.Connect,
                    "Connection failed: " + text,
                    exception,
                    null,
                    null,
                    null,
                    true);
            }

            if (exception is WebException rawWebException && IsTlsWebExceptionStatus(rawWebException.Status))
            {
                return new TransportException(
                    TransportErrorKind.Tls,
                    "TLS handshake failed: " + rawWebException.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            if (exception is UriFormatException ||
                exception is ArgumentException ||
                exception is FormatException)
            {
                return new TransportException(
                    TransportErrorKind.Protocol,
                    "Invalid request: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            return new TransportException(
                TransportErrorKind.Protocol,
                "HTTP transport failed: " + exception.Message,
                exception,
                null,
                null,
                null,
                false);
        }

        private static bool IsTlsWebExceptionStatus(WebExceptionStatus status)
        {
            return status == WebExceptionStatus.TrustFailure ||
                   status == WebExceptionStatus.SecureChannelFailure;
        }

        private static bool Contains(string value, string fragment)
        {
            return value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
