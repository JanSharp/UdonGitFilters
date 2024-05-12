using System.Diagnostics;
using System.Text;

public class Program
{
    private const int BufferSize = 1024 * 1024;

    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Requires 2 arguments. 'smudge'/'clean' and the file path.");
            return 1;
        }
        switch (args[0])
        {
            case "smudge":
                return Smudge(args[1]);
            case "clean":
                return Clean(args[1]);
            default:
                Console.Error.WriteLine($"Invalid first argument '{args[0]}', expected 'smudge'/'clean'.");
                return 1;
        }
    }

    private static bool ShouldCompress(string path)
    {
        return Path.GetExtension(path) switch
        {
            ".unity" => true,
            _ => false,
        };
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

    private static void FromInputToOutput(Action<Stream, Stream> processor)
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        processor(input, output);
        output.Flush();
    }

    private static void WrapProcess(Process process, Action<Stream, Stream> inputProcessor)
    {
        var inputTask = Task.Run(() => {
            using var input = Console.OpenStandardInput();
            inputProcessor(input, process.StandardInput.BaseStream);
            process.StandardInput.BaseStream.Flush();
            process.StandardInput.BaseStream.Close();
        });

        var outputTask = Task.Run(() => {
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

    private static int Smudge(string path)
    {
        if (!ShouldCompress(path))
        {
            FromInputToOutput(PassThrough);
            return 0;
        }

        ///cSpell:ignore tgzip

        var sevenZip = Process.Start(new ProcessStartInfo()
        {
            FileName = "7z",
            ArgumentList = {"x", "-si", "-so", "-an", "-txz"},
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

        WrapProcess(sevenZip, PassThrough);
        return 0;
    }

    private static Action<Stream, Stream> GetCleanProcessor(string path)
    {
        return ShouldRemoveReferences(path)
            ? RemoveSerializedProgramAssetReferences
            : PassThrough;
    }

    private static int Clean(string path)
    {
        if (!ShouldCompress(path))
        {
            FromInputToOutput(GetCleanProcessor(path));
            return 0;
        }

        var sevenZip = Process.Start(new ProcessStartInfo()
        {
            FileName = "7z",
            ArgumentList = {"a", "-si", "-so", "-an", "-txz"},
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

        WrapProcess(sevenZip, GetCleanProcessor(path));
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
            if (!TestNextOneOrMore(b => b switch {
                    (byte)'0' => true, (byte)'1' => true, (byte)'2' => true, (byte)'3' => true, (byte)'4' => true,
                    (byte)'5' => true, (byte)'6' => true, (byte)'7' => true, (byte)'8' => true, (byte)'9' => true,
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
            if (!TestNextOneOrMore(b => b switch {
                    (byte)'0' => true, (byte)'1' => true, (byte)'2' => true, (byte)'3' => true, (byte)'4' => true,
                    (byte)'5' => true, (byte)'6' => true, (byte)'7' => true, (byte)'8' => true, (byte)'9' => true,
                    (byte)'a' => true, (byte)'b' => true, (byte)'c' => true, (byte)'d' => true, (byte)'e' => true, (byte)'f' => true,
                    (byte)'A' => true, (byte)'B' => true, (byte)'C' => true, (byte)'D' => true, (byte)'E' => true, (byte)'F' => true,
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
            if (!TestNextOneOrMore(b => b switch {
                    (byte)'0' => true, (byte)'1' => true, (byte)'2' => true, (byte)'3' => true, (byte)'4' => true,
                    (byte)'5' => true, (byte)'6' => true, (byte)'7' => true, (byte)'8' => true, (byte)'9' => true,
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

        byte[] utf8bom = [ 0xef, 0xbb, 0xbf ];
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
}
