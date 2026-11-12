using System.Security.Cryptography;
using System.Text.Json;
using Xunit;

namespace PlaIO.Tests;

/// <summary>The private vector folder (inputs and expected outputs cut from sample files), named only by the environment
/// variable PLAIO_TEST_VECTORS. Vector names are read from its manifest at run time and never written into the code.</summary>
internal static class TestVectors
{
    public const string EnvironmentVariable = "PLAIO_TEST_VECTORS";

    public static string Folder() =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } folder && Directory.Exists(folder)
            ? folder
            : throw new SkipException($"{EnvironmentVariable} is not set or does not name a folder; vector tests need the private vector folder.");

    /// <summary>The entries of manifest.json ({spec, vectors:[...]}).</summary>
    public static IReadOnlyList<JsonElement> Entries()
    {
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Folder(), "manifest.json")));
        return manifest.RootElement.GetProperty("vectors").EnumerateArray().Select(e => e.Clone()).ToList();
    }

    public static IEnumerable<JsonElement> OfKind(string kind) =>
        Entries().Where(e => e.GetProperty("kind").GetString() == kind);

    public static string Name(JsonElement entry) => entry.GetProperty("file").GetString()!;

    /// <summary>The bytes of a vector, checked against the SHA-256 in the manifest.</summary>
    public static byte[] Read(string name)
    {
        Assert.True(Path.GetFileName(name) == name, $"Vector name '{name}' is not a plain file name.");
        var entry = Entries().Single(e => Name(e) == name);
        byte[] bytes = File.ReadAllBytes(Path.Combine(Folder(), name));
        string actual = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.True(string.Equals(actual, entry.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase),
            $"Vector {name}: SHA-256 does not match the manifest.");
        return bytes;
    }
}
