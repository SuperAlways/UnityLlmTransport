using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("phase3-server")]
    public class HttpClientContractTests
    {
        private readonly ServerFixture _fixture;

        public HttpClientContractTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        [Fact]
        public async Task Same_Fixture_Produces_Same_Sse_Items_As_Mock()
        {
            var transport = new HttpClientTransport();
            var httpSession = await transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/fixture"),
                new TransportOptions(),
                CancellationToken.None);
            var httpItems = await HttpClientTestHelpers.ParseAsync(httpSession.Body);
            await httpSession.DisposeAsync();

            var fixtureText =
                "data: 你\n\n" +
                "data: 好\n\n" +
                "data: ，\n\n" +
                "data: 世\n\n" +
                "data: 界\n\n" +
                "data: [DONE]\n\n";
            var fixtureBytes = new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes(fixtureText));
            var mock = new MockTransport(_ => new MockResponse { Chunks = new[] { fixtureBytes } });
            var mockSession = await mock.OpenAsync(
                HttpClientTestHelpers.Request("http://localhost/fixture"),
                new TransportOptions(),
                CancellationToken.None);
            var mockItems = await HttpClientTestHelpers.ParseAsync(mockSession.Body);
            await mockSession.DisposeAsync();

            // Phase 2 UWR 的同一 SSE fixture 条目（见 docs/spike/phase2-results.md 的 data= 字段）。
            Assert.Equal(new[] { "你", "好", "，", "世", "界", "[DONE]" }, httpItems);
            Assert.Equal(httpItems, mockItems);
        }

        [Fact]
        public async Task Injected_Client_Rejects_PerRequest_Proxy()
        {
            using var client = new System.Net.Http.HttpClient();
            var transport = new HttpClientTransport(new HttpClientTransportOptions { Client = client });
            var options = new TransportOptions
            {
                Proxy = new ProxyOptions { Mode = ProxyMode.Explicit, Url = "http://127.0.0.1:7897" }
            };

            var error = await Assert.ThrowsAsync<TransportException>(() => transport.OpenAsync(
                HttpClientTestHelpers.Request(_fixture.Server.HttpBase + "/echo"),
                options,
                CancellationToken.None));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }
    }
}
