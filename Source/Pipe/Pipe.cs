using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace Narkhedegs
{
    public sealed class Pipe
    {
        public const int ByteBufferSize = 1024;

        public const int CharBufferSize = ByteBufferSize / sizeof(char);

        private const int MinimumSize = 256;

        /// <summary>
        /// The maximum size at which the pipe will be left empty. Using 2 * <see cref="ByteBufferSize"/>
        /// helps prevent thrashing if data is being pushed through the pipe at that rate.
        /// </summary>
        private const int MaximumStableSize = 2 * ByteBufferSize;

        /// <summary>
        /// Task that has already completed successfully.
        /// </summary>
        private static readonly Task<int> CompletedTask = Task.FromResult(0);

        /// <summary>
        /// Empty byte array.
        /// </summary>
        private static readonly byte[] Empty = new byte[0];

        private readonly SemaphoreSlim _bytesAvailableSignal = new SemaphoreSlim(0, 1);
        private readonly object _lock = new object();
        private readonly PipeInputStream _input;
        private readonly PipeOutputStream _output;

        private byte[] _buffer = Empty;
        private int _start, _count;
        private bool _writerClosed, _readerClosed;
        private SemaphoreSlim _spaceAvailableSignal;
        private Task<int> _readTask = CompletedTask;
        private Task _writeTask = CompletedTask;

        public Pipe()
        {
            _input = new PipeInputStream(this);
            _output = new PipeOutputStream(this);
        }

        public Stream InputStream => _input;
        public Stream OutputStream => _output;

        #region ---- Signals ----
        public void SetFixedLength()
        {
            lock (_lock)
            {
                if (_spaceAvailableSignal == null
                    && !_readerClosed
                    && !_writerClosed)
                {
                    _spaceAvailableSignal = new SemaphoreSlim(GetSpaceAvailableNoLock() > 0 ? 1 : 0, 1
                    );
                }
            }
        }

        private int GetSpaceAvailableNoLock()
        {
            return Math.Max(_buffer.Length, MaximumStableSize) - _count;
        }

        private void UpdateSignalsNoLock()
        {
            // update bytes available
            switch (_bytesAvailableSignal.CurrentCount)
            {
                case 0:
                    if (_count > 0 || _writerClosed)
                    {
                        _bytesAvailableSignal.Release();
                    }
                    break;
                case 1:
                    if (_count == 0 && !_writerClosed)
                    {
                        _bytesAvailableSignal.Wait();
                    }
                    break;
                default:
                    throw new InvalidOperationException("Should never get here.");
            }

            // update space available
            if (_spaceAvailableSignal != null)
            {
                switch (_spaceAvailableSignal.CurrentCount)
                {
                    case 0:
                        if (_readerClosed || GetSpaceAvailableNoLock() > 0)
                        {
                            _spaceAvailableSignal.Release();
                        }
                        break;
                    case 1:
                        if (!_readerClosed && GetSpaceAvailableNoLock() == 0)
                        {
                            _spaceAvailableSignal.Wait();
                        }
                        break;
                    default:
                        throw new InvalidOperationException("Should never get here.");
                }
            }
        }
        #endregion

        #region ---- Writing ----
        private Task WriteAsync(byte[] buffer, int offset, int count, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ValidateBuffer(buffer, offset, count);

            // always respect cancellation, even in the sync flow
            if (cancellationToken.IsCancellationRequested)
            {
                return CreateCanceledTask();
            }

            if (count == 0)
            {
                // if we didn't want to write anything, return immediately
                return CompletedTask;
            }

            lock (_lock)
            {
                if (_writerClosed)
                {
                    throw new ObjectDisposedException("Writer", "The write side of the pipe is closed.");
                }

                if (!_writeTask.IsCompleted)
                {
                    throw new InvalidOperationException("Concurrent writes are not allowed.");
                }

                if (_readerClosed)
                {
                    // if we can't read, just throw away the bytes since no one can observe them anyway
                    return CompletedTask;
                }

                if (_spaceAvailableSignal == null
                    || GetSpaceAvailableNoLock() >= count)
                {
                    // if we're not limited by space, just write and return
                    WriteNoLock(buffer, offset, count);

                    return CompletedTask;
                }

                // otherwise, create and return an async write task
                return _writeTask = WriteNoLockAsync(buffer, offset, count, timeout, cancellationToken);
            }
        }

        private async Task WriteNoLockAsync(byte[] buffer, int offset, int count, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var remainingCount = count;
            do
            {
                // MA: we only use the timeout/token on the first time through, to avoid doing part of the write. This way, it's all or nothing
                CancellationToken cancellationTokenToUse;
                TimeSpan timeoutToUse;
                if (remainingCount == count)
                {
                    timeoutToUse = timeout;
                    cancellationTokenToUse = cancellationToken;
                }
                else
                {
                    timeoutToUse = Timeout.InfiniteTimeSpan;
                    cancellationTokenToUse = CancellationToken.None;
                }

                // acquire the semaphore
                var acquired = await _spaceAvailableSignal.WaitAsync(timeoutToUse, cancellationTokenToUse).ConfigureAwait(false);
                if (!acquired)
                {
                    throw new TimeoutException("Timed out writing to the pipe.");
                }

                // we need to reacquire the lock after the await since we might have switched threads
                lock (_lock)
                {
                    if (_readerClosed)
                    {
                        // if the read side is gone, we're instantly done
                        remainingCount = 0;
                    }
                    else
                    {
                        var countToWrite = Math.Min(GetSpaceAvailableNoLock(), remainingCount);
                        WriteNoLock(buffer, offset + (count - remainingCount), countToWrite);

                        remainingCount -= countToWrite;
                    }
                }
            } while (remainingCount > 0);
        }

        private void WriteNoLock(byte[] buffer, int offset, int count)
        {
            if (count <= 0)
            {
                throw new InvalidOperationException("Sanity check: WriteNoLock requires positive count.");
            }

            EnsureCapacityNoLock(unchecked(_count + count));

            var writeStart = (_start + _count) % _buffer.Length;
            var writeStartToEndCount = Math.Min(_buffer.Length - writeStart, count);
            Buffer.BlockCopy(buffer, offset, _buffer, writeStart, writeStartToEndCount);
            Buffer.BlockCopy(buffer, offset + writeStartToEndCount, _buffer, 0, count - writeStartToEndCount);
            _count += count;

            UpdateSignalsNoLock();
        }

        private void EnsureCapacityNoLock(int capacity)
        {
            if (capacity < 0)
            {
                throw new IOException("Pipe stream is too long.");
            }

            var currentCapacity = _buffer.Length;
            if (capacity <= currentCapacity)
            {
                return;
            }

            if (_spaceAvailableSignal != null
                && capacity > MaximumStableSize)
            {
                throw new InvalidOperationException("Sanity check: pipe should not attempt to expand beyond stable size in fixed length mode.");
            }

            int newCapacity;
            if (currentCapacity < MinimumSize)
            {
                newCapacity = Math.Max(capacity, MinimumSize);
            }
            else
            {
                var doubleCapacity = 2L * currentCapacity;
                newCapacity = capacity >= doubleCapacity
                    ? capacity
                    : (int)Math.Min(doubleCapacity, int.MaxValue);
            }

            var newBuffer = new byte[newCapacity];
            var startToEndCount = Math.Min(_buffer.Length - _start, _count);
            Buffer.BlockCopy(_buffer, _start, newBuffer, 0, startToEndCount);
            Buffer.BlockCopy(_buffer, 0, newBuffer, startToEndCount, _count - startToEndCount);
            _buffer = newBuffer;
            _start = 0;
        }

        private void CloseWriteSide()
        {
            lock (_lock)
            {
                // no-op if we're already closed
                if (_writerClosed)
                {
                    return;
                }

                // if we don't have an active write task, close now
                if (_writeTask.IsCompleted)
                {
                    InternalCloseWriteSideNoLock();
                    return;
                }

                // otherwise, close as a continuation on the write task
                _writeTask = _writeTask.ContinueWith(
                    (t, state) =>
                    {
                        var @this = (Pipe)state;
                        lock (@this._lock)
                        {
                            @this.InternalCloseWriteSideNoLock();
                        }
                    }
                    , this
                );
            }
        }

        private void InternalCloseWriteSideNoLock()
        {
            _writerClosed = true;
            UpdateSignalsNoLock();
            if (_readerClosed)
            {
                // if both sides are now closed, cleanup
                CleanupNoLock();
            }
        }
        #endregion

        #region ---- Reading ----
        private Task<int> ReadAsync(byte[] buffer, int offset, int count, TimeSpan timeout, CancellationToken cancellationToken)
        {
            ValidateBuffer(buffer, offset, count);

            // always respect cancellation, even in the sync flow
            if (cancellationToken.IsCancellationRequested)
            {
                return CreateCanceledTask();
            }

            // if we didn't want to read anything, return immediately
            if (count == 0)
            {
                return CompletedTask;
            }

            lock (_lock)
            {
                if (_readerClosed)
                {
                    throw new ObjectDisposedException("Reader", "The read side of the pipe is closed.");
                }

                if (!_readTask.IsCompleted)
                {
                    throw new InvalidOperationException("Concurrent reads are not allowed.");
                }

                // if we have bytes, read them and return synchronously
                if (_count > 0)
                {
                    return Task.FromResult(ReadNoLock(buffer, offset, count));
                }

                // if we don't have bytes and no more are coming, return 0
                if (_writerClosed)
                {
                    return CompletedTask;
                }

                // otherwise, create and return an async read task
                return _readTask = ReadNoLockAsync(buffer, offset, count, timeout, cancellationToken);
            }
        }

        private async Task<int> ReadNoLockAsync(byte[] buffer, int offset, int count, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var acquired = await _bytesAvailableSignal.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            if (!acquired)
            {
                throw new TimeoutException("Timed out reading from the pipe.");
            }

            // we need to reacquire the lock after the await since we might have switched threads
            lock (_lock)
            {
                return ReadNoLock(buffer, offset, count);
            }
        }

        private int ReadNoLock(byte[] buffer, int offset, int count)
        {
            var countToRead = Math.Min(_count, count);

            var bytesRead = 0;
            while (bytesRead < countToRead)
            {
                var bytesToRead = Math.Min(countToRead - bytesRead, _buffer.Length - _start);
                Buffer.BlockCopy(_buffer, _start, buffer, offset + bytesRead, bytesToRead);
                bytesRead += bytesToRead;
                _start = (_start + bytesToRead) % _buffer.Length;
            }
            _count -= countToRead;

            // ensure that an empty pipe never stays above the max stable size
            if (_count == 0
                && _buffer.Length > MaximumStableSize)
            {
                _start = 0;
                _buffer = new byte[MaximumStableSize];
            }

            UpdateSignalsNoLock();

            return countToRead;
        }

        private void CloseReadSide()
        {
            lock (_lock)
            {
                // no-op if we're already closed
                if (_readerClosed)
                {
                    return;
                }

                // if we don't have an active read task, close now
                if (_readTask.IsCompleted)
                {
                    InternalCloseReadSideNoLock();
                    return;
                }

                // otherwise, close as a continuation on the read task
                _readTask = _readTask.ContinueWith(
                    (t, state) =>
                    {
                        var @this = (Pipe)state;
                        lock (@this._lock)
                        {
                            @this.InternalCloseReadSideNoLock();
                        }
                        return -1;
                    }, this
                );
            }
        }

        private void InternalCloseReadSideNoLock()
        {
            _readerClosed = true;
            UpdateSignalsNoLock();
            if (_writerClosed)
            {
                // if both sides are now closed, cleanup
                CleanupNoLock();
            }
        }
        #endregion

        #region ---- Dispose ----
        private void CleanupNoLock()
        {
            _buffer = null;
            _readTask = null;
            _bytesAvailableSignal.Dispose();
            _spaceAvailableSignal?.Dispose();
        }
        #endregion

        #region ---- Cancellation ----
        private static Task<int> CreateCanceledTask()
        {
            var taskCompletionSource = new TaskCompletionSource<int>();
            taskCompletionSource.SetCanceled();
            return taskCompletionSource.Task;
        }
        #endregion

        #region ---- Input Stream ----
        private sealed class PipeInputStream : Stream
        {
            private readonly Pipe _pipe;

            public PipeInputStream(Pipe pipe) { _pipe = pipe; }

            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                throw WriteOnly();
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                // according to the docs, the callback is optional
                var writeTask = WriteAsync(buffer, offset, count, CancellationToken.None);
                var writeResult = new AsyncWriteResult(state, writeTask, this);
                if (callback != null)
                {
                    writeTask.ContinueWith(_ => callback(writeResult));
                }
                return writeResult;
            }

            private sealed class AsyncWriteResult : IAsyncResult
            {
                private readonly object _state;
                private readonly PipeInputStream _stream;

                public AsyncWriteResult(object state, Task writeTask, PipeInputStream stream)
                {
                    _state = state;
                    WriteTask = writeTask;
                    _stream = stream;
                }

                public Task WriteTask { get; }

                public Stream Stream => _stream;

                object IAsyncResult.AsyncState => _state;

                WaitHandle IAsyncResult.AsyncWaitHandle => (WriteTask as IAsyncResult).AsyncWaitHandle;

                bool IAsyncResult.CompletedSynchronously => (WriteTask as IAsyncResult).CompletedSynchronously;

                bool IAsyncResult.IsCompleted => WriteTask.IsCompleted;
            }

            public override bool CanRead => false;

            public override bool CanSeek => false;

            public override bool CanTimeout => false;

            public override bool CanWrite => true;

            public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
            {
                throw WriteOnly();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _pipe.CloseWriteSide();
                }
            }

            public override int EndRead(IAsyncResult asyncResult)
            {
                throw WriteOnly();
            }

            public override void EndWrite(IAsyncResult asyncResult)
            {
                if (asyncResult == null)
                {
                    throw new ArgumentNullException(nameof(asyncResult));
                }

                var writeResult = asyncResult as AsyncWriteResult;

                if (writeResult == null || writeResult.Stream != this)
                {
                    throw new ArgumentNullException(nameof(asyncResult),
                        "asyncResult: must be created by this stream's BeginWrite method.");
                }

                writeResult?.WriteTask.Wait();
            }

            public override void Flush()
            {
                // no-op, since we are just a buffer
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                // no-op since we are just a buffer
                return CompletedTask;
            }

            public override long Length { get { throw new NotSupportedException(); } }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw WriteOnly();
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                throw WriteOnly();
            }

            public override int ReadByte()
            {
                throw WriteOnly();
            }

            public override int ReadTimeout
            {
                get { throw WriteOnly(); }
                set { throw WriteOnly(); }
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                try
                {
                    _pipe.WriteAsync(buffer, offset, count, TimeSpan.FromMilliseconds(WriteTimeout), CancellationToken.None).Wait();
                }
                catch (AggregateException ex)
                {
                    // unwrap aggregate if we can
                    if (ex.InnerExceptions.Count == 1)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    }

                    throw;
                }
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _pipe.WriteAsync(buffer, offset, count, TimeSpan.FromMilliseconds(WriteTimeout), cancellationToken);
            }

            private int _writeTimeout = Timeout.Infinite;

            public override int WriteTimeout
            {
                get { return _writeTimeout; }
                set
                {
                    if (value != Timeout.Infinite)
                    {
                        if (value.CompareTo(0) < 0)
                        {
                            throw new ArgumentOutOfRangeException("WriteTimeout", $"Expected: >= 0, but was {value}");
                        }
                    }
                    _writeTimeout = value;
                }
            }

            private static NotSupportedException WriteOnly([CallerMemberName] string memberName = null)
            {
                throw new NotSupportedException(memberName + ": the stream is write only.");
            }
        }
        #endregion

        #region ---- Output Stream ----
        private sealed class PipeOutputStream : Stream
        {
            private readonly Pipe _pipe;

            public PipeOutputStream(Pipe pipe) { _pipe = pipe; }

            public override IAsyncResult BeginRead(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                // according to the docs, the callback is optional
                var readTask = ReadAsync(buffer, offset, count, CancellationToken.None);
                var readResult = new AsyncReadResult(state, readTask, this);
                if (callback != null)
                {
                    readTask.ContinueWith(_ => callback(readResult));
                }
                return readResult;
            }

            private sealed class AsyncReadResult : IAsyncResult
            {
                private readonly object _state;
                private readonly PipeOutputStream _stream;

                public AsyncReadResult(object state, Task<int> readTask, PipeOutputStream stream)
                {
                    _state = state;
                    ReadTask = readTask;
                    _stream = stream;
                }

                public Task<int> ReadTask { get; }

                public Stream Stream => _stream;

                object IAsyncResult.AsyncState => _state;

                WaitHandle IAsyncResult.AsyncWaitHandle => (ReadTask as IAsyncResult).AsyncWaitHandle;

                bool IAsyncResult.CompletedSynchronously => (ReadTask as IAsyncResult).CompletedSynchronously;

                bool IAsyncResult.IsCompleted => ReadTask.IsCompleted;
            }

            public override IAsyncResult BeginWrite(byte[] buffer, int offset, int count, AsyncCallback callback, object state)
            {
                throw ReadOnly();
            }

            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanTimeout => true;

            public override bool CanWrite => false;

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _pipe.CloseReadSide();
                }
            }

            public override int EndRead(IAsyncResult asyncResult)
            {
                if (asyncResult == null)
                {
                    throw new ArgumentNullException(nameof(asyncResult));
                }

                var readResult = asyncResult as AsyncReadResult;

                if (readResult == null || readResult.Stream != this)
                {
                    throw new ArgumentNullException(nameof(asyncResult),
                        "asyncResult: must be created by this stream's BeginRead method.");
                }

                return readResult.ReadTask.Result;
            }

            public override void EndWrite(IAsyncResult asyncResult)
            {
                throw ReadOnly();
            }

            public override void Flush()
            {
                throw ReadOnly();
            }

            public override Task FlushAsync(CancellationToken cancellationToken)
            {
                throw ReadOnly();
            }

            public override long Length { get { throw new NotSupportedException(); } }

            public override long Position
            {
                get { throw new NotSupportedException(); }
                set { throw new NotSupportedException(); }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                try
                {
                    return _pipe.ReadAsync(buffer, offset, count, TimeSpan.FromMilliseconds(ReadTimeout), CancellationToken.None).Result;
                }
                catch (AggregateException ex)
                {
                    // unwrap aggregate if we can
                    if (ex.InnerExceptions.Count == 1)
                    {
                        ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                    }

                    throw;
                }
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                return _pipe.ReadAsync(buffer, offset, count, TimeSpan.FromMilliseconds(ReadTimeout), cancellationToken);
            }

            private int _readTimeout = Timeout.Infinite;

            public override int ReadTimeout
            {
                get { return _readTimeout; }
                set
                {
                    if (value != Timeout.Infinite)
                    {
                        if (value.CompareTo(0) < 0)
                        {
                            throw new ArgumentOutOfRangeException("ReadTimeout", $"Expected: >= 0, but was {value}");
                        }
                    }
                    _readTimeout = value;
                }
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw ReadOnly();
            }

            public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                throw ReadOnly();
            }

            public override void WriteByte(byte value)
            {
                throw ReadOnly();
            }

            public override int WriteTimeout
            {
                get { throw ReadOnly(); }
                set { throw ReadOnly(); }
            }

            private static NotSupportedException ReadOnly([CallerMemberName] string memberName = null)
            {
                throw new NotSupportedException(memberName + ": the stream is read only.");
            }
        }
        #endregion

        #region ---- Helpers ----
        private void ValidateBuffer<T>(T[] buffer, int offset, int count)
        {
            if (buffer == null)
            {
                throw new ArgumentNullException(nameof(buffer));
            }
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }
            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }
            if (offset + count > buffer.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset) + ", " + nameof(count),
                    "The segment described by offset and count must be within buffer.");
            }
        }
        #endregion
    }
}