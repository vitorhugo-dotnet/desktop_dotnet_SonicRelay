using System.Text;
using System.Text.Json;

namespace SonicRelay.Platform.Linux.Audio;

public sealed record PipeWireNode(string NodeName, string DisplayName, bool IsAudioSink);

/// <summary>
/// Parses `pw-dump` JSON output into audio sink nodes. Malformed or oversized
/// output returns an empty list and never throws, so a bad discovery run
/// cannot crash Settings (spec: docs/superpowers/specs/2026-07-14-linux-desktop-publisher-design.md).
/// </summary>
public static class PipeWireNodeParser
{
    private const int MaxInputBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<PipeWireNode> ParseSinks(string pwDumpJson)
    {
        if (string.IsNullOrWhiteSpace(pwDumpJson) || Encoding.UTF8.GetByteCount(pwDumpJson) > MaxInputBytes)
        {
            return [];
        }

        JsonDocument document;
        try { document = JsonDocument.Parse(pwDumpJson); }
        catch (JsonException) { return []; }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
            var nodes = new List<PipeWireNode>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (TryParseSink(element, out var node)) nodes.Add(node);
            }
            return nodes;
        }
    }

    private static bool TryParseSink(JsonElement element, out PipeWireNode node)
    {
        node = null!;
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (!element.TryGetProperty("type", out var typeElement) ||
            typeElement.ValueKind != JsonValueKind.String ||
            typeElement.GetString() != "PipeWire:Interface:Node") return false;
        if (!element.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object) return false;
        if (!info.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object) return false;

        var mediaClass = GetString(props, "media.class");
        if (mediaClass is null || !mediaClass.Contains("Audio/Sink", StringComparison.Ordinal)) return false;

        var nodeName = GetString(props, "node.name");
        if (string.IsNullOrWhiteSpace(nodeName)) return false;

        var displayName = GetString(props, "node.description")
            ?? GetString(props, "device.description")
            ?? GetString(props, "node.nick")
            ?? nodeName;

        node = new PipeWireNode(nodeName, displayName, true);
        return true;
    }

    private static string? GetString(JsonElement props, string propertyName) =>
        props.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
