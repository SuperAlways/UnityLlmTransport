using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityLlmTransport;
using Xunit;

namespace UnityLlmTransport.Tests
{
    public class ChunkQueueTests
    {
        private static byte[] Bytes(string text)
        {
            return Encoding.UTF8.GetBytes(text);
        }

        [Fact]
        public void Write_And_Read_Preserves_Order()
        {
            var queue = new ChunkQueue(1024);
            Assert.True(queue.TryWrite(Bytes("a"), 1));
            Assert.True(queue.TryWrite(Bytes("b"), 1));

            Assert.True(queue.TryRead(out var first));
            Assert.Equal("a", Encoding.UTF8.GetString(first.ToArray()));
            Assert.True(queue.TryRead(out var second));
            Assert.Equal("b", Encoding.UTF8.GetString(second.ToArray()));
            Assert.False(queue.TryRead(out _));
        }

        [Fact]
        public void BufferedBytes_Tracks_Reads_And_Writes()
        {
            var queue = new ChunkQueue(1024);
            queue.TryWrite(Bytes("aa"), 2);
            queue.TryWrite(Bytes("bbb"), 3);

            Assert.Equal(5, queue.BufferedBytes);
            Assert.True(queue.TryRead(out _));
            Assert.Equal(3, queue.BufferedBytes);
        }

        [Fact]
        public void Overflow_Terminates_Queue_With_Overflow_Error()
        {
            var queue = new ChunkQueue(4);
            Assert.True(queue.TryWrite(Bytes("abc"), 3));
            Assert.False(queue.TryWrite(Bytes("def"), 3));

            Assert.False(queue.TryRead(out _));
            var error = Assert.IsType<TransportException>(queue.CompletionError);
            Assert.Equal(TransportErrorKind.Overflow, error.Kind);
            Assert.False(queue.TryWrite(Bytes("x"), 1));
        }

        [Fact]
        public async Task Complete_Allows_Draining_Then_Wait_Returns_False()
        {
            var queue = new ChunkQueue(1024);
            queue.TryWrite(Bytes("a"), 1);
            queue.TryWrite(Bytes("b"), 1);
            queue.Complete();

            Assert.True(queue.TryRead(out _));
            Assert.True(queue.TryRead(out _));
            Assert.False(await queue.WaitToReadAsync(CancellationToken.None));
        }

        [Fact]
        public async Task Abort_Discards_Buffered_And_Reports_Error()
        {
            var queue = new ChunkQueue(1024);
            queue.TryWrite(Bytes("a"), 1);
            var failure = new InvalidOperationException("boom");
            queue.Abort(failure);

            Assert.True(await queue.WaitToReadAsync(CancellationToken.None) == false);
            Assert.Same(failure, queue.CompletionError);
            Assert.False(queue.TryRead(out _));
        }

        [Fact]
        public async Task WaitToRead_Returns_True_When_Data_Is_Written()
        {
            var queue = new ChunkQueue(1024);
            var waiter = Task.Run(async () => await queue.WaitToReadAsync(CancellationToken.None));
            await Task.Delay(50);

            queue.TryWrite(Bytes("a"), 1);
            Assert.True(await waiter);
        }

        [Fact]
        public async Task WaitToRead_Honors_Cancellation()
        {
            var queue = new ChunkQueue(1024);
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                async () => await queue.WaitToReadAsync(cts.Token));
        }

        [Fact]
        public async Task Background_Writer_Does_Not_Lose_Data()
        {
            var queue = new ChunkQueue(64);
            var writer = Task.Run(async () =>
            {
                for (int i = 0; i < 32; i++)
                {
                    while (!queue.TryWrite(new byte[] { (byte)i }, 1))
                    {
                        await Task.Delay(1).ConfigureAwait(false);
                    }
                }
                queue.Complete();
            });

            var received = new List<byte>();
            while (await queue.WaitToReadAsync(CancellationToken.None))
            {
                while (queue.TryRead(out var chunk))
                {
                    received.Add(chunk.ToArray()[0]);
                }
            }

            await writer;
            Assert.Equal(32, received.Count);
        }
    }
}
