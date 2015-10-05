using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using NUnit.Framework;

namespace Narkhedegs.Tests
{
    [TestFixture]
    public class PipeTest
    {
        [Test]
        public void SimpleTest()
        {
            var pipe = new Pipe();

            pipe.WriteText("abc");
            Assert.AreEqual("abc", pipe.ReadTextAsync(3).Result);

            pipe.WriteText("1");
            pipe.WriteText("2");
            pipe.WriteText("3");
            Assert.AreEqual("123", pipe.ReadTextAsync(3).Result);

            var asyncRead = pipe.ReadTextAsync(100);
            Assert.AreEqual(false, asyncRead.Wait(TimeSpan.FromSeconds(.01)),
                asyncRead.IsCompleted ? "Found: " + (asyncRead.Result ?? "null") : "not complete");
            pipe.WriteText("x");
            Assert.AreEqual(false, asyncRead.Wait(TimeSpan.FromSeconds(.01)),
                asyncRead.IsCompleted ? "Found: " + (asyncRead.Result ?? "null") : "not complete");
            pipe.WriteText(new string('y', 100));
            Assert.AreEqual(true, asyncRead.Wait(TimeSpan.FromSeconds(5)),
                asyncRead.IsCompleted ? "Found: " + (asyncRead.Result ?? "null") : "not complete");
            Assert.AreEqual("x" + new string('y', 99), asyncRead.Result);
        }

        [Test]
        public void TestLargeStreamWithFixedLength()
        {
            var pipe = new Pipe();
            pipe.SetFixedLength();

            var bytes = Enumerable.Range(0, 5*Pipe.ByteBufferSize)
                .Select(b => (byte) (b%256))
                .ToArray();
            var asyncWrite = pipe.InputStream.WriteAsync(bytes, 0, bytes.Length)
                .ContinueWith(_ => pipe.InputStream.Close());
            var memoryStream = new MemoryStream();
            var buffer = new byte[777];
            int bytesRead;
            while ((bytesRead = pipe.OutputStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                memoryStream.Write(buffer, 0, bytesRead);
            }
            Assert.AreEqual(true, asyncWrite.Wait(TimeSpan.FromSeconds(1)));
            Assert.That(memoryStream.ToArray().SequenceEqual(bytes));
        }

        [Test]
        public void TimeoutTest()
        {
            var pipe = new Pipe {OutputStream = {ReadTimeout = 0}};
            Assert.Throws<TimeoutException>(() => pipe.OutputStream.ReadByte());

            pipe.WriteText(new string('a', 2048));
            Assert.AreEqual(new string('a', 2048), pipe.ReadTextAsync(2048).Result);
        }

        [Test]
        public void TestWriteTimeout()
        {
            var pipe = new Pipe {InputStream = {WriteTimeout = 0}};
            pipe.SetFixedLength();
            pipe.WriteText(new string('a', 2*Pipe.ByteBufferSize));
            var asyncWrite = pipe.InputStream.WriteAsync(new byte[1], 0, 1);
            Assert.AreEqual(true, asyncWrite.ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(.01)));
            Assert.AreEqual(true, asyncWrite.IsFaulted);
            Assert.IsInstanceOf<TimeoutException>(asyncWrite.Exception.InnerException);
        }

        [Test]
        public void TestPartialWriteDoesNotTimeout()
        {
            var pipe = new Pipe {InputStream = {WriteTimeout = 0}, OutputStream = {ReadTimeout = 1000}};
            pipe.SetFixedLength();
            var text = Enumerable.Repeat((byte) 't', 3*Pipe.ByteBufferSize).ToArray();
            var asyncWrite = pipe.InputStream.WriteAsync(text, 0, text.Length);
            Assert.AreEqual(false, asyncWrite.Wait(TimeSpan.FromSeconds(.01)));

            Assert.AreEqual(new string(text.Select(b => (char) b).ToArray()), pipe.ReadTextAsync(text.Length).Result);
        }

        [Test]
        public void TestCancel()
        {
            var pipe = new Pipe();
            var cancellationTokenSource = new CancellationTokenSource();

            var asyncRead = pipe.ReadTextAsync(1, cancellationTokenSource.Token);
            Assert.AreEqual(false, asyncRead.Wait(TimeSpan.FromSeconds(.01)));
            cancellationTokenSource.Cancel();
            Assert.AreEqual(true, asyncRead.ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(true, asyncRead.IsCanceled);

            pipe.WriteText("aaa");
            Assert.AreEqual("aa", pipe.ReadTextAsync(2).Result);

            asyncRead = pipe.ReadTextAsync(1, cancellationTokenSource.Token);
            Assert.AreEqual(true, asyncRead.ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(true, asyncRead.IsCanceled);
        }

        [Test]
        public void TestCancelWrite()
        {
            var pipe = new Pipe();
            var cancellationTokenSource = new CancellationTokenSource();

            pipe.WriteText(new string('a', 2*Pipe.ByteBufferSize));

            pipe.SetFixedLength();

            var bytes = Enumerable.Repeat((byte) 'b', Pipe.ByteBufferSize).ToArray();
            var asyncWrite = pipe.InputStream.WriteAsync(bytes, 0, bytes.Length, cancellationTokenSource.Token);
            Assert.AreEqual(false, asyncWrite.Wait(TimeSpan.FromSeconds(.01)));
            cancellationTokenSource.Cancel();
            Assert.AreEqual(true, asyncWrite.ContinueWith(_ => { }).Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(true, asyncWrite.IsCanceled);
        }

        [Test]
        public void TestPartialWriteDoesNotCancel()
        {
            var pipe = new Pipe();
            var cancellationTokenSource = new CancellationTokenSource();

            pipe.WriteText(new string('a', 2*Pipe.ByteBufferSize));

            var asyncRead = pipe.ReadTextAsync(1);
            Assert.AreEqual(true, asyncRead.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual("a", asyncRead.Result);

            pipe.SetFixedLength();

            var bytes = Enumerable.Repeat((byte) 'b', Pipe.ByteBufferSize).ToArray();
            var asyncWrite = pipe.InputStream.WriteAsync(bytes, 0, bytes.Length, cancellationTokenSource.Token);
            Assert.AreEqual(false, asyncWrite.Wait(TimeSpan.FromSeconds(.01)));
            cancellationTokenSource.Cancel();
            Assert.AreEqual(false, asyncWrite.Wait(TimeSpan.FromSeconds(.01)));

            asyncRead = pipe.ReadTextAsync((3*Pipe.ByteBufferSize) - 1);
            Assert.AreEqual(true, asyncRead.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(
                new string('a', (2*Pipe.ByteBufferSize) - 1) + new string('b', Pipe.ByteBufferSize),
                asyncRead.Result);
        }

        [Test]
        public void TestCloseWriteSide()
        {
            var pipe = new Pipe();
            pipe.WriteText("123456");
            pipe.InputStream.Close();
            Assert.Throws<ObjectDisposedException>(() => pipe.InputStream.WriteByte(1));

            Assert.AreEqual("12345", pipe.ReadTextAsync(5).Result);
            Assert.AreEqual(null, pipe.ReadTextAsync(2).Result);

            pipe = new Pipe();
            var asyncRead = pipe.ReadTextAsync(1);
            Assert.AreEqual(false, asyncRead.Wait(TimeSpan.FromSeconds(.01)));
            pipe.InputStream.Close();
            Assert.AreEqual(true, asyncRead.Wait(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void TestCloseReadSide()
        {
            var pipe = new Pipe();
            pipe.WriteText("abc");
            Assert.AreEqual("ab", pipe.ReadTextAsync(2).Result);
            pipe.OutputStream.Close();
            Assert.Throws<ObjectDisposedException>(() => pipe.OutputStream.ReadByte());

            var largeBytes = new byte[10*1024];
            var initialMemory = GC.GetTotalMemory(forceFullCollection: true);
            for (var i = 0; i < int.MaxValue/1024; ++i)
            {
                pipe.InputStream.Write(largeBytes, 0, largeBytes.Length);
            }
            var finalMemory = GC.GetTotalMemory(forceFullCollection: true);

            Assert.IsTrue(finalMemory - initialMemory < 10*largeBytes.Length,
                "final = " + finalMemory + " initial = " + initialMemory);

            Assert.Throws<ObjectDisposedException>(() => pipe.OutputStream.ReadByte());

            pipe.InputStream.Close();
        }

        [Test]
        public void TestConcurrentReads()
        {
            var pipe = new Pipe();

            var asyncRead = pipe.ReadTextAsync(1);
            Assert.Throws<InvalidOperationException>(() => pipe.OutputStream.ReadByte());
            pipe.InputStream.Close();
            Assert.AreEqual(true, asyncRead.Wait(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void TestConcurrentWrites()
        {
            var pipe = new Pipe();
            pipe.SetFixedLength();

            var longText = new string('x', (2*Pipe.ByteBufferSize) + 1);
            var asyncWrite = pipe.WriteTextAsync(longText);
            Assert.AreEqual(false, asyncWrite.Wait(TimeSpan.FromSeconds(.01)));
            Assert.Throws<InvalidOperationException>(() => pipe.InputStream.WriteByte(101));
            pipe.OutputStream.Close();
            Assert.AreEqual(true, asyncWrite.Wait(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void TestChainedPipes()
        {
            var pipes = CreatePipeChain(100);

            // short write
            pipes[0].InputStream.WriteByte(100);
            var buffer = new byte[1];
            Assert.AreEqual(true, pipes.Last().OutputStream.ReadAsync(buffer, 0, buffer.Length)
                .Wait(TimeSpan.FromSeconds(5)));

            Assert.AreEqual((byte) 100, buffer[0]);

            // long write
            var longText = new string('y', 3*Pipe.CharBufferSize);
            var asyncWrite = pipes[0].WriteTextAsync(longText);
            Assert.AreEqual(true, asyncWrite.Wait(TimeSpan.FromSeconds(5)));
            var asyncRead = pipes.Last().ReadTextAsync(longText.Length);
            Assert.AreEqual(true, asyncRead.Wait(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(longText, asyncRead.Result);
        }

        [Test]
        public void TestPipeChainWithFixedLengthPipes()
        {
            var pipes = CreatePipeChain(2);
            // note that this needs to be >> larger than the capacity to block all the pipes,
            // since we can store bytes in each pipe in the chain + each buffer between pipes
            var longText = new string('z', 8*Pipe.ByteBufferSize + 1);
            pipes.ForEach(p => p.SetFixedLength());
            var asyncWrite = pipes[0].WriteTextAsync(longText);
            Assert.AreEqual(false, asyncWrite.Wait(TimeSpan.FromSeconds(.01)));
            var asyncRead = pipes.Last().ReadTextAsync(longText.Length);
            Assert.AreEqual(true, asyncWrite.Wait(TimeSpan.FromSeconds(10)));
            Assert.AreEqual(true, asyncRead.Wait(TimeSpan.FromSeconds(10)));
            Assert.IsNotNull(asyncRead.Result);
            Assert.AreEqual(longText.Length, asyncRead.Result.Length);
            Assert.AreEqual(longText, asyncRead.Result);
        }

        private static List<Pipe> CreatePipeChain(int length)
        {
            var pipes = Enumerable.Range(0, length).Select(_ => new Pipe())
                .ToList();
            for (var i = 0; i < pipes.Count - 1; ++i)
            {
                var fromPipe = pipes[i];
                var toPipe = pipes[i + 1];
                fromPipe.OutputStream.CopyToAsync(toPipe.InputStream)
                    .ContinueWith(_ =>
                    {
                        fromPipe.OutputStream.Close();
                        toPipe.InputStream.Close();
                    });
            }

            return pipes;
        }

        [Test]
        public void FuzzTest()
        {
            const int ByteCount = 100000;

            var pipe = new Pipe();
            var writeTask = Task.Run(async () =>
            {
                var memoryStream = new MemoryStream();
                var random = new Random(1234);
                var bytesWritten = 0;
                while (bytesWritten < ByteCount)
                {
                    //Console.WriteLine("Writing " + memoryStream.Length);
                    switch (random.Next(10))
                    {
                        case 1:
                            //Console.WriteLine("SETTING FIXED LENGTH");
                            pipe.SetFixedLength();
                            break;
                        case 2:
                        case 3:
                            await Task.Delay(1);
                            break;
                        default:
                            var bufferLength = random.Next(0, 5000);
                            var offset = random.Next(0, bufferLength + 1);
                            var count = random.Next(0, bufferLength - offset + 1);
                            var buffer = new byte[bufferLength];
                            random.NextBytes(buffer);
                            memoryStream.Write(buffer, offset, count);
                            //Console.WriteLine("WRITE START");
                            await pipe.InputStream.WriteAsync(buffer, offset, count);
                            //Console.WriteLine("WRITE END");
                            bytesWritten += count;
                            break;
                    }
                }
                //Console.WriteLine("WRITER ALL DONE");
                pipe.InputStream.Close();
                return memoryStream;
            });

            var readTask = Task.Run(async () =>
            {
                var memoryStream = new MemoryStream();
                var random = new Random(5678);
                while (true)
                {
                    //Console.WriteLine("Reading " + memoryStream.Length);
                    if (random.Next(10) == 1)
                    {
                        await Task.Delay(1);
                    }
                    var bufferLength = random.Next(0, 5000);
                    var offset = random.Next(0, bufferLength + 1);
                    var count = random.Next(0, bufferLength - offset + 1);
                    var buffer = new byte[bufferLength];
                    //Console.WriteLine("READ START");
                    var bytesRead = await pipe.OutputStream.ReadAsync(buffer, offset, count);
                    //Console.WriteLine("READ END");
                    if (bytesRead == 0 && count > 0)
                    {
                        // if we tried to read more than 1 byte and we got 0, the pipe is done
                        //Console.WriteLine("READER ALL DONE");
                        return memoryStream;
                    }
                    memoryStream.Write(buffer, offset, bytesRead);
                }
            });

            Assert.AreEqual(true, Task.WhenAll(writeTask, readTask).Wait(TimeSpan.FromSeconds(5)));

            CollectionAssert.AreEqual(writeTask.Result.ToArray(), readTask.Result.ToArray());
        }
    }

    internal static class PipeExtensions
    {
        public static void WriteText(this Pipe @this, string text)
        {
            new StreamWriter(@this.InputStream) {AutoFlush = true}.Write(text);
        }

        public static Task WriteTextAsync(this Pipe @this, string text)
        {
            return new StreamWriter(@this.InputStream) {AutoFlush = true}.WriteAsync(text);
        }

        public static async Task<string> ReadTextAsync(this Pipe @this, int count,
            CancellationToken token = default(CancellationToken))
        {
            var bytes = new byte[count];
            var bytesRead = 0;
            while (bytesRead < count)
            {
                var result =
                    await
                        @this.OutputStream.ReadAsync(bytes, offset: bytesRead, count: count - bytesRead,
                            cancellationToken: token);
                if (result == 0)
                {
                    return null;
                }
                bytesRead += result;
            }

            return new string(Encoding.Default.GetChars(bytes));
        }
    }
}