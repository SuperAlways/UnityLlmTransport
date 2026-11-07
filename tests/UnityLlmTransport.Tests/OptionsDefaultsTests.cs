using System;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class OptionsDefaultsTests
    {
        [Fact]
        public void TransportOptions_Has_Spec_Defaults()
        {
            var options = new TransportOptions();

            Assert.Equal(TransportKind.Auto, options.Kind);
            Assert.Equal(RedirectPolicy.Follow, options.Redirects);
            Assert.True(options.Decompress);
            Assert.Null(options.Proxy);
            Assert.Null(options.FirstByteTimeout);
        }

        [Fact]
        public void ProxyOptions_Defaults_To_System()
        {
            var proxy = new ProxyOptions();

            Assert.Equal(ProxyMode.System, proxy.Mode);
            Assert.Null(proxy.Url);
        }

        [Fact]
        public void TlsOptions_Validates_By_Default()
        {
            var tls = new TlsOptions();

            Assert.True(tls.ValidateCertificate);
        }

        [Fact]
        public void TransportException_Carries_Classification()
        {
            var headers = new TransportHeaders { ["retry-after-ms"] = "100" };
            var error = new TransportException(
                TransportErrorKind.HttpStatus,
                "boom",
                null,
                503,
                headers,
                "{\"error\":\"boom\"}",
                true);

            Assert.Equal(TransportErrorKind.HttpStatus, error.Kind);
            Assert.Equal(503, error.StatusCode);
            Assert.Equal("{\"error\":\"boom\"}", error.ErrorBody);
            Assert.True(error.IsRetryable);
            Assert.Same(headers, error.Headers);
        }

        [Fact]
        public void Capability_Flags_Combine()
        {
            var capabilities = TransportCapabilities.StreamingBody | TransportCapabilities.SystemProxy;

            Assert.True(capabilities.HasFlag(TransportCapabilities.StreamingBody));
            Assert.True(capabilities.HasFlag(TransportCapabilities.SystemProxy));
            Assert.False(capabilities.HasFlag(TransportCapabilities.WebSocket));
        }
    }
}
