using System.IO.Compression;
using System.Net;
using System.Text;

var listener = new HttpListener();
listener.Prefixes.Add("http://127.0.0.1:5199/");
listener.Start();
Console.WriteLine("SSE server: http://127.0.0.1:5199/sse | /sse-error | /sse-gzip");

while (true)
{
    var ctx = await listener.GetContextAsync();
    var path = ctx.Request.Url!.AbsolutePath;
    if (path is not ("/sse" or "/sse-error" or "/sse-gzip"))
    {
        ctx.Response.StatusCode = 404;
        ctx.Response.Close();
        continue;
    }

    try
    {
    ctx.Response.ContentType = "text/event-stream";
    ctx.Response.SendChunked = true;
    var output = ctx.Response.OutputStream;

    if (path == "/sse-error")
    {
        ctx.Response.StatusCode = 500;
        await output.WriteAsync(Encoding.UTF8.GetBytes("{\"error\":\"boom\"}"));
        ctx.Response.Close();
        continue;
    }

    if (path == "/sse-gzip")
    {
        ctx.Response.Headers["Content-Encoding"] = "gzip";
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            foreach (var part in new[] { "你", "好", "，", "世", "界" })
            {
                var payload = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{part}\"}}}}]}}\n\n";
                await gzip.WriteAsync(Encoding.UTF8.GetBytes(payload));
                await gzip.FlushAsync();
                await output.FlushAsync();
                await Task.Delay(100);
            }
            await gzip.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
            await gzip.FlushAsync();
        }
        await output.FlushAsync();
        ctx.Response.Close();
        continue;
    }

    foreach (var part in new[] { "你", "好", "，", "世", "界" })
    {
        var payload = $"data: {{\"choices\":[{{\"delta\":{{\"content\":\"{part}\"}}}}]}}\n\n";
        await output.WriteAsync(Encoding.UTF8.GetBytes(payload));
        await output.FlushAsync();
        await Task.Delay(100);
    }

    await output.WriteAsync(Encoding.UTF8.GetBytes("data: [DONE]\n\n"));
    ctx.Response.Close();
    }
    catch (Exception ex)
    {
        Console.WriteLine("request error: " + ex.Message);
        try { ctx.Response.Abort(); } catch { }
    }
}
