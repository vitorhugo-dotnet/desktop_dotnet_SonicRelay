using SonicRelay.Platform.Linux.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class WpctlInspectParserTests
{
    private const string SampleInspectOutput = """
    id 55, type PipeWire:Interface:Node
     * priority.session = "1000"
     * node.name = "alsa_output.pci-0000_00_1f.3.analog-stereo"
     * node.description = "Built-in Audio Analog Stereo"
     object.serial = "42"
    """;

    [Fact]
    public void ParseExtractsNodeNameAndObjectSerial()
    {
        var resolved = WpctlInspectParser.Parse(SampleInspectOutput);

        Assert.NotNull(resolved);
        Assert.Equal("alsa_output.pci-0000_00_1f.3.analog-stereo", resolved!.NodeName);
        Assert.Equal("42", resolved.ObjectSerial);
    }

    [Fact]
    public void ParseReturnsNullWhenNodeNameIsMissing()
    {
        Assert.Null(WpctlInspectParser.Parse("id 55, type PipeWire:Interface:Node\n * priority.session = \"1000\""));
    }

    [Fact]
    public void ParseReturnsNullForEmptyInput()
    {
        Assert.Null(WpctlInspectParser.Parse(string.Empty));
    }

    [Fact]
    public void ParseReturnsNullForWhitespaceOnlyInput()
    {
        Assert.Null(WpctlInspectParser.Parse("   \n\t  \n"));
    }

    [Fact]
    public void ParseIgnoresLinesWithNoEqualsSeparator()
    {
        const string output = """
        id 55, type PipeWire:Interface:Node
        this line has no separator at all
         * node.name = "alsa_output.default"
        """;

        var resolved = WpctlInspectParser.Parse(output);

        Assert.NotNull(resolved);
        Assert.Equal("alsa_output.default", resolved!.NodeName);
    }

    [Fact]
    public void ParseIgnoresBlankLines()
    {
        const string output = """
        id 55, type PipeWire:Interface:Node

         * node.name = "alsa_output.default"

        """;

        var resolved = WpctlInspectParser.Parse(output);

        Assert.NotNull(resolved);
        Assert.Equal("alsa_output.default", resolved!.NodeName);
    }

    [Fact]
    public void ParseHandlesALoneAsteriskLineWithoutThrowing()
    {
        const string output = """
        id 55, type PipeWire:Interface:Node
         *
         * node.name = "alsa_output.default"
        """;

        var resolved = WpctlInspectParser.Parse(output);

        Assert.NotNull(resolved);
        Assert.Equal("alsa_output.default", resolved!.NodeName);
    }

    [Fact]
    public void ParseHandlesKeyWithEmptyValue()
    {
        const string output = """
        id 55, type PipeWire:Interface:Node
         * node.description =
         * node.name = "alsa_output.default"
        """;

        var resolved = WpctlInspectParser.Parse(output);

        Assert.NotNull(resolved);
        Assert.Equal("alsa_output.default", resolved!.NodeName);
    }

    [Fact]
    public void ParseReturnsNullWhenObjectSerialIsMissing()
    {
        var resolved = WpctlInspectParser.Parse("""
        id 55, type PipeWire:Interface:Node
         * node.name = "alsa_output.default"
        """);

        Assert.NotNull(resolved);
        Assert.Equal("alsa_output.default", resolved!.NodeName);
        Assert.Null(resolved.ObjectSerial);
    }

    [Fact]
    public void ParseHandlesCarriageReturnLineEndings()
    {
        var resolved = WpctlInspectParser.Parse(
            "id 55, type PipeWire:Interface:Node\r\n * node.name = \"alsa_output.default\"\r\n object.serial = \"42\"\r\n");

        Assert.NotNull(resolved);
        Assert.Equal("alsa_output.default", resolved!.NodeName);
        Assert.Equal("42", resolved.ObjectSerial);
    }
}
