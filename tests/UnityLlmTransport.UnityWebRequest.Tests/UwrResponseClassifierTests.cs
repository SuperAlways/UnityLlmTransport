using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class UwrResponseClassifierTests
    {
        private static UwrResponseSnapshot Snapshot(UwrCompletion completion, int status, string? error)
        {
            return new UwrResponseSnapshot(completion, status, new TransportHeaders(), error);
        }

        [Fact]
        public void Caller_Cancelled_With_Status_Is_Cancelled()
        {
            var error = UwrResponseClassifier.Classify(Snapshot(UwrCompletion.Success, 200, null), true);
            Assert.Equal(TransportErrorKind.Cancelled, error!.Kind);
        }

        [Fact]
        public void Aborted_Completion_Is_Cancelled()
        {
            var error = UwrResponseClassifier.Classify(Snapshot(UwrCompletion.Aborted, 200, "Request aborted"), false);
            Assert.Equal(TransportErrorKind.Cancelled, error!.Kind);
        }

        [Fact]
        public void Ssl_Error_Is_Tls()
        {
            var error = UwrResponseClassifier.Classify(Snapshot(UwrCompletion.ConnectionError, 0, "SSL CA certificate error"), false);
            Assert.Equal(TransportErrorKind.Tls, error!.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void Certificate_Error_Is_Tls()
        {
            var error = UwrResponseClassifier.Classify(Snapshot(UwrCompletion.ConnectionError, 0, "certificate verify failed"), false);
            Assert.Equal(TransportErrorKind.Tls, error!.Kind);
        }

        [Fact]
        public void Proxy_Error_Is_Proxy()
        {
            var error = UwrResponseClassifier.Classify(Snapshot(UwrCompletion.ConnectionError, 0, "Tunnel connection failed"), false);
            Assert.Equal(TransportErrorKind.Proxy, error!.Kind);
        }

        [Fact]
        public void Plain_Connection_Error_Is_Retryable_Connect()
        {
            var error = UwrResponseClassifier.Classify(Snapshot(UwrCompletion.ConnectionError, 0, "Cannot resolve destination host"), false);
            Assert.Equal(TransportErrorKind.Connect, error!.Kind);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public void Data_Processing_Error_Is_Protocol()
        {
            var error = UwrResponseClassifier.Classify(Snapshot(UwrCompletion.DataProcessingError, 0, "corrupted data"), false);
            Assert.Equal(TransportErrorKind.Protocol, error!.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void Protocol_Error_With_Status_Returns_Session()
        {
            Assert.Null(UwrResponseClassifier.Classify(Snapshot(UwrCompletion.ProtocolError, 500, "HTTP 500"), false));
        }

        [Fact]
        public void Success_With_Status_Returns_Session()
        {
            Assert.Null(UwrResponseClassifier.Classify(Snapshot(UwrCompletion.Success, 200, null), false));
        }

        [Fact]
        public void Success_Without_Status_Is_Protocol()
        {
            var error = UwrResponseClassifier.Classify(Snapshot(UwrCompletion.Success, 0, null), false);
            Assert.Equal(TransportErrorKind.Protocol, error!.Kind);
        }

        [Fact]
        public void Protocol_Error_With_Certificate_Text_And_No_Status_Is_Tls()
        {
            var error = UwrResponseClassifier.Classify(Snapshot(UwrCompletion.ProtocolError, 0, "SSL handshake failed"), false);
            Assert.Equal(TransportErrorKind.Tls, error!.Kind);
        }
    }
}
