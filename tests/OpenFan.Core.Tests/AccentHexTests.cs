using OpenFan.Core;

namespace OpenFan.Core.Tests;

public class AccentHexTests
{
    [Fact]
    public void Parses_rrggbb_and_rgb()
    {
        AccentHex.TryParse("#E24B4B", out var r, out var g, out var b).Should().BeTrue();
        r.Should().Be(0xE2);
        g.Should().Be(0x4B);
        b.Should().Be(0x4B);
        AccentHex.Normalize("#e24").Should().Be("#EE2244");
    }

    [Fact]
    public void Invalid_falls_back_to_default()
    {
        AccentHex.Normalize("nope").Should().Be(AccentHex.Default);
    }
}
