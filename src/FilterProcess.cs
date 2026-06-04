using System.Diagnostics;

namespace UdonGitFilters
{
    public static class FilterProcess
    {
        private static readonly Stopwatch sw = new();
        private static double commandStartMs;

        public static int Run(bool useCompression)
        {
            sw.Start();
            using Stream inputStream = Console.OpenStandardInput();
            using Stream outputStream = Console.OpenStandardOutput();
            if (!TryHandShake(inputStream, outputStream))
                return 1;

            int processedFileCount = 0;
            while (true)
            {
                commandStartMs = sw.Elapsed.TotalMilliseconds;
                if (!TryProcessCommand(inputStream, outputStream, useCompression, out bool reachedEnd))
                {
                    Trace.Info($"Unexpected error, processed commands: {processedFileCount}, total ms: {sw.Elapsed.TotalMilliseconds:f3}");
                    return 1;
                }
                if (reachedEnd)
                {
                    Trace.Info($"Done, processed commands: {processedFileCount}, total ms: {sw.Elapsed.TotalMilliseconds:f3}");
                    return 0;
                }
                processedFileCount++;
            }
        }

        private static bool TryHandShake(Stream inputStream, Stream outputStream)
        {
            if (!PktLine.TryReadStringPacketRequired(inputStream, out string line) || line != "git-filter-client")
                return false;
            if (!PktLine.TryReadKVPPacketList(inputStream, "version", out List<string> versions))
                return false;
            if (!versions.Contains("2"))
                return false;
            PktLine.WriteStringPacket(outputStream, "git-filter-server");
            PktLine.WriteStringPacket(outputStream, "version=2");
            PktLine.WriteFlushPacket(outputStream);

            if (!PktLine.TryReadKVPPacketList(inputStream, "capability", out List<string> capabilities))
                return false;
            if (!capabilities.Contains("clean"))
                return false;
            if (!capabilities.Contains("smudge"))
                return false;
            PktLine.WriteStringPacket(outputStream, "capability=clean");
            PktLine.WriteStringPacket(outputStream, "capability=smudge");
            PktLine.WriteFlushPacket(outputStream);
            return true;
        }

        private static bool TryProcessCommand(Stream inputStream, Stream outputStream, bool useCompression, out bool reachedEnd)
        {
            if (!PktLine.TryReadArbitraryKVPPacketList(inputStream, out var pairs, out reachedEnd))
                return false;
            if (reachedEnd)
                return true;
            if (!PktLine.TryGetReadSingletonValue(pairs, "command", out string command))
                return false;
            return command switch
            {
                "clean" => TryProcessCleanCommand(inputStream, outputStream, useCompression, pairs),
                "smudge" => TryProcessSmudgeCommand(inputStream, outputStream, useCompression, pairs),
                _ => false,
            };
        }

        private static void FinishReadingAndWriteErrorStatus(Stream outputStream, Stream contentsStream)
        {
            byte[] buffer = new byte[1024 * 1024];
            // Must finish reading git's pkt lines before responding, in accordance with the protocol.
            while (contentsStream.Read(buffer, 0, buffer.Length) != 0)
            { }
            PktLine.WriteStringPacket(outputStream, "status=error");
            PktLine.WriteFlushPacket(outputStream);
        }

        private static bool TryProcessCleanCommand(Stream inputStream, Stream outputStream, bool useCompression, List<(string key, string value)> pairs)
        {
            return TryProcessCleanOrSmudgeCommand(inputStream, outputStream, useCompression, pairs, "clean", Program.GetCleanProcessor);
        }

        private static bool TryProcessSmudgeCommand(Stream inputStream, Stream outputStream, bool useCompression, List<(string key, string value)> pairs)
        {
            return TryProcessCleanOrSmudgeCommand(inputStream, outputStream, useCompression, pairs, "smudge", Program.GetSmudgeProcessor);
        }

        /// <summary>
        /// </summary>
        /// <param name="inputStream"></param>
        /// <param name="outputStream"></param>
        /// <param name="useCompression"></param>
        /// <param name="pairs"></param>
        /// <param name="commandName"></param>
        /// <param name="processorGetter"></param>
        /// <returns><see langword="false"/> if an unhandled error occurred.</returns>
        private static bool TryProcessCleanOrSmudgeCommand(
            Stream inputStream,
            Stream outputStream,
            bool useCompression,
            List<(string key, string value)> pairs,
            string commandName,
            Program.CleanOrSmudgeProcessorGetter processorGetter)
        {
            if (!PktLine.TryGetReadSingletonValue(pairs, "pathname", out string pathname))
                return false;
            using Stream contentsStream = PktLine.ReadFileContents(inputStream);
            using MemoryStream resultStream = new();
            if (!processorGetter(pathname, useCompression)(contentsStream, resultStream))
            {
                FinishReadingAndWriteErrorStatus(outputStream, contentsStream);
                Trace.Info($"error,  ms: {sw.Elapsed.TotalMilliseconds - commandStartMs:f3}, command: {commandName}, path: {pathname}");
                return true;
            }
            if (contentsStream.Read(new byte[1], 0, 1) != 0)
            {
                FinishReadingAndWriteErrorStatus(outputStream, contentsStream);
                Trace.Info($"error,  processor did not finish reading, ms: {sw.Elapsed.TotalMilliseconds - commandStartMs:f3}, command: {commandName}, path: {pathname}");
                return true;
            }
            resultStream.Position = 0;
            PktLine.WriteStringPacket(outputStream, "status=success");
            PktLine.WriteFlushPacket(outputStream);
            PktLine.WriteFileContents(outputStream, resultStream);
            PktLine.WriteFlushPacket(outputStream); // No change in status, another flush to confirm.
            Trace.Info($"success, ms: {sw.Elapsed.TotalMilliseconds - commandStartMs:f3}, command: {commandName}, path: {pathname}");
            return true;
        }
    }
}
