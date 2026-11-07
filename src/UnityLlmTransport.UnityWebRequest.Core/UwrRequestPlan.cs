namespace UnityLlmTransport
{
    public sealed record UwrRequestPlan(
        TransportHeaders Headers,
        int RedirectLimit,
        bool AcceptAllCertificates);
}
