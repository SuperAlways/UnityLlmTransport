using System;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("phase3-server")]
    public class HttpClientProxyTlsTests
    {
        private readonly ServerFixture _fixture;

        public HttpClientProxyTlsTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Explicit_Proxy_Is_Used()
        {
            var transport = new HttpClientTransport();
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions
                {
                    Mode = ProxyMode.Explicit,
                    Url = _fixture.Server.ProxyUrl
                }
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request("http://phase3.invalid/echo"),
                options,
                CancellationToken.None);
            var text = await HttpClientTestHelpers.ReadAllTextAsync(session.Body, CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(200, session.StatusCode);
            Assert.Equal("proxied", text);
            Assert.True(session.Headers.TryGetValue("x-phase3-proxy", out var proxyHeader));
            Assert.Equal("yes", proxyHeader);
        }

        [Fact]
        public async Task SelfSigned_Default_Is_Tls()
        {
            var transport = new HttpClientTransport();
            var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpsBase + "/echo"),
                new TransportOptions(),
                CancellationToken.None));

            Assert.Equal(TransportErrorKind.Tls, error.Kind);
        }

        [Fact]
        public async Task SelfSigned_AcceptAll_Returns_200()
        {
            var transport = new HttpClientTransport();
            var options = new TransportOptions
            {
                Tls = new TlsOptions { ValidateCertificate = false }
            };

            var session = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpsBase + "/echo"),
                options,
                CancellationToken.None);
            var text = await HttpClientTestHelpers.ReadAllTextAsync(session.Body, CancellationToken.None);
            await session.DisposeAsync();

            Assert.Equal(200, session.StatusCode);
            Assert.Equal("ok", text);
        }
    }
}
