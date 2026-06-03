namespace UdonGitFilters
{
    public class PeekStream(Stream baseStream) : Stream
    {
        public Stream underlyingStream = baseStream;
        private readonly List<byte> bufferedBytes = [];
        private bool reachedEnd;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public int Peek(byte[]? buffer, int count)
        {
            if (buffer != null)
                bufferedBytes.CopyTo(0, buffer, 0, Math.Min(count, bufferedBytes.Count));
            if (bufferedBytes.Count >= count)
                return count;
            if (reachedEnd)
                return bufferedBytes.Count;
            int GetClampedMissingByteCount() => Math.Min(1024 * 1024, count - bufferedBytes.Count);
            byte[] secondaryBuffer = new byte[GetClampedMissingByteCount()];
            while (bufferedBytes.Count < count)
            {
                int countReadIntoBuffer = underlyingStream.Read(secondaryBuffer, 0, GetClampedMissingByteCount());
                if (countReadIntoBuffer <= 0)
                {
                    reachedEnd = true;
                    break;
                }
                if (buffer != null)
                    Buffer.BlockCopy(secondaryBuffer, 0, buffer, bufferedBytes.Count, countReadIntoBuffer);
                for (int i = 0; i < countReadIntoBuffer; i++)
                    bufferedBytes.Add(secondaryBuffer[i]);
            }
            return bufferedBytes.Count;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (bufferedBytes.Count == 0)
                return underlyingStream.Read(buffer, offset, count);
            int bufferedCount = Math.Min(count, bufferedBytes.Count);
            bufferedBytes.CopyTo(0, buffer, offset, bufferedCount);
            bufferedBytes.RemoveRange(0, bufferedCount);
            return bufferedCount;
        }

        protected override void Dispose(bool disposing)
        {
            underlyingStream.Dispose();
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
