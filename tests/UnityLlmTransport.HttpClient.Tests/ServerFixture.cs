using System.Threading.Tasks;
using Phase3TestSupport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public sealed class ServerFixture : IAsyncLifetime
    {
        public Phase3TestServer Server { get; private set; } = null!;

        public Task InitializeAsync()
        {
            Server = Phase3TestServer.Start(5211, 5243, 5244);
            return Task.CompletedTask;
        }

        public Task DisposeAsync()
        {
            return Server.DisposeAsync().AsTask();
        }
    }

    [CollectionDefinition("phase3-server")]
    public class Phase3ServerCollection : ICollectionFixture<ServerFixture> { }
}
