using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using UnityLlmTransport.Sse;
using Xunit;

namespace UnityLlmTransport.Tests
{
    [Collection("phase3-server")]
    public class StabilityTests
    {
        private readonly ServerFixture _fixture;

        public StabilityTests(ServerFixture fixture)
        {
            _fixture = fixture;
        }

        private static TransportRequest Request(string url)
        {
            return new TransportRequest(
                "POST",
                url,
                new TransportHeaders(),
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{}")));
        }

        private static async Task<List<string>> ParseAsync(Stream stream, CancellationToken ct = default)
        {
            var items = new List<string>();
            await foreach (var item in SseParser.Create(stream).EnumerateAsync(ct))
            {
                items.Add(item.Data);
            }
            return items;
        }

        [Fact]
        public async Task Sequential_SSE_Requests_No_Leak()
        {
            var transport = new HttpClientTransport();
            for (int i = 0; i < 20; i++)
            {
                var session = await transport.OpenAsync(
                    Request(_fixture.Server.HttpBase + "/sse"),
                    new TransportOptions(),
                    CancellationToken.None);
                var items = await ParseAsync(session.Body);
                await session.DisposeAsync();
                Assert.Equal(6, items.Count);
            }
        }

        [Fact]
        public async Task Cancel_And_Dispose_During_Stream()
        {
            var transport = new HttpClientTransport();
            for (int i = 0; i < 10; i++)
            {
                using var cts = new CancellationTokenSource();
                var session = await transport.OpenAsync(
                    Request(_fixture.Server.HttpBase + "/sse"),
                    new TransportOptions(),
                    cts.Token);
                var parse = Task.Run(() => ParseAsync(session.Body));
                await Task.Delay(50);
                cts.Cancel();
                try { await parse; } catch (Exception) { }
                await session.DisposeAsync();
            }
        }

        [Fact]
        public async Task Error_Body_Repeated()
        {
            var transport = new HttpClientTransport();
            for (int i = 0; i < 20; i++)
            {
                var session = await transport.OpenAsync(
                    Request(_fixture.Server.HttpBase + "/sse-error"),
                    new TransportOptions(),
                    CancellationToken.None);
                var buffer = new byte[256];
                var text = new StringBuilder();
                int read;
                while ((read = await session.Body.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
                {
                    text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
                await session.DisposeAsync();
                Assert.Equal(500, session.StatusCode);
                Assert.Contains("boom", text.ToString(), StringComparison.Ordinal);
            }
        }

        [Fact]
        public async Task Injected_Client_Reuse()
        {
            using var client = new System.Net.Http.HttpClient();
            var transport = new HttpClientTransport(new HttpClientTransportOptions { Client = client });
            for (int i = 0; i < 10; i++)
            {
                var session = await transport.OpenAsync(
                    Request(_fixture.Server.HttpBase + "/echo"),
                    new TransportOptions(),
                    CancellationToken.None);
                await session.DisposeAsync();
                Assert.Equal(200, session.StatusCode);
            }
        }
    }
}
