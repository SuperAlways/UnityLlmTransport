using System;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Authentication;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class WebSocketErrorClassifierTests
    {
        [Fact]
        public void Caller_Cancelled_Is_Cancelled()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new OperationCanceledException(), true, false);
            Assert.Equal(TransportErrorKind.Cancelled, error.Kind);
        }

        [Fact]
        public void Connect_Timeout_Is_Retryable_Connect()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new OperationCanceledException(), false, true);
            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.Contains("timeout", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public void Authentication_Exception_Is_Tls()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new AuthenticationException("bad cert"), false, false);
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void Socket_Inner_Exception_Is_Connect()
        {
            var inner = new SocketException((int)SocketError.ConnectionRefused);
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new WebSocketException("Unable to connect", inner), false, false);
            Assert.Equal(TransportErrorKind.Connect, error.Kind);
            Assert.True(error.IsRetryable);
        }

        [Fact]
        public void Ssl_Text_Is_Tls()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new WebSocketException("SSL certificate error"), false, false);
            Assert.Equal(TransportErrorKind.Tls, error.Kind);
        }

        [Fact]
        public void Other_WebSocketException_Is_Handshake()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new WebSocketException("The server returned status code '400'"), false, false);
            Assert.Equal(TransportErrorKind.Handshake, error.Kind);
            Assert.False(error.IsRetryable);
        }

        [Fact]
        public void UriFormat_Is_Protocol()
        {
            var error = WebSocketErrorClassifier.ClassifyConnect(
                new UriFormatException("bad uri"), false, false);
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }
    }
}
