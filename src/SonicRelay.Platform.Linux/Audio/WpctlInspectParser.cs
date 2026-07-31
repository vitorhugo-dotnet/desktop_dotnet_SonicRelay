namespace SonicRelay.Platform.Linux.Audio;

public sealed record ResolvedSink(string NodeName, string? ObjectSerial);

/// <summary>
/// Parses the plain-text tree `wpctl inspect &lt;id&gt;` prints. Malformed or
/// unexpected input never throws — lines without a recognizable key/value
/// shape are simply skipped, and a missing `node.name` yields null.
/// </summary>
public static class WpctlInspectParser
{
    public static ResolvedSink? Parse(string wpctlInspectOutput)
    {
        if (string.IsNullOrWhiteSpace(wpctlInspectOutput)) return null;

        string? nodeName = null;
        string? objectSerial = null;
        foreach (var rawLine in wpctlInspectOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('*')) line = line[1..].Trim();
            var separatorIndex = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separatorIndex < 0) continue;
            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 3)..].Trim().Trim('"');
            if (key == "node.name") nodeName = value;
            else if (key == "object.serial") objectSerial = value;
        }

        return nodeName is null ? null : new ResolvedSink(nodeName, objectSerial);
    }
}
