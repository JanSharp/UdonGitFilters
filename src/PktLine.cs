using System.Text;

namespace UdonGitFilters
{
    public static class PktLine
    {
        // https://git-scm.com/docs/gitprotocol-common
        // https://git-scm.com/docs/long-running-process-protocol
        // https://git-scm.com/docs/gitattributes

        private static readonly int zeroCharByte = Encoding.UTF8.GetBytes("0").Single();
        private static readonly int lowerACharByte = Encoding.UTF8.GetBytes("a").Single();
        private static readonly byte linefeedCharByte = Encoding.UTF8.GetBytes("\n").Single();
        private const int MaxPacketPayloadLength = 65516;
        private const int MaxPacketLength = MaxPacketPayloadLength + 4;
        public static readonly byte[] packetPayloadBuffer = new byte[MaxPacketPayloadLength];
        private static readonly byte[] lengthDigitsBuffer = new byte[4];
        private static readonly byte[] flushPacket = Encoding.UTF8.GetBytes("0000");

        private static bool TryParseHexDigit(byte digit, out int value)
        {
            if (zeroCharByte <= digit && digit < zeroCharByte + 10)
                value = digit - zeroCharByte;
            else if (lowerACharByte <= digit && digit < lowerACharByte + 6)
                value = digit - lowerACharByte + 10;
            else
            {
                value = 0;
                return false;
            }
            return true;
        }

        private static bool TryParsePacketLength(byte[] digits, out int length)
        {
            if (TryParseHexDigit(digits[0], out int digit0)
                && TryParseHexDigit(digits[1], out int digit1)
                && TryParseHexDigit(digits[2], out int digit2)
                && TryParseHexDigit(digits[3], out int digit3))
            {
                length = (digit0 << 12) + (digit1 << 8) + (digit2 << 4) + digit3;
                return true;
            }
            length = 0;
            return false;
        }

        private static bool TryReadExactly(Stream stream, byte[] buffer, int offset, int count, out bool reachedEnd)
        {
            int alreadyRead = 0;
            reachedEnd = true;
            while (alreadyRead < count)
            {
                int readCount = stream.Read(buffer, offset + alreadyRead, count - alreadyRead);
                if (readCount == 0)
                    return false;
                alreadyRead += readCount;
                reachedEnd = false;
            }
            return true;
        }

        private static bool TryReadPacketLength(Stream stream, out int length, out bool reachedEnd)
        {
            if (TryReadExactly(stream, lengthDigitsBuffer, 0, 4, out reachedEnd) && TryParsePacketLength(lengthDigitsBuffer, out length))
                return true;
            length = 0;
            return false;
        }

        public static bool TryReadStringPacket(Stream stream, out string line, out bool isFlushPacket, out bool reachedEnd)
        {
            line = null!;
            isFlushPacket = false;
            if (!TryReadPacketLength(stream, out int length, out reachedEnd))
                return false;
            if (reachedEnd)
                return true;
            if (length == 0u)
            {
                isFlushPacket = true;
                return true;
            }
            if (length < 4u || MaxPacketLength < length)
                return false;
            if (length == 4u)
            {
                line = "";
                return true;
            }
            if (!TryReadExactly(stream, packetPayloadBuffer, 0, length - 4, out reachedEnd) || reachedEnd)
                return false;
            if (packetPayloadBuffer[length - 5u] == linefeedCharByte)
                length--;
            line = Encoding.UTF8.GetString(packetPayloadBuffer, 0, length - 4);
            return true;
        }

        /// <summary>
        /// <para>Use the <see cref="packetPayloadBuffer"/> to access read data.</para>
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="length"></param>
        /// <param name="isFlushPacket"></param>
        /// <param name="reachedEnd"></param>
        /// <returns></returns>
        public static bool TryReadBinaryPacket(Stream stream, out int length, out bool isFlushPacket, out bool reachedEnd)
        {
            length = 0;
            isFlushPacket = false;
            if (!TryReadPacketLength(stream, out int packetLength, out reachedEnd))
                return false;
            if (reachedEnd)
                return true;
            if (packetLength == 0u)
            {
                isFlushPacket = true;
                return true;
            }
            if (packetLength < 4u || MaxPacketLength < packetLength)
                return false;
            length = packetLength - 4;
            return TryReadExactly(stream, packetPayloadBuffer, 0, length, out reachedEnd) && !reachedEnd;
        }

        /// <summary>
        /// <para>Use the <see cref="packetPayloadBuffer"/> to access read data.</para>
        /// </summary>
        /// <param name="stream"></param>
        /// <param name="length"></param>
        /// <param name="isFlushPacket"></param>
        /// <returns></returns>
        public static bool TryReadBinaryPacketRequired(Stream stream, out int length, out bool isFlushPacket)
        {
            return TryReadBinaryPacket(stream, out length, out isFlushPacket, out bool reachedEnd) && !reachedEnd;
        }

        public static bool TryReadStringPacketRequired(Stream stream, out string line, out bool isFlushPacket)
        {
            return TryReadStringPacket(stream, out line, out isFlushPacket, out bool reachedEnd) && !reachedEnd;
        }

        public static bool TryReadStringPacketRequired(Stream stream, out string line)
        {
            return TryReadStringPacket(stream, out line, out bool isFlushPacket, out bool reachedEnd) && !isFlushPacket && !reachedEnd;
        }

        public static bool TryReadKVPPacket(Stream stream, out string key, out string value, out bool isFlushPacket, out bool reachedEnd)
        {
            key = null!;
            value = null!;
            if (!TryReadStringPacket(stream, out string line, out isFlushPacket, out reachedEnd))
                return false;
            if (isFlushPacket)
                return true;
            int separatorIndex = line.IndexOf('=');
            if (separatorIndex == -1)
                return false;
            key = line[..separatorIndex];
            value = line[(separatorIndex + 1)..];
            return true;
        }

        public static bool TryReadKVPPacketRequired(Stream stream, out string key, out string value, out bool isFlushPacket)
        {
            return TryReadKVPPacket(stream, out key, out value, out isFlushPacket, out bool reachedEnd) && !reachedEnd;
        }

        public static bool TryReadKVPPacketList(Stream stream, string expectedKey, out List<string> values)
        {
            values = [];
            while (true)
            {
                if (!TryReadKVPPacketRequired(stream, out string gotKey, out string value, out bool isFlushPacket))
                    return false;
                if (isFlushPacket)
                    return true;
                if (gotKey != expectedKey)
                    return false;
                values.Add(value);
            }
        }

        public static bool TryReadArbitraryKVPPacketList(Stream stream, out List<(string key, string value)> pairs, out bool reachedEnd)
        {
            pairs = [];
            while (true)
            {
                if (!TryReadKVPPacket(stream, out string key, out string value, out bool isFlushPacket, out reachedEnd))
                    return false;
                if (reachedEnd)
                    return pairs.Count == 0; // When having read a kvp already, a flush packet is required.
                if (isFlushPacket)
                    return true;
                pairs.Add((key, value));
            }
        }

        public static bool TryGetReadSingletonValue(List<(string key, string value)> pairs, string key, out string value)
        {
            value = null!;
            var filtered = pairs.Where(kvp => kvp.key == key);
            if (filtered.Count() != 1)
                return false;
            value = filtered.First().value;
            return true;
        }

        public static Stream ReadFileContents(Stream stream)
        {
            return new PktLineStreamReader(stream);
        }

        private static void WritePacketLength(Stream stream, int length)
        {
            Encoding.UTF8.TryGetBytes(length.ToString("x4"), lengthDigitsBuffer, out _);
            stream.Write(lengthDigitsBuffer);
        }

        public static void WriteStringPacket(Stream stream, string packet)
        {
            if (packet == "")
                throw new Exception("Sending empty string packets is invalid.");
            if (!Encoding.UTF8.TryGetBytes(packet, packetPayloadBuffer, out int length)
                || length == MaxPacketPayloadLength)// No room for the trailing linefeed.
            {
                throw new Exception($"The packet is longer than the max payload length of {MaxPacketPayloadLength}.");
            }
            packetPayloadBuffer[length] = linefeedCharByte;
            length += 4 + 1; // Leading length and trailing linefeed.
            WritePacketLength(stream, length);
            stream.Write(packetPayloadBuffer, 0, length - 4);
        }

        public static void WriteFileContents(Stream stream, Stream contents)
        {
            while (true)
            {
                int length = contents.Read(packetPayloadBuffer, 0, MaxPacketPayloadLength);
                if (length == 0)
                {
                    WriteFlushPacket(stream);
                    return;
                }
                WritePacketLength(stream, length + 4);
                stream.Write(packetPayloadBuffer, 0, length);
                stream.Flush();
            }
        }

        public static void WriteFlushPacket(Stream stream)
        {
            stream.Write(flushPacket);
            stream.Flush();
        }
    }

    public class PktLineStreamReader(Stream baseStream) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public readonly Stream baseStream = baseStream;

        private bool hasBufferedData;
        private int resumeIndex;
        private int bufferedLength;
        private bool reachedEnd;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (reachedEnd || count == 0)
                return 0;
            int totalReadCount = 0;
            while (true)
            {
                if (!hasBufferedData)
                {
                    if (!PktLine.TryReadBinaryPacketRequired(baseStream, out bufferedLength, out bool isFlushPacket))
                        throw new PktLineException("Invalid binary packet during read of file contents.");
                    if (isFlushPacket)
                    {
                        reachedEnd = true;
                        return totalReadCount;
                    }
                    resumeIndex = 0;
                }
                int lengthFromBuffer = Math.Min(count - totalReadCount, bufferedLength - resumeIndex);
                Buffer.BlockCopy(PktLine.packetPayloadBuffer, resumeIndex, buffer, offset, lengthFromBuffer);
                totalReadCount += lengthFromBuffer;
                offset += lengthFromBuffer;
                resumeIndex += lengthFromBuffer;
                hasBufferedData = resumeIndex < bufferedLength;
                if (totalReadCount == count)
                    return count;
            }
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Serializable]
    public class PktLineException : Exception
    {
        public PktLineException() { }
        public PktLineException(string message) : base(message) { }
        public PktLineException(string message, Exception inner) : base(message, inner) { }
    }
}
