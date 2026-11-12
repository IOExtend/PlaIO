using System.Globalization;
using System.Reflection;

namespace PlaIO.CommandLine;

/// <summary>The command line of the <c>plaio</c> tool: usage text, argument parsing, the commands and the exit codes. It
/// uses only the public API of the library.</summary>
internal static class Cli
{
    public const int ExitOk = 0;
    public const int ExitUsage = 1;
    public const int ExitInput = 2;

    internal const string UsageText = """
        Usage: plaio info <file>
               plaio blocks <file>
               plaio directory <file> [--out <file.json>]
               plaio --help | --version

          info       Kind and length. FDB: header fields (hex), raw header bytes 32..63 (hex),
                     block counts per mode and per tag, number of blocks with flag 0x10.
                     BIMx: cover offset, cover length, content offset.
          blocks     FDB only. Tab-separated header line, then one line per indexed block:
                     offset tag id kind ver flags class mode packed unpacked extra payloadOffset guid
          directory  FDB only. Searches raw blocks for object directories; prints one summary line
                     per directory (blockOffset recordOffset phase records abutting overlapping duplicates);
                     with --out also writes all records as JSON.

        Exit codes: 0 done; 1 usage error (no/unknown command or option, missing, empty or extra argument,
                    --out given twice or without a value);
                    2 input or output file cannot be used (missing, unreadable, not a recognized file,
                      malformed, wrong kind for the command, write failed).
        """;

    internal static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0) return Usage(stderr, "No command given.");
        string command = args[0];
        switch (command)
        {
            case "--help" or "-h":
                stdout.WriteLine(UsageText);
                return ExitOk;
            case "--version":
                string version = typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
                stdout.WriteLine($"plaio {version.Split('+')[0]}");
                return ExitOk;
            case "info" or "blocks" or "directory":
                break;
            default:
                return Usage(stderr, command.StartsWith('-') ? $"Unknown option '{command}'." : $"Unknown command '{command}'.");
        }

        string? file = null, output = null;
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg == "--out" && command == "directory")
            {
                if (output != null) return Usage(stderr, "--out is given twice.");
                if (i + 1 >= args.Length || args[i + 1].Length == 0) return Usage(stderr, "--out needs a file name.");
                output = args[++i];
            }
            else if (arg.StartsWith('-') && arg.Length > 1)
                return Usage(stderr, $"Unknown option '{arg}'.");
            else if (arg.Length == 0)
                return Usage(stderr, "The file name is empty.");
            else if (file != null)
                return Usage(stderr, $"Unexpected argument '{arg}'.");
            else
                file = arg;
        }
        if (file == null) return Usage(stderr, $"'{command}' needs a file.");

        try
        {
            var kind = PlaFile.DetectKind(file);
            if (kind == PlaFileKind.Unknown) return Fail(stderr, "not a recognized .pla/.pln/.bimx file.");
            if (kind == PlaFileKind.Bimx && command != "info") return Fail(stderr, $"'{command}' needs a .pla or .pln file; this is a .bimx file.");
            switch (command)
            {
                case "info" when kind == PlaFileKind.Bimx:
                    InfoBimx(file, stdout);
                    break;
                case "info":
                    InfoFdb(file, stdout);
                    break;
                case "blocks":
                    BlocksCommand(file, stdout);
                    break;
                default:
                    DirectoryCommand(file, output, stdout);
                    break;
            }
            return ExitOk;
        }
        // ArgumentException: a path the OS rejects (e.g. an embedded NUL) or a file that is not seekable.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return Fail(stderr, e.Message);
        }
    }

    private static void InfoFdb(string file, TextWriter stdout)
    {
        using var document = PlaDocument.Open(file);
        var header = document.Header;
        stdout.WriteLine("kind: FDB");
        stdout.WriteLine($"length: {document.Length}");
        stdout.WriteLine($"layoutVersion: {Hex(header.LayoutVersion, 4)}");
        stdout.WriteLine($"formatVersion: {Hex(header.FormatVersion, 4)}");
        stdout.WriteLine($"field12: {Hex(header.Field12, 8)}");
        var raw = header.Raw.Span;
        stdout.WriteLine($"headerBytes32to63: {(raw.Length > 32 ? Convert.ToHexString(raw[32..]) : "-")}");
        stdout.WriteLine($"blocks: {document.Blocks.Count}");
        foreach (var mode in Enum.GetValues<PlaBlockMode>())
            stdout.WriteLine($"mode {mode}: {document.Blocks.Count(b => b.Mode == mode)}");
        foreach (string tag in new[] { "BL", "DY" })
            stdout.WriteLine($"tag {tag}: {document.Blocks.Count(b => b.Tag == tag)}");
        stdout.WriteLine($"flag 0x10: {document.Blocks.Count(b => (b.Flags & 0x10) != 0)}");
    }

    private static void InfoBimx(string file, TextWriter stdout)
    {
        using var document = BimxDocument.Open(file);
        stdout.WriteLine("kind: BIMx");
        stdout.WriteLine($"length: {document.Length}");
        stdout.WriteLine($"coverOffset: 0x{document.CoverOffset:x}");
        stdout.WriteLine($"coverLength: {document.CoverLength}");
        stdout.WriteLine($"contentOffset: 0x{document.ContentOffset:x}");
    }

    private static void BlocksCommand(string file, TextWriter stdout)
    {
        using var document = PlaDocument.Open(file);
        stdout.WriteLine("offset\ttag\tid\tkind\tver\tflags\tclass\tmode\tpacked\tunpacked\textra\tpayloadOffset\tguid");
        foreach (var b in document.Blocks)
        {
            stdout.WriteLine(string.Join('\t',
                $"0x{b.Offset:x}", b.Tag, Hex(b.Id, 8), Hex(b.Kind, 4), b.Version.ToString(CultureInfo.InvariantCulture),
                Hex(b.Flags, 4), b.SizeClass.ToString(CultureInfo.InvariantCulture), b.Mode.ToString(),
                b.PackedSize.ToString(CultureInfo.InvariantCulture), b.UnpackedSize.ToString(CultureInfo.InvariantCulture),
                b.Extra is { } extra ? Hex(extra, 8) : "-", $"0x{b.PayloadOffset:x}", b.Guid?.ToString("D") ?? "-"));
        }
    }

    private static void DirectoryCommand(string file, string? output, TextWriter stdout)
    {
        using var document = PlaDocument.Open(file);
        var directories = document.FindDirectories();
        stdout.WriteLine("blockOffset\trecordOffset\tphase\trecords\tabutting\toverlapping\tduplicates");
        foreach (var d in directories)
            stdout.WriteLine($"0x{d.Block.Offset:x}\t{d.RecordOffset}\t{d.Phase}\t{d.Records.Count}\t{d.AbuttingPairs}\t{d.OverlappingPairs}\t{d.DuplicatePositions}");
        if (output == null) return;
        // Written next to the target under a unique name and moved into place, so a failed write never leaves a truncated
        // JSON file behind and no existing file other than the target is touched.
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                DirectoryJson.Write(stream, document.Length, directories);
            File.Move(temporary, output, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            throw new IOException($"'{output}' cannot be written: {e.Message}", e);
        }
    }

    private static string Hex(ulong value, int digits) => "0x" + value.ToString("x" + digits, CultureInfo.InvariantCulture);

    private static int Usage(TextWriter stderr, string message)
    {
        stderr.WriteLine($"plaio: {message}");
        stderr.WriteLine("Run 'plaio --help' for usage.");
        return ExitUsage;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"plaio: {message}");
        return ExitInput;
    }
}
