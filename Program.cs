using System.Diagnostics;

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
        switch (Path.GetExtension(path))
        {
            case ".unity":
                return true;
        }
        return false;
    }

    private static void PassThrough()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        byte[] buffer = new byte[BufferSize];
        while (true)
        {
            int size = input.Read(buffer, 0, BufferSize);
            if (size <= 0)
                break;
            output.Write(buffer, 0, size);
            output.Flush();
        }
    }

    private static void WrapProcess(Process process)
    {
        var inputTask = Task.Run(() => {
            using var input = Console.OpenStandardInput();
            byte[] buffer = new byte[BufferSize];
            while (true)
            {
                int size = input.Read(buffer, 0, BufferSize);
                if (size <= 0)
                    break;
                process.StandardInput.BaseStream.Write(buffer, 0, size);
                process.StandardInput.BaseStream.Flush();
            }
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
            PassThrough();
            return 0;
        }

        ///cSpell:ignore tgzip

        var sevenZip = Process.Start(new ProcessStartInfo()
        {
            FileName = "7z",
            ArgumentList = {"x", "-si", "-so", "-an", "-tgzip"},
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

        WrapProcess(sevenZip);
        return 0;
    }

    private static int Clean(string path)
    {
        if (!ShouldCompress(path))
        {
            PassThrough();
            return 0;
        }

        var sevenZip = Process.Start(new ProcessStartInfo()
        {
            FileName = "7z",
            ArgumentList = {"a", "-si", "-so", "-an", "-tgzip"},
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

        WrapProcess(sevenZip);
        return 0;
    }
}
