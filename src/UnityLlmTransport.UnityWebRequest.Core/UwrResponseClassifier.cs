using System;

namespace UnityLlmTransport
{
    public static class UwrResponseClassifier
    {
        /// <summary>返回 null = 可交付会话（任意非 2xx 状态）；非 null = 应抛出的 TransportException。</summary>
        public static TransportException? Classify(UwrResponseSnapshot snapshot, bool callerCancelled)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (callerCancelled ||
                snapshot.Completion == UwrCompletion.Aborted ||
                Contains(snapshot.Error, "aborted"))
            {
                return new TransportException(
                    TransportErrorKind.Cancelled,
                    "Request cancelled.",
                    null,
                    snapshot.StatusCode,
                    snapshot.Headers,
                    snapshot.Error,
                    false);
            }

            if (snapshot.StatusCode > 0)
            {
                return null;
            }

            var error = snapshot.Error ?? string.Empty;
            if (snapshot.Completion == UwrCompletion.ConnectionError ||
                snapshot.Completion == UwrCompletion.ProtocolError)
            {
                if (Contains(error, "SSL") || Contains(error, "certificate"))
                {
                    return new TransportException(
                        TransportErrorKind.Tls,
                        "TLS handshake failed: " + error,
                        null,
                        null,
                        snapshot.Headers,
                        error,
                        false);
                }
                if (Contains(error, "proxy") || Contains(error, "Tunnel") || Contains(error, "407"))
                {
                    return new TransportException(
                        TransportErrorKind.Proxy,
                        "Proxy failure: " + error,
                        null,
                        null,
                        snapshot.Headers,
                        error,
                        false);
                }
                return new TransportException(
                    TransportErrorKind.Connect,
                    "Connection failed: " + error,
                    null,
                    null,
                    snapshot.Headers,
                    error,
                    true);
            }

            if (snapshot.Completion == UwrCompletion.DataProcessingError)
            {
                return new TransportException(
                    TransportErrorKind.Protocol,
                    "Data processing failed: " + error,
                    null,
                    null,
                    snapshot.Headers,
                    error,
                    false);
            }

            return new TransportException(
                TransportErrorKind.Protocol,
                "Success without HTTP status code.",
                null,
                null,
                snapshot.Headers,
                error,
                false);
        }

        private static bool Contains(string? value, string fragment)
        {
            return value != null && value.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
