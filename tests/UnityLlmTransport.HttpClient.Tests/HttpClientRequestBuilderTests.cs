using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class HttpClientRequestBuilderTests
    {
        private static TransportRequest Request(string method = "POST", string url = "http://localhost/test", TransportHeaders? headers = null)
        {
            return new TransportRequest(
                method,
                url,
                headers ?? new TransportHeaders(),
                new ReadOnlyMemory<byte>(Encoding.UTF8.GetBytes("{}")));
        }

        [Fact]
        public void Builds_Method_Uri_And_Body()
        {
            using var message = HttpClientRequestBuilder.Build(Request("POST", "http://localhost/test"));

            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal(new Uri("http://localhost/test"), message.RequestUri);
            Assert.NotNull(message.Content);
            Assert.Equal(2, message.Content.Headers.ContentLength);
        }

        [Fact]
        public void BodyFactory_Is_Unsupported()
        {
            var request = Request() with
            {
                BodyFactory = ct => Task.FromResult<Stream>(new MemoryStream(new byte[] { 1 }))
            };

            var error = Assert.Throws<TransportException>(() => HttpClientRequestBuilder.Build(request));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        [Fact]
        public void Invalid_Uri_Is_Protocol()
        {
            var request = Request(url: "not a url");
            var error = Assert.Throws<TransportException>(() => HttpClientRequestBuilder.Build(request));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public void Invalid_Method_Is_Protocol()
        {
            var request = Request(method: "BAD METHOD");
            var error = Assert.Throws<TransportException>(() => HttpClientRequestBuilder.Build(request));
            Assert.Equal(TransportErrorKind.Protocol, error.Kind);
        }

        [Fact]
        public void Null_Header_Is_Skipped()
        {
            var headers = new TransportHeaders
            {
                ["X-Skip"] = null,
                ["X-Test"] = "1"
            };

            using var message = HttpClientRequestBuilder.Build(Request(headers: headers));

            Assert.False(message.Headers.Contains("X-Skip"));
            Assert.Equal("1", message.Headers.GetValues("X-Test").Single());
        }

        [Fact]
        public void Content_Header_Is_Routed_To_Content()
        {
            var headers = new TransportHeaders { ["Content-Type"] = "application/json" };
            using var message = HttpClientRequestBuilder.Build(Request(headers: headers));

            Assert.Equal("application/json", message.Content!.Headers.ContentType!.MediaType);
            // net7+ 的 HttpHeaders.Contains 对"内容头查请求头集合"会抛 InvalidOperationException（Mono/.NET Framework 返回 false），
            // 故用集合计数断言：本用例只有 Content-Type 一个头，路由到 content 后请求头应为空。
            Assert.Equal(0, message.Headers.Count());
        }

        [Fact]
        public void Caller_Content_Length_Is_Ignored()
        {
            var headers = new TransportHeaders { ["Content-Length"] = "999" };
            using var message = HttpClientRequestBuilder.Build(Request(headers: headers));

            Assert.Equal(2, message.Content!.Headers.ContentLength);
        }
    }
}
