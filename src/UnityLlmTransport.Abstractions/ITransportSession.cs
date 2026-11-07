using System;
using System.IO;
using System.Threading.Tasks;

namespace UnityLlmTransport
{
    public interface ITransportSession : IAsyncDisposable
    {
        int StatusCode { get; }
        TransportHeaders Headers { get; }
        Stream Body { get; }
        void Abort();
    }
}
