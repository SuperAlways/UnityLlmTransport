namespace UnityLlmTransport
{
    public sealed record UwrResponseSnapshot(
        UwrCompletion Completion,
        int StatusCode,
        TransportHeaders Headers,
        string? Error);
}
