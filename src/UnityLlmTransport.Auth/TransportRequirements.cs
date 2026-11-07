namespace UnityLlmTransport
{
    public static class TransportRequirements
    {
        public static void RequireRequestFinalizer(TransportCapabilities capabilities)
        {
            if ((capabilities & TransportCapabilities.RequestFinalizer) == 0)
            {
                throw new TransportException(
                    TransportErrorKind.Unsupported,
                    "Transport does not support request finalization; use a transport that declares RequestFinalizer.");
            }
        }
    }
}
