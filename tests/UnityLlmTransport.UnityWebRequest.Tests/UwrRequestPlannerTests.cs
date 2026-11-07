using System;
using System.IO;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class UwrRequestPlannerTests
    {
        private static TransportRequest Request(TransportHeaders? headers = null)
        {
            return new TransportRequest(
                "POST",
                "http://localhost/test",
                headers ?? new TransportHeaders(),
                ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public void Defaults_To_System_Proxy_And_Follow_Redirects()
        {
            var plan = UwrRequestPlanner.Create(Request(), new TransportOptions());

            Assert.Equal(32, plan.RedirectLimit);
            Assert.False(plan.AcceptAllCertificates);
            Assert.Equal("identity", plan.Headers["Accept-Encoding"]);
        }

        [Fact]
        public void Explicit_Proxy_Is_Unsupported()
        {
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" }
            };

            var error = Assert.Throws<TransportException>(() => UwrRequestPlanner.Create(Request(), options));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Disabled_Proxy_Is_Unsupported()
        {
            var options = new TransportOptions { Proxy = new ProxyOptions { Mode = ProxyMode.None } };

            var error = Assert.Throws<TransportException>(() => UwrRequestPlanner.Create(Request(), options));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Custom_Roots_Are_Unsupported()
        {
            var options = new TransportOptions
            {
                Tls = new TlsOptions { CustomRoots = Array.Empty<X509Certificate2>() }
            };

            var error = Assert.Throws<TransportException>(() => UwrRequestPlanner.Create(Request(), options));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Protocols_Are_Unsupported()
        {
            var options = new TransportOptions
            {
                Tls = new TlsOptions { Protocols = SslProtocols.Tls12 }
            };

            var error = Assert.Throws<TransportException>(() => UwrRequestPlanner.Create(Request(), options));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Redirect_Error_Uses_Zero_Limit()
        {
            var plan = UwrRequestPlanner.Create(Request(), new TransportOptions { Redirects = RedirectPolicy.Error });
            Assert.Equal(0, plan.RedirectLimit);
        }

        [Fact]
        public void BodyFactory_Is_Unsupported()
        {
            var request = Request() with
            {
                BodyFactory = ct => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1 }))
            };

            var error = Assert.Throws<TransportException>(() => UwrRequestPlanner.Create(request, new TransportOptions()));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Null_Headers_Are_Suppressed()
        {
            var headers = new TransportHeaders
            {
                ["User-Agent"] = null,
                ["X-Test"] = "1"
            };
            var plan = UwrRequestPlanner.Create(Request(headers), new TransportOptions());

            Assert.False(plan.Headers.ContainsKey("User-Agent"));
            Assert.Equal("1", plan.Headers["x-test"]);
        }

        [Fact]
        public void Caller_Accept_Encoding_Is_Preserved()
        {
            var headers = new TransportHeaders { ["Accept-Encoding"] = "gzip" };
            var plan = UwrRequestPlanner.Create(Request(headers), new TransportOptions());

            Assert.Equal("gzip", plan.Headers["Accept-Encoding"]);
        }

        [Fact]
        public void Accept_All_When_Validation_Disabled()
        {
            var plan = UwrRequestPlanner.Create(Request(), new TransportOptions
            {
                Tls = new TlsOptions { ValidateCertificate = false }
            });
            Assert.True(plan.AcceptAllCertificates);
        }
    }
}
