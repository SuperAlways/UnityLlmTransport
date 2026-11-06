extern alias VendoredSse;
using VendoredSse::System.Net.ServerSentEvents;
using System.Text;
using System.Threading.Channels;
using Xunit;

public class ChannelReadStreamTests
{
    [Fact]
    public async Task Parser_Consumes_Chunks_Across_Boundaries()
    {
        var channel = Channel.CreateUnbounded<byte[]>();
        var raw = Encoding.UTF8.GetBytes("data: 你\r\n\r\ndata: 好\r\n\r\n");
        foreach (var b in raw) channel.Writer.TryWrite(new[] { b });
        channel.Writer.TryComplete();

        using var stream = new ChannelReadStream(channel.Reader);
        var items = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            items.Add(item);

        Assert.Equal(2, items.Count);
        Assert.Equal("你", items[0].Data);
        Assert.Equal("好", items[1].Data);
    }
}
