namespace UnityLlmTransport
{
    public interface ITransportSelector
    {
        TransportSelection Select(TransportSelectionContext context);
    }
}
