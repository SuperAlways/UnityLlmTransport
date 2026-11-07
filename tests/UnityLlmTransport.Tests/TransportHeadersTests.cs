using System.Collections.Generic;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class TransportHeadersTests
    {
        [Fact]
        public void Lookup_Is_Case_Insensitive()
        {
            var headers = new TransportHeaders();
            headers["Content-Type"] = "text/plain";

            Assert.Equal("text/plain", headers["content-type"]);
            Assert.True(headers.ContainsKey("CONTENT-TYPE"));
        }

        [Fact]
        public void Null_Value_Suppresses_Default()
        {
            var headers = new TransportHeaders();
            headers["Accept-Encoding"] = null;

            Assert.True(headers.ContainsKey("accept-encoding"));
            Assert.Null(headers["Accept-Encoding"]);
        }

        [Fact]
        public void Copy_Constructor_Keeps_Case_Insensitivity()
        {
            var source = new Dictionary<string, string?> { ["X-Api-Key"] = "k" };
            var headers = new TransportHeaders(source);

            Assert.Equal("k", headers["x-api-key"]);
        }
    }
}
