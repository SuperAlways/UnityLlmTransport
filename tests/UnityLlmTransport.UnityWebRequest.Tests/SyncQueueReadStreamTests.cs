using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class SyncQueueReadStreamTests
    {
        private static byte[] Bytes(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        [Fact]
        public void Reads_Across_Chunks_With_Partial_Buffers()
        {
            var queue = new ChunkQueue(1024);
            queue.TryWrite(Bytes("abc"), 3);
            queue.TryWrite(Bytes("def"), 3);
            queue.Complete();

            using var stream = new SyncQueueReadStream(queue);
            var buffer = new byte[2];
            Assert.Equal(2, stream.Read(buffer, 0, 2));
            Assert.Equal("ab", Encoding.UTF8.GetString(buffer, 0, 2));
            Assert.Equal(1, stream.Read(buffer, 0, 2));
            Assert.Equal("c", Encoding.UTF8.GetString(buffer, 0, 1));
            Assert.Equal(2, stream.Read(buffer, 0, 2));
            Assert.Equal("de", Encoding.UTF8.GetString(buffer, 0, 2));
            Assert.Equal(1, stream.Read(buffer, 0, 2));
            Assert.Equal("f", Encoding.UTF8.GetString(buffer, 0, 1));
            Assert.Equal(0, stream.Read(buffer, 0, 2));
        }

        [Fact]
        public async Task Read_Blocks_Until_Data_Arrives_From_Another_Thread()
        {
            var queue = new ChunkQueue(1024);
            using var stream = new SyncQueueReadStream(queue);

            var reader = Task.Run(() =>
            {
                var buffer = new byte[16];
                var builder = new StringBuilder();
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                }
                return builder.ToString();
            });

            await Task.Delay(100);
            Assert.False(reader.IsCompleted);
            queue.TryWrite(Bytes("ab"), 2);
            queue.TryWrite(Bytes("c"), 1);
            queue.Complete();
            Assert.Equal("abc", await reader);
        }

        [Fact]
        public async Task Read_Returns_Zero_On_Clean_Complete_While_Waiting()
        {
            var queue = new ChunkQueue(1024);
            using var stream = new SyncQueueReadStream(queue);

            var reader = Task.Run(() =>
            {
                var buffer = new byte[4];
                return stream.Read(buffer, 0, 4);
            });

            await Task.Delay(100);
            Assert.False(reader.IsCompleted);
            queue.Complete();
            Assert.Equal(0, await reader);
        }

        [Fact]
        public async Task Read_Throws_Queued_Error_On_Abort_While_Waiting()
        {
            var queue = new ChunkQueue(1024);
            using var stream = new SyncQueueReadStream(queue);

            var reader = Task.Run(() =>
            {
                var buffer = new byte[4];
                return stream.Read(buffer, 0, 4);
            });

            await Task.Delay(100);
            Assert.False(reader.IsCompleted);
            queue.Abort(new TransportException(TransportErrorKind.Aborted, "killed"));
            var error = await Assert.ThrowsAsync<TransportException>(() => reader);
            Assert.Equal(TransportErrorKind.Aborted, error.Kind);
        }

        [Fact]
        public async Task ReadAsync_Is_Rejected_Explicitly()
        {
            var queue = new ChunkQueue(1024);
            using var stream = new SyncQueueReadStream(queue);
            await Assert.ThrowsAsync<NotSupportedException>(() =>
                stream.ReadAsync(new byte[4].AsMemory(), CancellationToken.None).AsTask());
        }

        [Fact]
        public async Task Gzip_Chain_Decompresses_Through_Offload()
        {
            // 复刻会话装配（真 gzip 字节）：AsyncOffloadStream → GzipSniffReadStream → SyncQueueReadStream。
            var raw = new ChunkQueue(64 * 1024);
            var body = new AsyncOffloadStream(new GzipSniffReadStream(new SyncQueueReadStream(raw)));

            var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            {
                foreach (var part in new[] { "你", "好", "，", "世", "界" })
                {
                    await writer.WriteAsync("data: " + part + "\n\n");
                    await writer.FlushAsync();
                }
            }
            var payload = compressed.ToArray();
            Assert.Equal(0x1f, payload[0]);
            Assert.Equal(0x8b, payload[1]);

            await FeedAllAsync(raw, payload, 7);
            Assert.Equal(
                "data: 你\n\ndata: 好\n\ndata: ，\n\ndata: 世\n\ndata: 界\n\n",
                await DrainAsync(body));
        }

        [Fact]
        public async Task Plain_Body_Passes_Through_When_Header_Says_Gzip()
        {
            // UWR(2021.3) 已自动解压 gzip，但响应头仍带 Content-Encoding: gzip——
            // 队列里其实是明文，嗅探后必须原样透传。
            var raw = new ChunkQueue(64 * 1024);
            var body = new AsyncOffloadStream(new GzipSniffReadStream(new SyncQueueReadStream(raw)));

            var plain = Encoding.UTF8.GetBytes("data: 你\n\ndata: [DONE]\n\n");
            Assert.NotEqual(0x1f, plain[0]);
            await FeedAllAsync(raw, plain, 7);
            Assert.Equal("data: 你\n\ndata: [DONE]\n\n", await DrainAsync(body));
        }

        [Fact]
        public async Task Sniff_Handles_Header_Split_Across_Small_Chunks()
        {
            var raw = new ChunkQueue(64 * 1024);
            var body = new AsyncOffloadStream(new GzipSniffReadStream(new SyncQueueReadStream(raw)));

            var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            using (var writer = new StreamWriter(gzip, new UTF8Encoding(false)))
            {
                await writer.WriteAsync("hello");
            }
            var payload = compressed.ToArray();

            // gzip 头逐字节喂入，嗅探循环必须凑齐 2 字节再判定。
            await FeedAllAsync(raw, payload, 1);
            Assert.Equal("hello", await DrainAsync(body));
        }

        [Fact]
        public async Task Short_Body_Passes_Through_Without_Sniff_Match()
        {
            var raw = new ChunkQueue(64 * 1024);
            var body = new AsyncOffloadStream(new GzipSniffReadStream(new SyncQueueReadStream(raw)));

            await FeedAllAsync(raw, Encoding.UTF8.GetBytes("{"), 1);
            Assert.Equal("{", await DrainAsync(body));
        }

        private static async Task FeedAllAsync(ChunkQueue raw, byte[] payload, int chunkSize)
        {
            var feed = Task.Run(async () =>
            {
                foreach (var chunk in SliceChunks(payload, chunkSize))
                {
                    raw.TryWrite(chunk, chunk.Length);
                    await Task.Yield();
                }
                raw.Complete();
            });
            await feed;
        }

        private static async Task<string> DrainAsync(Stream body)
        {
            var buffer = new byte[64];
            var decoded = new MemoryStream();
            int read;
            while ((read = await body.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                await decoded.WriteAsync(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(decoded.ToArray());
        }

        private static IEnumerable<byte[]> SliceChunks(byte[] payload, int size)
        {
            for (int i = 0; i < payload.Length; i += size)
            {
                int n = Math.Min(size, payload.Length - i);
                var chunk = new byte[n];
                Array.Copy(payload, i, chunk, 0, n);
                yield return chunk;
            }
        }
    }
}
