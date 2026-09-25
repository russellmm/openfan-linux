using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using OpenFan.Core.Hud;

namespace OpenFan.Linux.App;

/// <summary>
/// Colour chooser for an overlay tile when the eight curated swatches are not enough.
/// </summary>
/// <remarks>
/// Built from sliders rather than Avalonia's ColorView on purpose: ColorView ships in a separate
/// package with its own theme-resource requirements, and this dialog needs nothing beyond RGB — tile
/// alpha is meaningless (the strip paints solid) and text contrast is derived automatically.
/// </remarks>
public sealed class HudColorDialog : Window
{
    private readonly Slider _r = Channel("R", 0x2C);
    private readonly Slider _g = Channel("G", 0x36);
    private readonly Slider _b = Channel("B", 0x3D);
    private readonly Border _preview = new() { Width = 46, Height = 46, CornerRadius = new CornerRadius(8) };
    private readonly TextBox _hex = new() { Width = 110 };
    private readonly TextBlock _contrastNote = new() { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8FA0AA")) };

    private HudColorDialog(string initial)
    {
        Title = "Tile colour";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#171B1F"));

        if (HudTheme.TryParse(initial, out var r0, out var g0, out var b0))
        {
            _r.Value = r0;
            _g.Value = g0;
            _b.Value = b0;
        }

        foreach (var s in new[] { _r, _g, _b })
            s.PropertyChanged += (_, e) =>
            {
                if (e.Property == RangeBase.ValueProperty)
                    SyncFromSliders();
            };

        // Hex is an input too, not just a read-out — pasting #RRGGBB from a design tool is the common case.
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                ApplyHex(_hex.Text);
        };
        _hex.LostFocus += (_, _) => ApplyHex(_hex.Text);

        var ok = new Button { Content = "Use this colour", Classes = { "accent" }, IsDefault = true };
        ok.Click += (_, _) => Close(CurrentHex());

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close(null);

        SyncFromSliders();

        Content = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(16),
            Children =
            {
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*"),
                    Children =
                    {
                        Placed(_preview, 0),
                        new StackPanel
                        {
                            Spacing = 4,
                            Margin = new Thickness(12, 0, 0, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Children = { _contrastNote },
                        }.Also(p => Grid.SetColumn(p, 1)),
                    },
                },
                _r, _g, _b,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "hex", Foreground = new SolidColorBrush(Color.Parse("#8FA0AA")), VerticalAlignment = VerticalAlignment.Center },
                        _hex,
                    },
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, ok },
                },
            },
        };
    }

    /// <summary>Resolves to #rrggbb, or null when cancelled.</summary>
    public static async Task<string?> PickAsync(Window owner, string initial) =>
        await new HudColorDialog(initial).ShowDialog<string?>(owner);

    private void SyncFromSliders()
    {
        var hex = CurrentHex();
        _preview.Background = new SolidColorBrush(Color.FromRgb((byte)_r.Value, (byte)_g.Value, (byte)_b.Value));
        if (_hex.Text?.ToUpperInvariant() != hex)
            _hex.Text = hex;

        // Say what the overlay will actually do with this colour, so a pale pick is not a surprise.
        var dark = HudTheme.TextColorFor(hex) == "#141414";
        _contrastNote.Text = dark ? "tile text: dark" : "tile text: light";
    }

    private string CurrentHex() => $"#{(int)_r.Value:X2}{(int)_g.Value:X2}{(int)_b.Value:X2}";

    private void ApplyHex(string? text)
    {
        if (!HudTheme.TryParse(text, out var r, out var g, out var b))
            return;   // leave the sliders alone rather than half-applying garbage
        _r.Value = r;
        _g.Value = g;
        _b.Value = b;
        SyncFromSliders();
    }

    private static Slider Channel(string name, int initial)
    {
        var s = new Slider { Minimum = 0, Maximum = 255, Value = initial, Width = 240 };
        ToolTip.SetTip(s, $"{name} (0–255)");
        return s;
    }

    private static T Placed<T>(T control, int column) where T : Control
    {
        Grid.SetColumn(control, column);
        return control;
    }
}

file static class ControlExtensions
{
    public static T Also<T>(this T control, Action<T> act)
    {
        act(control);
        return control;
    }
}
