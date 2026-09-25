using Avalonia;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using OpenFan.Core;
using OpenFan.Core.Config;
using OpenFan.Core.Hardware;
using OpenFan.Core.Hud;

namespace OpenFan.Linux.App;

/// <summary>
/// Tray page = desktop-overlay setup: turn the HUD on, choose which sensors get a tile, give each tile a
/// background colour, and order them. The overlay itself is the live preview — no separate mock-up to
//  drift away from what actually renders.
/// </summary>
public partial class MainWindow
{
    private bool _hudPageBuilt;
    private readonly StackPanel _hudRows = new() { Spacing = 6 };
    private CheckBox? _hudOnBox;
    private ComboBox? _hudColumnsBox;
    private ComboBox? _hudSizeBox;
    private CheckBox? _hudTopmostBox;
    private bool _hudSyncing;   // suppress handlers while re-reading settings the overlay just changed

    private void EnsureHudPage()
    {
        if (_hudPageBuilt)
            return;
        _hudPageBuilt = true;
        TrayContent.Children.Clear();

        // ---- enable + layout card ---------------------------------------------------------------
        _hudOnBox = new CheckBox
        {
            Content = "Show the desktop overlay",
            IsChecked = _app.Settings.HudEnabled,
            Foreground = ValueText,
        };
        _hudOnBox.IsCheckedChanged += (_, _) =>
        {
            if (_hudSyncing) return;
            var on = _hudOnBox.IsChecked == true;
            (Application.Current as App)?.SetHudVisible(on);
            RebuildHudRows();
        };

        var columns = new ComboBox
        {
            ItemsSource = new[] { "1 (column)", "2", "3", "4" },
            SelectedIndex = Math.Clamp(_app.Settings.HudColumns, 1, 4) - 1,
            MinWidth = 110,
        };
        _hudColumnsBox = columns;
        columns.SelectionChanged += (_, _) =>
        {
            if (_hudSyncing || columns.SelectedIndex < 0) return;
            _app.Settings.HudColumns = HudLayout.ClampColumns(columns.SelectedIndex + 1);
            _app.Save();
            (Application.Current as App)?.HudRefreshNow();
        };

        var sizes = new ComboBox
        {
            ItemsSource = new[] { "Small", "Normal", "Large", "Huge" },
            SelectedIndex = SizeIndex(_app.Settings.HudScale),
            MinWidth = 110,
        };
        _hudSizeBox = sizes;
        sizes.SelectionChanged += (_, _) =>
        {
            if (_hudSyncing || sizes.SelectedIndex < 0) return;
            _app.Settings.HudScale = HudLayout.ClampScale(SizeScales[sizes.SelectedIndex]);
            _app.Save();
            (Application.Current as App)?.HudRefreshNow();
        };

        _hudTopmostBox = new CheckBox
        {
            Content = "Keep above other windows",
            IsChecked = _app.Settings.HudTopMost,
            Foreground = ValueText,
        };
        _hudTopmostBox.IsCheckedChanged += (_, _) =>
        {
            if (_hudSyncing) return;
            _app.Settings.HudTopMost = _hudTopmostBox.IsChecked == true;
            _app.Save();
            (Application.Current as App)?.HudApplyTopMost();
        };

        TrayContent.Children.Add(Card(
            "Desktop overlay",
            "A borderless strip of live sensor tiles — HWiNFO64-style. The tray icon cannot do this: GNOME renders tray items as icons only, with no text and themed colour.",
            _hudOnBox,
            LabeledRow("Tiles per row", columns),
            LabeledRow("Overlay size", sizes),
            _hudTopmostBox));

        // ---- tiles card -------------------------------------------------------------------------
        var add = new Button { Content = "Add sensor…", Classes = { "accent" } };
        add.Flyout = AddSensorMenu();

        TrayContent.Children.Add(Card(
            "Tiles",
            "Top of this list is the left of the overlay. Each tile gets its own background colour; text colour is chosen for contrast automatically.",
            _hudRows,
            add));

        // The overlay's own right-click menu can hide it or change columns; re-read rather than let the
        // page keep asserting a state that is no longer true.
        App.HudUiSync = SyncHudControls;

        RebuildHudRows();
    }

    private void SyncHudControls()
    {
        if (!_hudPageBuilt)
            return;

        _hudSyncing = true;
        try
        {
            if (_hudOnBox is not null) _hudOnBox.IsChecked = _app.Settings.HudEnabled;
            if (_hudColumnsBox is not null)
                _hudColumnsBox.SelectedIndex = HudLayout.ClampColumns(_app.Settings.HudColumns) - 1;
            if (_hudSizeBox is not null)
                _hudSizeBox.SelectedIndex = SizeIndex(_app.Settings.HudScale);
            if (_hudTopmostBox is not null)
                _hudTopmostBox.IsChecked = _app.Settings.HudTopMost;
        }
        finally
        {
            _hudSyncing = false;
        }
    }

    /// <summary>Re-renders the tile rows from settings. Called on any add/remove/reorder/colour change.</summary>
    private void RebuildHudRows()
    {
        if (!_hudPageBuilt)
            return;

        _hudRows.Children.Clear();
        var tiles = _app.Settings.HudTiles;

        if (tiles.Count == 0)
        {
            _hudRows.Children.Add(new TextBlock
            {
                Text = _app.Settings.HudEnabled
                    ? "No tiles yet — add one below."
                    : "Nothing configured yet. Turning the overlay on seeds CPU power, CPU temp and each GPU's power and temp.",
                Foreground = Secondary,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        for (var i = 0; i < tiles.Count; i++)
            _hudRows.Children.Add(HudRow(tiles[i], i, tiles.Count));
    }

    private Control HudRow(HudTileSettings tile, int index, int total)
    {
        var swatch = new Button
        {
            Width = 34,
            Height = 26,
            Padding = new Thickness(3),
            Content = new Border
            {
                Background = BrushFromHex(tile.ColorHex),
                CornerRadius = new CornerRadius(4),
            },
            Flyout = ColorMenu(tile),
        };
        ToolTip.SetTip(swatch, "Tile background colour");

        // Click-to-rename, same interaction as the fan cards and Sensors page: commit on blur or Enter.
        var name = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(tile.Label) ? HudFormat.LabelFor(tile.SourceId) : tile.Label,
            Classes = { "cardname" },
            Margin = new Thickness(6, 0, 6, 0),
        };
        ToolTip.SetTip(name, $"{tile.SourceId}\nClick to rename this tile.");
        name.LostFocus += (_, _) => CommitLabel(tile, name);
        name.KeyDown += (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter)
                TopLevel.GetTopLevel(name)?.FocusManager?.ClearFocus();   // blur → commit
        };

        var unit = new TextBlock
        {
            Text = HudFormat.UnitFor(tile.SourceId),
            Foreground = Secondary,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 30,
        };

        var up = MiniButton("↑", "Move left/up");
        up.IsEnabled = index > 0;
        up.Click += (_, _) => MoveTile(index, -1);

        var down = MiniButton("↓", "Move right/down");
        down.IsEnabled = index < total - 1;
        down.Click += (_, _) => MoveTile(index, +1);

        var remove = MiniButton("✕", "Remove tile");
        remove.Click += (_, _) =>
        {
            _app.Settings.HudTiles.RemoveAt(index);
            _app.Save();
            (Application.Current as App)?.HudRefreshNow();
            RebuildHudRows();
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto,Auto") };
        Grid.SetColumn(swatch, 0);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(unit, 2);
        Grid.SetColumn(up, 3);
        Grid.SetColumn(down, 4);
        Grid.SetColumn(remove, 5);
        grid.Children.Add(swatch);
        grid.Children.Add(name);
        grid.Children.Add(unit);
        grid.Children.Add(up);
        grid.Children.Add(down);
        grid.Children.Add(remove);
        return grid;
    }

    /// <summary>
    /// Persists a renamed tile. An empty name falls back to the sensor-derived label rather than leaving a
    /// blank tile; rebuilding the row list is skipped because Refresh() re-reads settings on the next tick.
    /// </summary>
    private void CommitLabel(HudTileSettings tile, TextBox box)
    {
        var text = (box.Text ?? "").Trim();
        if (text.Length == 0)
        {
            tile.Label = null;
            box.Text = HudFormat.LabelFor(tile.SourceId);
        }
        else if (text != tile.Label)
        {
            tile.Label = text;
        }
        else
        {
            return;   // nothing changed; don't touch the disk
        }

        _app.Save();
        (Application.Current as App)?.HudRefreshNow();
    }

    private static readonly double[] SizeScales = [0.75, 1.0, 1.35, 1.8];

    private static int SizeIndex(double scale)
    {
        var clamped = HudLayout.ClampScale(scale);
        var best = 1;
        var bestDelta = double.MaxValue;
        for (var i = 0; i < SizeScales.Length; i++)
        {
            var delta = Math.Abs(SizeScales[i] - clamped);
            if (delta < bestDelta) { bestDelta = delta; best = i; }
        }
        return best;
    }

    private void MoveTile(int index, int delta)
    {
        var tiles = _app.Settings.HudTiles;
        var target = index + delta;
        if (target < 0 || target >= tiles.Count)
            return;

        (tiles[index], tiles[target]) = (tiles[target], tiles[index]);
        _app.Save();
        (Application.Current as App)?.HudRefreshNow();
        RebuildHudRows();
    }

    // ---- menus -------------------------------------------------------------------------------

    /// <summary>Palette first (one click, chosen for contrast), then a full colour editor.</summary>
    private MenuFlyout ColorMenu(HudTileSettings tile)
    {
        var flyout = new MenuFlyout();

        foreach (var hex in HudTheme.Palette)
        {
            var captured = hex;
            var item = new MenuItem
            {
                Header = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Border
                        {
                            Width = 16, Height = 16, CornerRadius = new CornerRadius(3),
                            Background = BrushFromHex(captured),
                        },
                        new TextBlock { Text = captured, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            item.Click += (_, _) => SetTileColour(tile, captured);
            flyout.Items.Add(item);
        }

        flyout.Items.Add(new Separator());
        var custom = new MenuItem { Header = "Custom colour…" };
        custom.Click += async (_, _) =>
        {
            var picked = await HudColorDialog.PickAsync(this, tile.ColorHex);
            if (picked is not null)
                SetTileColour(tile, picked);
        };
        flyout.Items.Add(custom);

        return flyout;
    }

    private void SetTileColour(HudTileSettings tile, string hex)
    {
        tile.ColorHex = hex;
        _app.Save();
        (Application.Current as App)?.HudRefreshNow();
        RebuildHudRows();
    }

    /// <summary>
    /// Sources offered: the three CPU readings plus every temperature sensor in the same inventory the
    /// fan curves use, plus each GPU's board power. GPU power is not in that inventory (curves never
    /// drive off wattage), so it comes from the NVML snapshot instead — and only appears when NVML works.
    /// </summary>
    private MenuFlyout AddSensorMenu()
    {
        var flyout = new MenuFlyout();

        var cpu = new MenuItem { Header = "CPU" };
        cpu.Items.Add(AddItem("Socket power (PPT draw)", "cpu:power:w", "CPU power"));
        cpu.Items.Add(AddItem("Power limit (cap)", "cpu:pptcap:w", "CPU cap"));
        cpu.Items.Add(AddItem("Package temperature", "cpu:temp:c", "CPU temp"));
        flyout.Items.Add(cpu);

        var gpus = new MenuItem { Header = "GPU power" };
        foreach (var g in _app.Nvml.SnapshotAll())
        {
            var label = $"GPU {g.Index + 1} — {HudFormat.LabelFor("nvml:" + g.Uuid + ":x", g.Name, g.Index)}";
            gpus.Items.Add(AddItem(label, $"nvml:{g.Uuid}:power:w", $"GPU {g.Index + 1} power"));
        }
        if (gpus.Items.Count == 0)
        {
            var none = new MenuItem { Header = "no NVIDIA GPUs found", IsEnabled = false };
            gpus.Items.Add(none);
        }
        flyout.Items.Add(gpus);

        var temps = new MenuItem { Header = "Temperatures" };
        foreach (var item in _app.Inventory.Where(i => i.Kind == HardwareKind.Temperature))
        {
            var friendly = _app.Settings.SensorNicknames.TryGetValue(item.Id, out var nick) ? nick : item.Name;
            temps.Items.Add(AddItem(friendly, item.Id, null));
        }
        if (temps.Items.Count == 0)
            temps.Items.Add(new MenuItem { Header = "no temperature sensors", IsEnabled = false });
        flyout.Items.Add(temps);

        return flyout;
    }

    private MenuItem AddItem(string header, string sourceId, string? label)
    {
        var already = _app.Settings.HudTiles.Any(t => t.SourceId == sourceId);
        var item = new MenuItem { Header = already ? header + "  ✓" : header, IsEnabled = !already };
        item.Click += (_, _) =>
        {
            // Next unused palette colour, so a strip of tiles stays distinguishable without the user
            // having to think about it.
            var used = _app.Settings.HudTiles.Select(t => t.ColorHex).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var colour = HudTheme.Palette.FirstOrDefault(c => !used.Contains(c)) ?? HudTheme.Palette[0];

            _app.Settings.HudTiles.Add(new HudTileSettings
            {
                SourceId = sourceId,
                Label = label ?? HudFormat.LabelFor(sourceId),
                ColorHex = colour,
            });
            _app.Save();
            (Application.Current as App)?.HudRefreshNow();
            RebuildHudRows();
        };
        return item;
    }

    // ---- small builders ------------------------------------------------------------------------

    private static Button MiniButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = glyph,
            Padding = new Thickness(7, 2, 7, 3),
            Margin = new Thickness(3, 0, 0, 0),
            FontSize = 12,
        };
        ToolTip.SetTip(b, tip);
        return b;
    }

    private static IBrush BrushFromHex(string hex)
        => HudTheme.TryParse(hex, out var r, out var g, out var b)
            ? new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b))
            : new SolidColorBrush(Color.Parse(AccentHex.Default));

    private static Control LabeledRow(string label, Control input)
    {
        var text = new TextBlock
        {
            Text = label, Foreground = Secondary, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
        };
        // Both children need an explicit column: without it the input lands on top of the label
        // (the same Avalonia default-Column-0 trap already catalogued in STATUS §4).
        Grid.SetColumn(text, 0);
        Grid.SetColumn(input, 1);
        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            HorizontalAlignment = HorizontalAlignment.Left,
            Children = { text, input },
        };
    }

    private static Control Card(string title, string blurb, params Control[] body)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = ValueText });
        if (!string.IsNullOrWhiteSpace(blurb))
            panel.Children.Add(new TextBlock { Text = blurb, FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap });
        foreach (var c in body)
            panel.Children.Add(c);

        return new Border
        {
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Child = panel,
        };
    }
}
