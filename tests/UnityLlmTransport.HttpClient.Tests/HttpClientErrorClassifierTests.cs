using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class HttpClientErrorClassifierTests
    {
        [Fact]
        public void Caller_Cancelled_Is_Cancelled()
        {
            var error = HttpClientErrorClassifier.Classify(new OperationCanceledException(), true, false);
            Assert.Equal(TransportErrorKind.Cancelled, error.Kind);
        }

        [Fact]
        public void Connect_Timeout_Is_Retryable_Connect()
        {
            var error = HttpClientErrorClassifier.Classify(new OperationCanceledException(), false, true);
            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public void Authentication_Exception_Is_Tls()
        {
            var error = HttpClientErrorClassifier.Classify(new AuthenticationException("bad cert"), false, false);
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void TrustFailure_Under_HttpRequestException_Is_Tls()
        {
            // Unity Mono 的 WebRequest 栈形状：HttpRequestException 包 WebException(TrustFailure)。
            var inner = new WebException("Error: TrustFailure", WebExceptionStatus.TrustFailure);
            var error = HttpClientErrorClassifier.Classify(
                new HttpRequestException("An error occurred while sending the request", inner), false, false);
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void Raw_SecureChannelFailure_Is_Tls()
        {
            var error = HttpClientErrorClassifier.Classify(
                new WebException("A security channel failure occurred.", WebExceptionStatus.SecureChannelFailure),
                false, false);
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void NonTls_WebException_Under_HttpRequestException_Stays_Connect()
        {
            var inner = new WebException("Unable to connect", WebExceptionStatus.ConnectFailure);
            var error = HttpClientErrorClassifier.Classify(
                new HttpRequestException("An error occurred while sending the request", inner), false, false);
            Assert.Equal(TransportErrorKind.Connect, error.Kind);
        }

        [Fact]
        public void Proxy_Text_Is_Proxy()
        {
            var inner = new SocketException((int)SocketError.ConnectionRefused);
            var error = HttpClientErrorClassifier.Classify(
                new HttpRequestException("CONNECT tunnel failed, response 407", inner), false, false);
            Assert.Equal(TransportErrorKind.Proxy, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void Plain_HttpRequestException_Is_Retryable_Connect()
        {
            var inner = new SocketException((int)SocketError.HostNotFound);
            var error = HttpClientErrorClassifier.Classify(
                new HttpRequestException("Name or service not known", inner), false, false);
            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public void Format_Exception_Is_Protocol()
        {
            var error = HttpClientErrorClassifier.Classify(new UriFormatException("bad uri"), false, false);
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
            Assert.False(error.IsRetryable);
        }
    }
}
