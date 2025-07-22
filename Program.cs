using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

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
        public override long Length => throw new NotImplementedException();
        public override long Position { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

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

        public override void Flush() => throw new NotImplementedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotImplementedException();
        public override void SetLength(long value) => throw new NotImplementedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotImplementedException();
    }

    public class Program
    {
        private const int BufferSize = 1024 * 1024;
        private const int AssetBufferSize = 128 * 1024;
        private const int CompressionFileSizeThreshold = 10 * 1024 * 1024;
        private const string UdonGraphScriptGuid = "4f11136daadff0b44ac2278a314682ab";
        private const string UdonSharpScriptGuid = "c333ccfdd0cbdbc4ca30cef2dd6e6b9b";

        public static int Main(string[] args)
        {
            bool useCompression = args is [_, "--use-compression", ..];
            bool hasDoubleDash = useCompression ? args is [_, _, "--", ..] : args is [_, "--", ..];
            int expectedArgsCount = 2 + (useCompression ? 1 : 0) + (hasDoubleDash ? 1 : 0);
            if (args.Length < expectedArgsCount)
            {
                Console.Error.WriteLine("Requires at least 2 arguments: 'smudge'/'clean' and the file path "
                    + "(use '%f' (without the quotes) if the command is defined in the git config file).\n"
                    + "Optionally '--use-compression' can be specified immediately after 'smudge'/'clean', before the file path.\n"
                    + "Accepts a '--' as an args separator before the file path.");
                return 1;
            }
            switch (args[0])
            {
                case "smudge":
                    return Smudge(args[expectedArgsCount - 1], useCompression);
                case "clean":
                    return Clean(args[expectedArgsCount - 1], useCompression);
                default:
                    Console.Error.WriteLine($"Invalid first argument '{args[0]}', expected 'smudge'/'clean'.");
                    return 1;
            }
        }

        private static bool ShouldRemoveReferences(string path)
        {
            return Path.GetExtension(path) switch
            {
                ".unity" => true,
                ".prefab" => true,
                _ => false,
            };
        }

        private static bool IsAsset(string path)
        {
            return Path.GetExtension(path) == ".asset";
        }

        private static void PassThrough(Stream inputStream, Stream outputStream)
        {
            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                int size = inputStream.Read(buffer, 0, BufferSize);
                if (size <= 0)
                    break;
                outputStream.Write(buffer, 0, size);
                outputStream.Flush();
            }
        }

        private static void FromInputToOutput(Action<Stream, Stream> processor, Stream? inputStream = null)
        {
            using var input = inputStream ?? Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            processor(input, output);
            output.Flush();
        }

        private static void WrapProcess(Process process, Action<Stream, Stream> inputProcessor, Stream? inputStream = null)
        {
            var inputTask = Task.Run(() =>
            {
                using var input = inputStream ?? Console.OpenStandardInput();
                inputProcessor(input, process.StandardInput.BaseStream);
                process.StandardInput.BaseStream.Flush();
                process.StandardInput.BaseStream.Close();
            });

            var outputTask = Task.Run(() =>
            {
                using var output = Console.OpenStandardOutput();
                byte[] buffer = new byte[BufferSize];
                while (true)
                {
                    int size = process.StandardOutput.BaseStream.Read(buffer, 0, BufferSize);
                    if (size <= 0)
                        break;
                    output.Write(buffer, 0, size);
                    output.Flush();
                }
            });

            inputTask.Wait();
            outputTask.Wait();
            process.WaitForExit();
            process.Close();
        }

        private static int Smudge(string path, bool useCompression)
        {
            _ = path; // The path is no longer being used, however for simplicity of the interface
            // and for future proofing, keep it as a requirement anyway.
            _ = useCompression; // This is also unused in order to support changing the git filter
            // used by a file from using compression to not using compression, and still being able
            // to checkout files from history without issue. Because I think git uses the .gitattributes
            // from the work tree in order to determine what filters to run on a file when checking it
            // out, even if the .gitattributes for that file were different at the commit it is being
            // checked out from.

            using var inputStream = new PeekStream(Console.OpenStandardInput());
            byte[] header = new byte[6];
            header[5] = 0xff; // To make the xz array pattern match fail if only 5 bytes were read.
            inputStream.Peek(header, 6);

            int WrapSevenZip(string compressionType)
            {
                var sevenZip = Process.Start(new ProcessStartInfo()
                {
                    FileName = "7z",
                    ArgumentList = { "x", "-si", "-so", "-an", $"-t{compressionType}" },
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                });
                if (sevenZip == null)
                {
                    Console.Error.WriteLine($"Failed to start 7z (seven zip) process.");
                    return 1;
                }
                WrapProcess(sevenZip, PassThrough, inputStream);
                return 0;
            }

            ///cSpell:ignore bbbccccc, NOTIMPL

            // https://iamhow.com/Technical_Notes/File_headers.html
            // Zip (.zip) format description, starts with 0x50, 0x4b, 0x03, 0x04 (unless empty — then the last two are 0x05, 0x06 or 0x06, 0x06)
            // Gzip (.gz) format description, starts with 0x1f, 0x8b, 0x08
            // xz (.xz) format description, starts with 0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00
            // zlib (.zz) format description, starts with (in bits) 0aaa1000 bbbccccc, where ccccc is chosen so that the first byte times 256 plus the second byte is a multiple of 31.
            // compress (.Z) starts with 0x1f, 0x9d

            switch (header)
            {
                // 'zip' compression type was never used by this program, nor does it even work apparently.
                // But I am keeping it here just for the headers and for the explanation how it is broken.
                // case [0x50, 0x4b, 0x03, 0x04, ..]
                //     or [0x50, 0x4b, 0x05, 0x06, ..]
                //     or [0x50, 0x4b, 0x06, 0x06, ..]:
                //     // 7z does not actually have an implementation for this? It says:
                //     // ERROR:
                //     // Cannot open the file as archive
                //     //
                //     // E_NOTIMPL : Not implemented
                //     return WrapSevenZip("zip");
                case [0x1f, 0x8b, 0x08, ..]: // This program used gzip in the past,
                    return WrapSevenZip("gzip"); // this is required for backwards compatibility.
                case [0xfd, 0x37, 0x7a, 0x58, 0x5a, 0x00, ..]:
                    return WrapSevenZip("xz");
                default: // Assume text.
                    FromInputToOutput(PassThrough, inputStream);
                    return 0;
            }
        }

        private static Action<Stream, Stream> GetCleanProcessor(string path)
        {
            return ShouldRemoveReferences(path) ? RemoveSerializedProgramAssetReferences
                : IsAsset(path) ? CleanUdonGraphAndUdonSharpAsset
                : PassThrough;
        }

        private static int Clean(string path, bool useCompression)
        {
            if (!useCompression)
            {
                FromInputToOutput(GetCleanProcessor(path));
                return 0;
            }

            using var inputStream = new PeekStream(Console.OpenStandardInput());
            if (inputStream.Peek(null, CompressionFileSizeThreshold) != CompressionFileSizeThreshold)
            {
                FromInputToOutput(GetCleanProcessor(path), inputStream);
                return 0;
            }

            var sevenZip = Process.Start(new ProcessStartInfo()
            {
                FileName = "7z",
                ArgumentList = { "a", "-si", "-so", "-an", "-txz" },
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            });
            if (sevenZip == null)
            {
                Console.Error.WriteLine($"Failed to start 7z (seven zip) process.");
                return 1;
            }

            WrapProcess(sevenZip, GetCleanProcessor(path), inputStream);
            return 0;
        }

        private static void RemoveSerializedProgramAssetReferences(Stream inputStream, Stream outputStream)
        {
            byte[] inputBuffer = new byte[BufferSize];
            int inputSize = inputStream.Read(inputBuffer, 0, BufferSize);
            if (inputSize <= 0)
                return;
            int inputIndex = 0;
            bool reachedEnd = false;

            void ReadMore()
            {
                if (reachedEnd)
                    return;
                inputSize = inputStream.Read(inputBuffer, 0, BufferSize);
                if (inputSize <= 0)
                    reachedEnd = true;
                inputIndex = 0;
            }

            bool IsEndOfFile()
            {
                if (inputIndex >= inputSize)
                    ReadMore();
                return reachedEnd;
            }

            byte Next()
            {
                if (IsEndOfFile())
                    throw new Exception("Attempt to read past the end of the file.");
                return inputBuffer[inputIndex++];
            }

            byte Peek()
            {
                if (IsEndOfFile())
                    throw new Exception("Attempt to peek past the end of the file.");
                return inputBuffer[inputIndex];
            }

            byte[] outputBuffer = new byte[BufferSize];
            int outputSize = 0;

            void FlushOutputBuffer()
            {
                outputStream.Write(outputBuffer, 0, outputSize);
                outputSize = 0;
            }

            void Write(byte b)
            {
                outputBuffer[outputSize++] = b;
                if (outputSize == BufferSize)
                    FlushOutputBuffer();
            }

            List<byte> buffer = [];
            void BufferNext()
            {
                buffer.Add(Next());
            }

            void ReadWhiteSpace()
            {
                while (!IsEndOfFile()
                    && (Peek() switch
                    {
                        (byte)' ' => true,
                        (byte)'\t' => true,
                        (byte)'\v' => true,
                        (byte)'\r' => true,
                        (byte)'\n' => true,
                        _ => false,
                    }))
                {
                    BufferNext();
                }
            }

            bool TestNext(byte expected)
            {
                if (IsEndOfFile() || Peek() != expected)
                    return false;
                BufferNext();
                return true;
            }

            bool TestNextWord(byte[] word)
            {
                foreach (byte b in word)
                    if (!TestNext(b))
                        return false;
                return true;
            }

            bool TestNextOneOrMore(Func<byte, bool> condition)
            {
                if (IsEndOfFile() || !condition(Peek()))
                    return false;
                BufferNext();
                while (!IsEndOfFile() && condition(Peek()))
                    BufferNext();
                return true;
            }

            bool ReadPattern()
            {
                // serializedProgramAsset was already matched, so this matches:
                // : {fileID: 11400000, guid: 9eb6bf22b7b45af1d8ef5e8652d24b03, type: 2}
                ReadWhiteSpace();
                if (!TestNext((byte)':'))
                    return false;
                ReadWhiteSpace();
                if (!TestNext((byte)'{'))
                    return false;
                ReadWhiteSpace();
                if (!TestNextWord(Encoding.UTF8.GetBytes("fileID")))
                    return false;
                ReadWhiteSpace();
                if (!TestNext((byte)':'))
                    return false;
                ReadWhiteSpace();
                if (!TestNextOneOrMore(b => b switch
                {
                    (byte)'0' => true,
                    (byte)'1' => true,
                    (byte)'2' => true,
                    (byte)'3' => true,
                    (byte)'4' => true,
                    (byte)'5' => true,
                    (byte)'6' => true,
                    (byte)'7' => true,
                    (byte)'8' => true,
                    (byte)'9' => true,
                    _ => false,
                }))
                    return false;
                ReadWhiteSpace();
                if (!TestNext((byte)','))
                    return false;
                ReadWhiteSpace();
                if (!TestNextWord(Encoding.UTF8.GetBytes("guid")))
                    return false;
                ReadWhiteSpace();
                if (!TestNext((byte)':'))
                    return false;
                ReadWhiteSpace();
                if (!TestNextOneOrMore(b => b switch
                {
                    (byte)'0' => true,
                    (byte)'1' => true,
                    (byte)'2' => true,
                    (byte)'3' => true,
                    (byte)'4' => true,
                    (byte)'5' => true,
                    (byte)'6' => true,
                    (byte)'7' => true,
                    (byte)'8' => true,
                    (byte)'9' => true,
                    (byte)'a' => true,
                    (byte)'b' => true,
                    (byte)'c' => true,
                    (byte)'d' => true,
                    (byte)'e' => true,
                    (byte)'f' => true,
                    (byte)'A' => true,
                    (byte)'B' => true,
                    (byte)'C' => true,
                    (byte)'D' => true,
                    (byte)'E' => true,
                    (byte)'F' => true,
                    _ => false,
                }))
                    return false;
                ReadWhiteSpace();
                if (!TestNext((byte)','))
                    return false;
                ReadWhiteSpace();
                if (!TestNextWord(Encoding.UTF8.GetBytes("type")))
                    return false;
                ReadWhiteSpace();
                if (!TestNext((byte)':'))
                    return false;
                ReadWhiteSpace();
                if (!TestNextOneOrMore(b => b switch
                {
                    (byte)'0' => true,
                    (byte)'1' => true,
                    (byte)'2' => true,
                    (byte)'3' => true,
                    (byte)'4' => true,
                    (byte)'5' => true,
                    (byte)'6' => true,
                    (byte)'7' => true,
                    (byte)'8' => true,
                    (byte)'9' => true,
                    _ => false,
                }))
                    return false;
                ReadWhiteSpace();
                if (!TestNext((byte)'}'))
                    return false;
                return true;
            }

            byte[] startWord = Encoding.UTF8.GetBytes("serializedProgramAsset");
            int startWordIndex = 0;

            byte[] utf8bom = [0xef, 0xbb, 0xbf];
            foreach (byte b in utf8bom)
            {
                // If this breaks out in the middle of a utf8 BOM then the parser should technically restart from the beginning.
                // However if that is actually the case then chances are about 99.99999% that the yaml header does not exist either,
                // therefore it'll all just get passed through anyway.
                if (IsEndOfFile() || Peek() != b)
                    break;
                Write(Next());
            }
            byte[] yamlHeader = Encoding.UTF8.GetBytes("%YAML");
            foreach (byte b in yamlHeader)
            {
                if (IsEndOfFile() || Peek() != b)
                {
                    while (inputIndex < inputSize)
                        Write(inputBuffer[inputIndex++]);
                    FlushOutputBuffer();
                    if (!IsEndOfFile())
                        PassThrough(inputStream, outputStream);
                    return; // Do not process any files that are not yaml files. Especially not binary files.
                }
                Write(Next());
            }

            while (!IsEndOfFile())
            {
                byte current = Next();
                Write(current);
                if (current != startWord[startWordIndex])
                {
                    startWordIndex = 0;
                    continue;
                }
                if ((++startWordIndex) != startWord.Length)
                    continue;
                startWordIndex = 0;
                if (!ReadPattern())
                {
                    foreach (byte b in buffer)
                        Write(b);
                    buffer.Clear();
                    continue;
                }
                buffer.Clear();
                foreach (byte b in Encoding.UTF8.GetBytes(": {fileID: 0}"))
                    Write(b);
            }

            FlushOutputBuffer();
        }

        private static void CleanUdonGraphAndUdonSharpAsset(Stream inputStream, Stream outputStream)
        {
            byte[] inputBuffer = new byte[AssetBufferSize];
            int inputSize = 0;
            bool reachedEnd = false;
            while (inputSize < AssetBufferSize)
            {
                int size = inputStream.Read(inputBuffer, inputSize, AssetBufferSize - inputSize);
                if (size <= 0)
                {
                    reachedEnd = true;
                    break;
                }
                inputSize += size;
            }

            void WriteAndPassThrough()
            {
                outputStream.Write(inputBuffer, 0, inputSize);
                outputStream.Flush();
                if (!reachedEnd)
                    PassThrough(inputStream, outputStream);
            }

            int i = 0;

            byte[] utf8bom = [0xef, 0xbb, 0xbf];
            if (inputSize >= 3 && inputBuffer[0] == utf8bom[0] && inputBuffer[1] == utf8bom[1] && inputBuffer[2] == utf8bom[2])
                i = 3;
            byte[] yamlHeader = Encoding.UTF8.GetBytes("%YAML");
            if (inputSize < i + 4
                || inputBuffer[i + 0] != yamlHeader[0]
                || inputBuffer[i + 1] != yamlHeader[1]
                || inputBuffer[i + 2] != yamlHeader[2]
                || inputBuffer[i + 3] != yamlHeader[3])
            {
                // Only process text files with the yaml header, others get passed through.
                WriteAndPassThrough();
                return;
            }
            i += 4;

            string? scriptGuid = null;
            string? name = null;
            int? serializedUdonOpenCurly = null;
            int? serializedUdonPostCloseCurly = null;
            string? sourceCsFileId = null;
            string? sourceCsGuid = null;
            string? sourceCsType = null;

            void ReadWhiteSpace()
            {
                while (i < inputSize
                    && (inputBuffer[i] switch
                    {
                        (byte)' ' => true,
                        (byte)'\t' => true,
                        (byte)'\v' => true,
                        (byte)'\r' => true,
                        (byte)'\n' => true,
                        _ => false,
                    }))
                {
                    i++;
                }
            }

            bool CheckField(byte[] field)
            {
                int startIndex = i;
                if (startIndex + field.Length >= AssetBufferSize)
                    return false;
                for (int j = 0; j < field.Length; j++)
                    if (inputBuffer[startIndex + j] != field[j])
                        return false;
                i += field.Length;
                ReadWhiteSpace();
                if (!TestNext((byte)':'))
                {
                    i = startIndex;
                    return false;
                }
                return true;
            }

            bool TestNext(byte expected)
            {
                if (i >= inputSize || inputBuffer[i] != expected)
                    return false;
                i++;
                return true;
            }

            bool ReadUntil(byte closingByte)
            {
                while (i < inputSize)
                    if (inputBuffer[i++] == closingByte) // Consume regardless of the condition.
                        return true;
                return false;
            }

            byte[] scriptField = Encoding.UTF8.GetBytes("m_Script");
            bool CheckScriptField()
            {
                if (!CheckField(scriptField))
                    return false;
                ReadWhiteSpace();
                if (!TestNext((byte)'{'))
                    return false;
                int contentBegin = i;
                if (!ReadUntil((byte)'}'))
                    return false;
                string content = Encoding.UTF8.GetString(inputBuffer, contentBegin, i - contentBegin - 1);
                Match match = Regex.Match(content, @"guid\s*:\s*([0-9a-zA-Z]+)");
                if (!match.Success)
                    return false;
                scriptGuid = match.Groups[1].Value.ToLower();
                return true;
            }

            byte[] nameField = Encoding.UTF8.GetBytes("m_Name");
            bool CheckNameField()
            {
                if (!CheckField(nameField))
                    return false;
                ReadWhiteSpace();
                int nameStart = i;
                while (i < inputSize && inputBuffer[i] != (byte)'\n' && inputBuffer[i] != (byte)'\r')
                    i++;
                name = Encoding.UTF8.GetString(inputBuffer, nameStart, i - nameStart);
                if (name == "")
                    name = null;
                return true;
            }

            byte[] serializedUdonProgramAssetField = Encoding.UTF8.GetBytes("serializedUdonProgramAsset");
            bool CheckSerializedUdonProgramAssetField()
            {
                if (!CheckField(serializedUdonProgramAssetField))
                    return false;
                ReadWhiteSpace();
                int open = i;
                if (!TestNext((byte)'{'))
                    return false;
                if (!ReadUntil((byte)'}'))
                    return false;
                serializedUdonOpenCurly = open;
                serializedUdonPostCloseCurly = i;
                return true;
            }

            byte[] sourceCsField = Encoding.UTF8.GetBytes("sourceCsScript");
            bool CheckSourceCsField()
            {
                if (!CheckField(sourceCsField))
                    return false;
                ReadWhiteSpace();
                if (!TestNext((byte)'{'))
                    return false;
                int contentBegin = i;
                if (!ReadUntil((byte)'}'))
                    return false;
                string content = Encoding.UTF8.GetString(inputBuffer, contentBegin, i - contentBegin - 1);
                Match match = Regex.Match(content, @"fileID\s*:\s*([0-9]+)\s*,\s*guid\s*:\s*([0-9a-zA-Z]+)\s*,\s*type\s*:\s*([0-9]+)");
                if (!match.Success)
                    return false;
                sourceCsFileId = match.Groups[1].Value;
                sourceCsGuid = match.Groups[2].Value;
                sourceCsType = match.Groups[3].Value;
                return true;
            }

            bool checkForFieldStart = false;

            while (i < inputSize)
            {
                byte current = inputBuffer[i];

                if (!checkForFieldStart)
                {
                    checkForFieldStart = current == (byte)'\n';
                    i++;
                    continue;
                }

                switch (current)
                {
                    case (byte)'\r':
                    case (byte)' ':
                    case (byte)'\t':
                    case (byte)'\v':
                        i++;
                        continue;
                }

                checkForFieldStart = false;
                if (CheckScriptField())
                {
                    if (scriptGuid != UdonGraphScriptGuid && scriptGuid != UdonSharpScriptGuid)
                    {
                        WriteAndPassThrough();
                        return;
                    }
                    continue;
                }
                if (CheckNameField())
                    continue;
                if (CheckSerializedUdonProgramAssetField())
                    continue;
                if (CheckSourceCsField())
                    continue;
                i++;
            }

            if (scriptGuid == null)
            {
                WriteAndPassThrough();
                return;
            }

            if (scriptGuid == UdonGraphScriptGuid)
            {
                if (serializedUdonOpenCurly == null)
                {
                    WriteAndPassThrough();
                    return;
                }
                // Set 'serializedUdonProgramAsset' to '{fileID: 0}'.
                outputStream.Write(inputBuffer, 0, serializedUdonOpenCurly.Value);
                outputStream.Write(Encoding.UTF8.GetBytes("{fileID: 0}"));
                outputStream.Write(inputBuffer, serializedUdonPostCloseCurly!.Value, inputSize - serializedUdonPostCloseCurly.Value);
                outputStream.Flush();
                if (!reachedEnd)
                    PassThrough(inputStream, outputStream);
                return;
            }

            // It is an UdonSharp asset file.

            if (name == null || sourceCsFileId == null)
            {
                WriteAndPassThrough();
                return;
            }

            ///cSpell:ignore Behaviour
            outputStream.Write(Encoding.UTF8.GetBytes("%YAML 1.1\n"
                + "%TAG !u! tag:unity3d.com,2011:\n"
                + "--- !u!114 &11400000\n"
                + "MonoBehaviour:\n"
                + "  m_ObjectHideFlags: 0\n"
                + "  m_CorrespondingSourceObject: {fileID: 0}\n"
                + "  m_PrefabInstance: {fileID: 0}\n"
                + "  m_PrefabAsset: {fileID: 0}\n"
                + "  m_GameObject: {fileID: 0}\n"
                + "  m_Enabled: 1\n"
                + "  m_EditorHideFlags: 0\n"
                + "  m_Script: {fileID: 11500000, guid: " + UdonSharpScriptGuid + ", type: 3}\n"
                + "  m_Name: " + name + "\n"
                + "  m_EditorClassIdentifier: \n"
                + "  serializedUdonProgramAsset: {fileID: 0}\n"
                + "  udonAssembly: \n"
                + "  assemblyError: \n"
                + "  sourceCsScript: {fileID: " + sourceCsFileId + (sourceCsFileId != "0" ? (", guid: " + sourceCsGuid! + ", type: " + sourceCsType!) : "") + "}\n"
                + "  scriptVersion: 0\n"
                + "  compiledVersion: 0\n"
                + "  behaviourSyncMode: 0\n"
                + "  hasInteractEvent: 0\n"
                + "  scriptID: 0\n"
                + "  serializationData:\n"
                + "    SerializedFormat: 2\n"
                + "    SerializedBytes: \n"
                + "    ReferencedUnityObjects: []\n"
                + "    SerializedBytesString: \n"
                + "    Prefab: {fileID: 0}\n"
                + "    PrefabModificationsReferencedUnityObjects: []\n"
                + "    PrefabModifications: []\n"
                + "    SerializationNodes:\n"
                + "    - Name: fieldDefinitions\n"
                + "      Entry: 6\n"
                + "      Data: \n"));
            outputStream.Flush();
        }
    }
}
