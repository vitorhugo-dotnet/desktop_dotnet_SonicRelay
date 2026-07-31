using SonicRelay.Platform.Linux.Audio;

namespace SonicRelay.Platform.Linux.Tests;

public sealed class PipeWireNodeParserTests
{
    private const string TwoSinksJson = """
    [
      {
        "type": "PipeWire:Interface:Node",
        "info": { "props": {
          "media.class": "Audio/Sink",
          "node.name": "alsa_output.pci-0000_00_1f.3.analog-stereo",
          "node.description": "Built-in Audio Analog Stereo"
        } }
      },
      {
        "type": "PipeWire:Interface:Node",
        "info": { "props": {
          "media.class": "Audio/Sink",
          "node.name": "bluez_output.AA_BB_CC.a2dp-sink"
        } }
      },
      {
        "type": "PipeWire:Interface:Node",
        "info": { "props": {
          "media.class": "Audio/Source",
          "node.name": "alsa_input.pci-0000_00_1f.3.analog-stereo"
        } }
      }
    ]
    """;

    [Fact]
    public void ParseSinksReturnsOnlyAudioSinkNodes()
    {
        var sinks = PipeWireNodeParser.ParseSinks(TwoSinksJson);

        Assert.Equal(2, sinks.Count);
        Assert.DoesNotContain(sinks, sink => sink.NodeName.Contains("alsa_input"));
    }

    [Fact]
    public void ParseSinksUsesDescriptionFallbackChain()
    {
        var sinks = PipeWireNodeParser.ParseSinks(TwoSinksJson);

        var withDescription = sinks.Single(s => s.NodeName == "alsa_output.pci-0000_00_1f.3.analog-stereo");
        Assert.Equal("Built-in Audio Analog Stereo", withDescription.DisplayName);

        var withoutDescription = sinks.Single(s => s.NodeName == "bluez_output.AA_BB_CC.a2dp-sink");
        Assert.Equal("bluez_output.AA_BB_CC.a2dp-sink", withoutDescription.DisplayName);
    }

    [Fact]
    public void ParseSinksReturnsEmptyForMalformedJson()
    {
        var sinks = PipeWireNodeParser.ParseSinks("{ not json");
        Assert.Empty(sinks);
    }

    [Fact]
    public void ParseSinksReturnsEmptyForOversizedInput()
    {
        var oversized = new string('x', 5 * 1024 * 1024);
        var sinks = PipeWireNodeParser.ParseSinks(oversized);
        Assert.Empty(sinks);
    }

    [Fact]
    public void ParseSinksReturnsEmptyForEmptyInput()
    {
        Assert.Empty(PipeWireNodeParser.ParseSinks(string.Empty));
    }

    [Fact]
    public void ParseSinksReturnsEmptyWhenTypeFieldIsNotAString()
    {
        var sinks = PipeWireNodeParser.ParseSinks("""[{"type": 123, "info": {"props": {"media.class": "Audio/Sink", "node.name": "x"}}}]""");
        Assert.Empty(sinks);
    }
}
