using System.Text.Json;

namespace PlaIO.CommandLine;

/// <summary>Streams the records of the directories found in a file as indented JSON. No file name is written, so the output
/// identifies nothing beyond its content.</summary>
internal static class DirectoryJson
{
    internal static void Write(Stream output, long length, IReadOnlyList<ObjectDirectory> directories)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("length", length);
        writer.WriteStartArray("directories");
        foreach (var d in directories)
        {
            writer.WriteStartObject();
            writer.WriteNumber("blockOffset", d.Block.Offset);
            writer.WriteNumber("payloadOffset", d.Block.PayloadOffset);
            writer.WriteNumber("recordOffset", d.RecordOffset);
            writer.WriteNumber("phase", d.Phase);
            writer.WriteNumber("recordCount", d.Records.Count);
            writer.WriteNumber("abuttingPairs", d.AbuttingPairs);
            writer.WriteNumber("overlappingPairs", d.OverlappingPairs);
            writer.WriteNumber("duplicatePositions", d.DuplicatePositions);
            writer.WriteStartArray("records");
            foreach (var r in d.Records)
            {
                writer.WriteStartObject();
                writer.WriteNumber("class", r.ClassNumber);
                writer.WriteNumber("pos", r.Position);
                writer.WriteNumber("count", r.Count);
                writer.WriteString("guid", r.ObjectId.ToString("D"));
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush(); // keep the buffered output small for large directories
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
