using System;

namespace UnityLlmTransport
{
    public sealed class TransportException : Exception
    {
        public TransportException(
            TransportErrorKind kind,
            string message,
            Exception? innerException = null,
            int? statusCode = null,
            TransportHeaders? headers = null,
            string? errorBody = null,
            bool isRetryable = false)
            : base(message, innerException)
        {
            Kind = kind;
            StatusCode = statusCode;
            Headers = headers;
            ErrorBody = errorBody;
            IsRetryable = isRetryable;
        }

        public TransportErrorKind Kind { get; }
        public int? StatusCode { get; }
        public TransportHeaders? Headers { get; }
        public string? ErrorBody { get; }
        public bool IsRetryable { get; }
    }
}
