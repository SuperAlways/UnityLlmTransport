extern alias SseVendored;
using SseVendored::System.Net.ServerSentEvents;
using System.Text;
using Xunit;

public class SseParserSmokeTests
{
    private static async Task<List<SseItem<string>>> ParseAsync(string raw)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(raw));
        var items = new List<SseItem<string>>();
        await foreach (var item in SseParser.Create(stream).EnumerateAsync())
            items.Add(item);
        return items;
    }

    [Fact]
    public async Task MultiLine_Comment_Fields()
    {
        var items = await ParseAsync(": hi\r\nid: 1\r\nevent: delta\r\ndata: hello\r\ndata: world\r\n\r\n");
        Assert.Single(items);
        Assert.Equal("hello\nworld", items[0].Data);
        Assert.Equal("delta", items[0].EventType);
        Assert.Equal("1", items[0].EventId);
    }

    [Fact]
    public async Task Lf_And_Done()
    {
        var items = await ParseAsync("data: a\n\ndata: [DONE]\n\n");
        Assert.Equal(2, items.Count);
        Assert.Equal("[DONE]", items[1].Data);
    }

    [Fact]
    public async Task Cr_Only()
    {
        var items = await ParseAsync("data: a\r\rdata: b\r\r");
        Assert.Equal(2, items.Count);
        Assert.Equal("a", items[0].Data);
        Assert.Equal("b", items[1].Data);
    }

    [Fact]
    public async Task Eof_Half_Event_Is_Dropped()
    {
        var items = await ParseAsync("data: a\n");
        Assert.Empty(items);
    }
}
