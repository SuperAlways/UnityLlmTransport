using System;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class SignerFinalizerTests
    {
        private static TransportRequest Request()
        {
            return new TransportRequest(
                "POST",
                "http://localhost/test",
                new TransportHeaders(),
                ReadOnlyMemory<byte>.Empty);
        }

        [Fact]
        public async Task Calls_Signer_And_Returns_Signed_Request()
        {
            var signer = new HeaderSigner("x-signature", "signed");
            var finalizer = new SignerFinalizer(signer);

            var result = await finalizer.FinalizeAsync(Request(), CancellationToken.None);

            Assert.Equal(1, signer.Calls);
            Assert.Equal("signed", result.Headers["x-signature"]);
        }

        [Fact]
        public async Task Signer_Exception_Propagates()
        {
            var signer = new ThrowingSigner();
            var finalizer = new SignerFinalizer(signer);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => finalizer.FinalizeAsync(Request(), CancellationToken.None).AsTask());
            Assert.Contains("sign failed", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Null_Signer_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new SignerFinalizer(null!));
        }

        [Fact]
        public void Guard_Passes_When_Transport_Declares_RequestFinalizer()
        {
            TransportRequirements.RequireRequestFinalizer(
                TransportCapabilities.StreamingBody | TransportCapabilities.RequestFinalizer);
        }

        [Fact]
        public void Guard_Throws_Without_RequestFinalizer()
        {
            var error = Assert.Throws<TransportException>(() =>
                TransportRequirements.RequireRequestFinalizer(TransportCapabilities.StreamingBody));
            Assert.Equal(TransportErrorKind.Unsupported, error.Kind);
        }

        private sealed class HeaderSigner : IRequestSigner
        {
            private readonly string _name;
            private readonly string _value;

            public HeaderSigner(string name, string value)
            {
                _name = name;
                _value = value;
            }

            public int Calls { get; private set; }

            public ValueTask<TransportRequest> SignAsync(TransportRequest request, CancellationToken ct)
            {
                Calls++;
                var headers = new TransportHeaders();
                foreach (var pair in request.Headers)
                {
                    headers[pair.Key] = pair.Value;
                }
                headers[_name] = _value;
                return new ValueTask<TransportRequest>(request with { Headers = headers });
            }
        }

        private sealed class ThrowingSigner : IRequestSigner
        {
            public ValueTask<TransportRequest> SignAsync(TransportRequest request, CancellationToken ct)
            {
                throw new InvalidOperationException("sign failed");
            }
        }
    }
}
