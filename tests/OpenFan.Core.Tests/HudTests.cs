using System.Text.Json;
using FluentAssertions;
using OpenFan.Core.Config;
using OpenFan.Core.Hardware;
using OpenFan.Core.Platform;
using OpenFan.Core.Hud;
using Xunit;

namespace OpenFan.Core.Tests;

/// <summary>
/// Overlay logic that must be right without a display: unit inference, tile formatting, contrast choice
/// on colours the user picks freely, and that the new settings actually round-trip.
/// </summary>
public class HudTests
{
    [Theory]
    [InlineData("cpu:power:w", "W")]
    [InlineData("nvml:GPU-abc:power:w", "W")]
    [InlineData("nvml:GPU-abc:temp:core", "°C")]
    [InlineData("hwmon:k10temp:0:temp:Tctl", "°C")]
    [InlineData("hwmon:nct6799:10:fan:1", "RPM")]   // hwmon ":fan:" is a tachometer, unlike NVML
    [InlineData("nvml:GPU-abc:fan:0", "%")]
    public void Unit_is_inferred_from_source_id(string id, string expected) => HudFormat.UnitFor(id).Should().Be(expected);

    [Fact]
    public void Values_render_whole_and_never_fabricate_zero()
    {
        HudFormat.Value("cpu:power:w", 269.7).Should().Be("270 W");
        HudFormat.Value("cpu:temp:c", 41.2).Should().Be("41 °C");

        // A missing reading must not look like an idle 0 W CPU.
        HudFormat.Value("cpu:power:w", null).Should().Be("—");
        HudFormat.Value("cpu:power:w", double.NaN).Should().Be("—");
    }

    [Theory]
    [InlineData("#FFFFFF", "#141414")]   // white tile → dark text
    [InlineData("#F0A03C", "#141414")]   // accent amber is bright enough for dark text
    [InlineData("#171B1F", "#FFFFFF")]   // near-black tile → light text
    [InlineData("#2C363D", "#FFFFFF")]
    [InlineData("not-a-colour", "#FFFFFF")] // garbage must degrade safely, not throw
    public void Text_colour_stays_readable_on_the_chosen_background(string bg, string expected) =>
        HudTheme.TextColorFor(bg).Should().Be(expected);

    [Theory]
    [InlineData("#abc", true)]
    [InlineData("#AABBCC", true)]
    [InlineData("#AB", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Hex_parsing_accepts_only_real_colours(string? hex, bool ok) =>
        HudTheme.TryParse(hex, out _, out _, out _).Should().Be(ok);

    [Fact]
    public void Palette_colours_all_get_a_readable_text_colour()
    {
        foreach (var c in HudTheme.Palette)
        {
            HudTheme.TryParse(c, out _, out _, out _).Should().BeTrue(c);
            HudTheme.TextColorFor(c).Should().NotBe(c, "text must not match its own background");
        }
    }

    [Fact]
    public void Default_seed_covers_cpu_and_every_gpu_power_and_temp()
    {
        var tiles = HudDefaults.Seed(
        [
            new HudDefaults.GpuRef("GPU-1", 0, "NVIDIA RTX PRO 6000"),
            new HudDefaults.GpuRef("GPU-2", 1, "NVIDIA RTX 4090"),
        ]);

        tiles.Select(t => t.SourceId).Should().Contain(["cpu:power:w", "cpu:temp:c"]);
        tiles.Should().Contain(t => t.SourceId == "nvml:GPU-1:power:w" && t.Label == "GPU 1 power");
        tiles.Should().Contain(t => t.SourceId == "nvml:GPU-2:temp:core" && t.Label == "GPU 2 temp");

        // Neighbouring tiles must not blur together; with more GPUs than palette colours a wrap is fine.
        for (var i = 1; i < tiles.Count; i++)
            tiles[i].ColorHex.Should().NotBe(tiles[i - 1].ColorHex, $"tile {i} sits next to an identical colour");
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(0, 1)]      // a hand-edited config must not produce a zero-column layout
    [InlineData(-3, 1)]
    [InlineData(99, 1)]     // absurd values collapse to one column rather than an empty strip
    public void Tiles_per_row_stays_in_the_legal_range(int raw, int expected) =>
        HudLayout.ClampColumns(raw).Should().Be(expected);

    [Theory]
    [InlineData(1.0, 1.0)]
    [InlineData(1.35, 1.35)]
    [InlineData(0.0, 1.0)]     // a hand-edited 0 must not make the overlay invisible
    [InlineData(-2.0, 1.0)]
    [InlineData(99.0, 1.0)]    // nor cover the whole screen
    [InlineData(double.NaN, 1.0)]
    public void Scale_stays_in_the_legal_range(double raw, double expected) =>
        HudLayout.ClampScale(raw).Should().Be(expected);

    [Fact]
    public void Tile_metrics_scale_together_so_text_does_not_outgrow_its_tile()
    {
        var normal = HudLayout.TileMetrics(1.0);
        var large = HudLayout.TileMetrics(1.35);

        large.Width.Should().BeApproximately(normal.Width * 1.35, 0.01);
        large.Height.Should().BeApproximately(normal.Height * 1.35, 0.01);
        large.ValueFont.Should().BeApproximately(normal.ValueFont * 1.35, 0.01);

        // Text must stay inside the tile at any scale, or "Large" becomes clipped numbers.
        large.ValueFont.Should().BeLessThan(large.Height);
        normal.ValueFont.Should().BeLessThan(normal.Height);
    }

    [Fact]
    public void Tooltip_describes_a_gpu_tile_by_name_not_by_uuid()
    {
        const string id = "nvml:GPU-d0f36b3b-abad-1a42-0e65-e78da2139da3:power:w";

        var text = HudDescribe.Of(id, "NVIDIA RTX PRO 6000 Blackwell Workstation Edition");

        text.Should().Contain("RTX PRO 6000");
        text.Should().Contain("board power");
        text.Should().NotContain("GPU-d0f36b3b", "a UUID in a tooltip tells the user nothing about which card it is");
    }

    [Fact]
    public void Drive_labels_lead_with_pci_so_truncation_cannot_hide_the_discriminator()
    {
        // Two identical WD_BLACK SN850X 8000GB drives in this box: the model alone cannot separate them.
        var a = HudDescribe.DeviceLabel("Composite", "0000:a9:00.0", "WD_BLACK SN850X 8000GB");
        var b = HudDescribe.DeviceLabel("Composite", "0000:ad:00.0", "WD_BLACK SN850X 8000GB");

        a.Should().Be("a9:00.0 Composite · WD_BLACK SN850X 8000GB");
        a.Should().NotBe(b);
        a.IndexOf("a9:00.0").Should().Be(0, "menus truncate the tail, so the discriminator must be first");
    }

    [Theory]
    [InlineData("Sensor 1", "", "Samsung SSD 970 EVO Plus 2TB", "Sensor 1 · Samsung SSD 970 EVO Plus 2TB")]
    [InlineData("Composite", "0000:c1:00.0", "", "c1:00.0 Composite")]     // no model: bus + kind still work
    [InlineData("Composite", "", "", "Composite")]
    public void Drive_labels_degrade_gracefully_without_one_of_the_parts(
        string kind, string pci, string model, string expected) =>
        HudDescribe.DeviceLabel(kind, pci, model).Should().Be(expected);

    [Fact]
    public void Identical_gpus_are_told_apart_by_pci_bus_in_the_tooltip()
    {
        // The user's box: two RTX PRO 6000 cards differing only by slot. Name-only tooltips are ambiguous.
        const string a = "nvml:GPU-11111111-2222-3333-4444-555555555555:power:w";
        const string b = "nvml:GPU-aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee:power:w";
        const string sameName = "NVIDIA RTX PRO 6000 Blackwell Workstation Edition";

        var tipA = HudDescribe.Of(a, sameName, pciBus: "0000:01:00.0", gpuIndex: 1);
        var tipB = HudDescribe.Of(b, sameName, pciBus: "0000:11:00.0", gpuIndex: 2);

        tipA.Should().Contain("01:00.0");
        tipB.Should().Contain("11:00.0");
        tipA.Should().NotBe(tipB, "two identical cards must not produce the same tooltip");
        tipA.Should().Contain("board power");
    }

    [Fact]
    public void Bus_is_not_duplicated_when_the_name_already_carries_it()
    {
        var tip = HudDescribe.Of(
            "nvml:GPU-x:temp:core", "RTX PRO 6000 Blackwell WS 11:00.0", pciBus: "0000:11:00.0");

        tip.Should().Be("RTX PRO 6000 Blackwell WS 11:00.0 — GPU temperature");
    }

    [Theory]
    [InlineData("cpu:power:w", "CPU socket power draw (HSMP)")]
    [InlineData("cpu:pptcap:w", "CPU socket power limit — PPT cap")]
    [InlineData("cpu:temp:c", "CPU package temperature")]
    public void Cpu_synthetics_read_as_plain_language(string id, string expected) =>
        HudDescribe.Of(id).Should().Be(expected);

    [Fact]
    public void Named_hwmon_sensors_show_their_name_and_group() =>
        HudDescribe.Of("hwmon:nct6799:10:temp:temp1", "CPU Optimal", "Motherboard").Should().Be("CPU Optimal (Motherboard)");

    [Fact]
    public void Unknown_source_without_a_name_falls_back_to_the_id_derived_label() =>
        HudDescribe.Of("hwmon:k10temp:0:temp:Tctl").Should().Contain("k10temp");

    [Theory]
    [InlineData(0)]                // 0 means automatic: say nothing, let the toolkit detect
    [InlineData(-5)]
    [InlineData(999)]              // an absurd pin is ignored rather than blinding the user
    public void Non_percentage_pins_are_ignored(double percent) =>
        UiScale.FactorsFromPercent(percent, new[] { "DP-2" }).Should().BeNull();

    [Fact]
    public void Pinned_scale_names_every_connector_because_wildcards_are_ignored()
    {
        // Avalonia's X11 backend matched this per connector in measurement: "*=2.4" did nothing,
        // "DP-2=2.4;DP-3=2.4" worked. A pin that silently does nothing is worse than no pin.
        UiScale.FactorsFromPercent(240, new[] { "DP-2", "DP-3" }).Should().Be("DP-2=2.4;DP-3=2.4");
        UiScale.FactorsFromPercent(125, new[] { "eDP-1" }).Should().Be("eDP-1=1.25");
    }

    [Fact]
    public void Pinned_scale_still_emits_something_with_no_connectors_enumerated() =>
        UiScale.FactorsFromPercent(150, Array.Empty<string>()).Should().Be("*=1.5");

    [Theory]
    [InlineData(96, null)]     // the default X reports before a HiDPI session publishes its real value
    [InlineData(0, null)]
    [InlineData(232, 2.4167)]
    [InlineData(192, 2.0)]
    public void Dpi_hint_is_ignored_until_it_says_something(double dpi, double? expected) =>
        UiScale.FactorFromDpi(dpi).Should().Be(expected);

    [Fact]
    public void Scale_percentage_label_is_readable() =>
        UiScale.PercentLabel(2.4167).Should().Be("241.7%");

    [Theory]
    [InlineData(0.56, 0.56)]   // XSmall is legal and distinct from Small
    [InlineData(0.49, 1.0)]    // below the floor falls back rather than rendering an unreadable strip
    public void Scale_floor_accommodates_xsmall_without_letting_it_vanish(double raw, double expected) =>
        HudLayout.ClampScale(raw).Should().Be(expected);

    [Fact]
    public void Off_screen_saved_position_comes_back_grabbable()
    {
        // Strip parked on a monitor that no longer exists: 1600x900 desktop, saved at x=2600.
        var screens = new[] { new HudLayout.ScreenBounds(0, 0, 1600, 900) };

        var (x, y) = HudLayout.ClampIntoBounds(2600, 40, 292, 210, screens);

        x.Should().BeLessThanOrEqualTo(1600 - 24, "some of the strip must land on screen");
        x.Should().BeGreaterThan(1600 - 292, "but it should hug the right edge, not teleport to the left");
        y.Should().Be(40, "an in-bounds axis is left alone");
    }

    [Fact]
    public void Negative_saved_position_is_pulled_back_not_left_off_the_top_left()
    {
        var screens = new[] { new HudLayout.ScreenBounds(0, 0, 1920, 1080) };
        var (x, y) = HudLayout.ClampIntoBounds(-400, -300, 152, 144, screens);

        x.Should().BeGreaterThanOrEqualTo(0);
        y.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Positions_on_a_second_monitor_are_left_exactly_where_the_user_put_them()
    {
        // Two screens side by side: the right one starts at x=1920. Nothing may shift.
        var screens = new[]
        {
            new HudLayout.ScreenBounds(0, 0, 1920, 1080),
            new HudLayout.ScreenBounds(1920, 0, 2560, 1440),
        };

        HudLayout.ClampIntoBounds(3200, 700, 152, 144, screens).Should().Be((3200, 700));
    }

    [Fact]
    public void Clamp_survives_unknown_screens_instead_of_hiding_the_strip()
    {
        // Headless / no screen info must not become "park the overlay at 0,0 forever".
        HudLayout.ClampIntoBounds(812, 344, 152, 144, []).Should().Be((812, 344));
    }

    [Fact]
    public void Overlay_settings_round_trip_and_order_is_the_list_order()
    {
        var s = new AppSettings
        {
            HudEnabled = true,
            HudColumns = 3,
            HudX = 40,
            HudY = 80,
            HudTiles =
            [
                new HudTileSettings { SourceId = "cpu:power:w", Label = "PPT", ColorHex = "#F0A03C" },
                new HudTileSettings { SourceId = "nvml:GPU-1:temp:core", ColorHex = "#B388FF" },
            ],
        };

        var back = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s))!;

        back.HudEnabled.Should().BeTrue();
        back.HudColumns.Should().Be(3);
        (back.HudX, back.HudY).Should().Be((40, 80));
        back.HudTiles.Select(t => t.SourceId).Should().ContainInOrder("cpu:power:w", "nvml:GPU-1:temp:core");
        back.HudTiles[0].Label.Should().Be("PPT");
        back.HudTiles[1].ColorHex.Should().Be("#B388FF");
    }

    [Fact]
    public void Unset_geometry_stays_null_rather_than_becoming_the_top_left_corner()
    {
        var json = JsonSerializer.Serialize(new AppSettings());
        json.Should().NotContain("hudX", "null geometry must be omitted, not written as 0");

        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        (back.HudX, back.HudY).Should().Be(((int?)null, null));
        back.HudTiles.Should().BeEmpty();
    }
}
