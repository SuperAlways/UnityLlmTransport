namespace UnityLlmTransport
{
    internal static class AuthHeaders
    {
        public static bool ShouldInject(TransportRequest request, string headerName)
        {
            return !request.Headers.ContainsKey(headerName);
        }

        public static TransportRequest WithHeader(TransportRequest request, string headerName, string value)
        {
            var headers = new TransportHeaders();
            foreach (var pair in request.Headers)
            {
                headers[pair.Key] = pair.Value;
            }
            headers[headerName] = value;
            return request with { Headers = headers };
        }
    }
}
