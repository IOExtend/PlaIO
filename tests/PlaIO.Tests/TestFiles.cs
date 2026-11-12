namespace PlaIO.Tests;

/// <summary>Where the tests find private sample files that are not part of the repository. The folder is named by the
/// environment variable PLAIO_TEST_FILES; when it is not set, the folder test_files in the repository root (or in an ancestor
/// of the test binary) is used. Files may lie directly in the folder or in one level of subfolders. A test that needs a file
/// which is not there is reported as skipped with the reason, never passed.</summary>
internal static class TestFiles
{
    public const string EnvironmentVariable = "PLAIO_TEST_FILES";

    /// <summary>Folders searched for fixtures, the one named by the environment variable first.</summary>
    public static IEnumerable<string> Roots()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured && Directory.Exists(configured))
            yield return configured;
        for (var directory = AppContext.BaseDirectory; directory != null; directory = Path.GetDirectoryName(directory))
        {
            string candidate = Path.Combine(directory, "test_files");
            if (Directory.Exists(candidate)) yield return candidate;
        }
    }

    /// <summary>The path of the fixture with this file name, or null when it is not available.</summary>
    public static string? TryFind(string fileName)
    {
        foreach (string root in Roots())
        {
            string direct = Path.Combine(root, fileName);
            if (File.Exists(direct)) return direct;
            foreach (string subfolder in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
            {
                string nested = Path.Combine(subfolder, fileName);
                if (File.Exists(nested)) return nested;
            }
        }
        return null;
    }

    /// <summary>The path of the fixture, or a <see cref="SkipException"/> when it is not available.</summary>
    public static string Find(string fileName) => TryFind(fileName) ?? throw Missing(fileName);

    /// <summary>Every fixture with one of the extensions (case-insensitive) in the roots and one level of subfolders. A file
    /// name seen twice is taken once: the first root and the first folder win. Ordered by file name.</summary>
    public static IReadOnlyList<string> FindAll(params string[] extensions)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in Roots())
        {
            var folders = new[] { root }.Concat(Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal));
            foreach (string folder in folders)
                foreach (string file in Directory.EnumerateFiles(folder).Order(StringComparer.Ordinal))
                    if (extensions.Any(e => Path.GetExtension(file).Equals(e, StringComparison.OrdinalIgnoreCase)))
                        found.TryAdd(Path.GetFileName(file), file);
        }
        return found.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value).ToList();
    }

    public static SkipException Missing(string what)
    {
        string hint = Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured && !Directory.Exists(configured)
            ? $"{EnvironmentVariable} is set but does not name an existing folder."
            : $"Set {EnvironmentVariable} to the folder that holds the private test files.";
        return new($"Private fixture '{what.Replace('\\', '/')}' is not available. {hint}");
    }
}
