using System;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;

namespace UnityLlmTransport
{
    internal static class WebSocketErrorClassifier
    {
        public static TransportException ClassifyConnect(Exception exception, bool callerCancelled, bool connectTimeout)
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
                        "WebSocket connect cancelled.",
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
                        "WebSocket connect timeout.",
                        exception,
                        null,
                        null,
                        null,
                        true);
                }
                return new TransportException(
                    TransportErrorKind.Connect,
                    "WebSocket connect canceled before handshake.",
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
                    "WebSocket TLS handshake failed: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            var webSocketException = exception as WebSocketException;
            if (webSocketException != null)
            {
                var inner = webSocketException.InnerException;
                if (inner is AuthenticationException)
                {
                    return new TransportException(
                        TransportErrorKind.Tls,
                        "WebSocket TLS handshake failed: " + inner.Message,
                        exception,
                        null,
                        null,
                        null,
                        false);
                }

                string text = webSocketException.Message + " " + (inner != null ? inner.Message : string.Empty);
                if (Contains(text, "SSL") || Contains(text, "certificate"))
                {
                    return new TransportException(
                        TransportErrorKind.Tls,
                        "WebSocket TLS handshake failed: " + text,
                        exception,
                        null,
                        null,
                        null,
                        false);
                }
                if (inner is SocketException || Contains(text, "refused") || Contains(text, "Unable to connect"))
                {
                    return new TransportException(
                        TransportErrorKind.Connect,
                        "WebSocket connection failed: " + text,
                        exception,
                        null,
                        null,
                        null,
                        true);
                }
                return new TransportException(
                    TransportErrorKind.Handshake,
                    "WebSocket handshake failed: " + text,
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
                    "Invalid WebSocket request: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }

            return new TransportException(
                TransportErrorKind.Protocol,
                "WebSocket connect failed: " + exception.Message,
                exception,
                null,
                null,
                null,
                false);
        }

        public static TransportException ClassifyOperation(Exception exception, bool callerCancelled)
        {
            if (exception == null)
            {
                throw new ArgumentNullException(nameof(exception));
            }
            if (exception is OperationCanceledException && callerCancelled)
            {
                return new TransportException(
                    TransportErrorKind.Cancelled,
                    "WebSocket operation cancelled.",
                    exception,
                    null,
                    null,
                    null,
                    false);
            }
            if (exception is WebSocketException || exception is System.IO.IOException)
            {
                return new TransportException(
                    TransportErrorKind.Protocol,
                    "WebSocket operation failed: " + exception.Message,
                    exception,
                    null,
                    null,
                    null,
                    false);
            }
            return new TransportException(
                TransportErrorKind.Protocol,
                "WebSocket operation failed: " + exception.Message,
                exception,
                null,
                null,
                null,
                false);
        }

        private static bool Contains(string value, string fragment)
        {
            return value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
