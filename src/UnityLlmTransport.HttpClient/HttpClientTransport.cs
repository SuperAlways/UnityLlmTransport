using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public sealed class HttpClientTransport : IStreamingHttpTransport
    {
        private static readonly TransportCapabilities DefaultCapabilities =
            TransportCapabilities.StreamingBody |
            TransportCapabilities.SystemProxy |
            TransportCapabilities.ExplicitProxy |
            TransportCapabilities.ProxyAuthentication |
            TransportCapabilities.TlsCertificateHandler |
            TransportCapabilities.Compression |
            TransportCapabilities.RequestFinalizer;

        private readonly HttpClient? _injectedClient;
        private readonly TransportCapabilities _capabilities;

        public HttpClientTransport()
            : this(new HttpClientTransportOptions())
        {
        }

        public HttpClientTransport(HttpClientTransportOptions options)
        {
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }
            _injectedClient = options.Client;
            _capabilities = options.CapabilitiesOverride ?? DefaultCapabilities;
        }

        public TransportCapabilities Capabilities => _capabilities;

        public async Task<ITransportSession> OpenAsync(
            TransportRequest request,
            TransportOptions options,
            CancellationToken cancellationToken)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            HttpClientRequestValidator.Validate(options, _injectedClient != null);

            HttpRequestMessage message;
            try
            {
                message = HttpClientRequestBuilder.Build(request);
            }
            catch (TransportException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw HttpClientErrorClassifier.Classify(ex, false, false);
            }

            HttpClient? client = _injectedClient;
            bool ownsClient = false;
            if (client == null)
            {
                HttpClientHandler handler;
                try
                {
                    handler = HttpClientHandlerFactory.Create(options);
                }
                catch (TransportException)
                {
                    message.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    message.Dispose();
                    throw HttpClientErrorClassifier.Classify(ex, false, false);
                }
                client = new HttpClient(handler, disposeHandler: true);
                client.Timeout = Timeout.InfiniteTimeSpan;
                ownsClient = true;
            }

            CancellationTokenSource? timeoutCts = null;
            CancellationTokenSource? linkedCts = null;
            HttpResponseMessage? response = null;
            try
            {
                CancellationToken token = cancellationToken;
                if (options.ConnectTimeout.HasValue && options.ConnectTimeout.Value > TimeSpan.Zero)
                {
                    timeoutCts = new CancellationTokenSource(options.ConnectTimeout.Value);
                    linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
                    token = linkedCts.Token;
                }

                try
                {
                    response = await client.SendAsync(
                        message,
                        HttpCompletionOption.ResponseHeadersRead,
                        token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    bool callerCancelled = cancellationToken.IsCancellationRequested;
                    bool connectTimeout = timeoutCts != null &&
                                          timeoutCts.IsCancellationRequested &&
                                          !callerCancelled;
                    message.Dispose();
                    if (ownsClient)
                    {
                        client.Dispose();
                    }
                    throw HttpClientErrorClassifier.Classify(ex, callerCancelled, connectTimeout);
                }

                TransportHeaders headers = HttpClientResponseHeaders.From(response);

                if (options.Redirects == RedirectPolicy.Error &&
                    (int)response.StatusCode >= 300 &&
                    (int)response.StatusCode < 400)
                {
                    var redirectError = new TransportException(
                        TransportErrorKind.Protocol,
                        "Redirect not allowed by RedirectPolicy.Error.",
                        null,
                        (int)response.StatusCode,
                        headers,
                        response.ReasonPhrase,
                        false);
                    response.Dispose();
                    message.Dispose();
                    if (ownsClient)
                    {
                        client.Dispose();
                    }
                    throw redirectError;
                }

                Stream inner;
                try
                {
                    inner = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    response.Dispose();
                    message.Dispose();
                    if (ownsClient)
                    {
                        client.Dispose();
                    }
                    throw HttpClientErrorClassifier.Classify(ex, cancellationToken.IsCancellationRequested, false);
                }

                var body = new HttpResponseReadStream(inner, response);
                message.Dispose();
                return new HttpClientSession(
                    response,
                    body,
                    (int)response.StatusCode,
                    headers,
                    client,
                    ownsClient,
                    cancellationToken);
            }
            finally
            {
                linkedCts?.Dispose();
                timeoutCts?.Dispose();
            }
        }
    }
}
