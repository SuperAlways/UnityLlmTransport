using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class ChunkQueueReadStreamTests
    {
        private static byte[] Bytes(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        private static async Task<string> ReadAllAsync(Stream stream, int bufferSize = 3)
        {
            var buffer = new byte[bufferSize];
            var builder = new StringBuilder();
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), CancellationToken.None)) > 0)
            {
                builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            return builder.ToString();
        }

        [Fact]
        public async Task Reads_Across_Chunks_With_Partial_Buffers()
        {
            var queue = new ChunkQueue(1024);
            queue.TryWrite(Bytes("你"), 3);
            queue.TryWrite(Bytes("好"), 3);
            queue.Complete();

            var stream = new ChunkQueueReadStream(queue);
            Assert.Equal("你好", await ReadAllAsync(stream));
        }

        [Fact]
        public async Task Complete_With_Data_Drains_Then_Returns_Zero()
        {
            var queue = new ChunkQueue(1024);
            queue.TryWrite(Bytes("ab"), 2);
            queue.Complete();

            var stream = new ChunkQueueReadStream(queue);
            Assert.Equal("ab", await ReadAllAsync(stream, 1));
        }

        [Fact]
        public async Task Completion_Error_Is_Thrown_By_Read()
        {
            var queue = new ChunkQueue(1024);
            var failure = new InvalidOperationException("boom");
            queue.Abort(failure);

            var stream = new ChunkQueueReadStream(queue);
            var buffer = new byte[16];
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => stream.ReadAsync(buffer.AsMemory(), CancellationToken.None).AsTask());
            Assert.Same(failure, error);
        }

        [Fact]
        public async Task Cancellation_Propagates()
        {
            var queue = new ChunkQueue(1024);
            var stream = new ChunkQueueReadStream(queue);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => stream.ReadAsync(new byte[16].AsMemory(), cts.Token).AsTask());
        }

        [Fact]
        public async Task Sync_Read_Throws()
        {
            var queue = new ChunkQueue(1024);
            var stream = new ChunkQueueReadStream(queue);
            await Task.Yield();

            // xunit 2.9 对值返回 lambda 走过时(错误级)重载，改语句 lambda 走 Action 重载，语义不变。
            Assert.Throws<NotSupportedException>(() => { stream.Read(new byte[16], 0, 16); });
        }

        [Fact]
        public async Task Dispose_Then_Read_Throws()
        {
            var queue = new ChunkQueue(1024);
            var stream = new ChunkQueueReadStream(queue);
            stream.Dispose();

            var error = await Assert.ThrowsAsync<ObjectDisposedException>(
                () => stream.ReadAsync(new byte[16].AsMemory(), CancellationToken.None).AsTask());
            Assert.Equal(nameof(ChunkQueueReadStream), error.ObjectName);
        }

        [Fact]
        public async Task Slow_Producer_No_Deadlock()
        {
            var queue = new ChunkQueue(64);
            var writer = Task.Run(async () =>
            {
                for (int i = 0; i < 10; i++)
                {
                    queue.TryWrite(Bytes("x"), 1);
                    await Task.Delay(20).ConfigureAwait(false);
                }
                queue.Complete();
            });

            var stream = new ChunkQueueReadStream(queue);
            Assert.Equal("xxxxxxxxxx", await ReadAllAsync(stream, 4));
            await writer;
        }

        [Fact]
        public async Task Empty_Buffer_Returns_Zero()
        {
            var queue = new ChunkQueue(1024);
            queue.TryWrite(Bytes("a"), 1);
            var stream = new ChunkQueueReadStream(queue);

            // Stream.ReadAsync 只收 Memory<byte>（ReadOnlyMemory 无隐式转换），改用 Memory<byte>.Empty，语义不变。
            Assert.Equal(0, await stream.ReadAsync(Memory<byte>.Empty, CancellationToken.None));
        }
    }
}
