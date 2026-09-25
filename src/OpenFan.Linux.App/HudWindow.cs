using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using OpenFan.Core;
using OpenFan.Core.Config;
using OpenFan.Core.Hud;
using OpenFan.Linux.Hw;

namespace OpenFan.Linux.App;

/// <summary>
/// HWiNFO64-style desktop overlay: a borderless, always-on-top strip of sensor tiles the user colours
/// themselves. Reads only — it never touches sysfs writes or the helper socket.
/// </summary>
/// <remarks>
/// This exists instead of top-panel tray items because GNOME cannot do what the user needs there:
/// Avalonia's TrayIcon exposes Icon/ToolTipText/Menu/IsVisible/Command and no text, and Ubuntu's
/// appindicator extension renders panel text only from the legacy XAyatanaLabel SNI property with a
/// themed colour. Drawing our own window is the only way to get real text at a readable size with a
/// per-sensor background colour, without asking the user to install a Shell extension.
///
/// Two things worth preserving when editing: repaints are skipped unless a tile's *formatted* string
/// changed (this window redraws every refresh tick otherwise), and the tile visuals are rebuilt only
/// when the settings signature changes — not per tick.
/// </remarks>
public sealed class HudWindow : Window
{
    private readonly FanApp _app;
    private readonly CpuMonitor _cpu = new();
    private readonly UniformGrid _grid = new();
    private readonly List<(HudTileSettings Tile, TextBlock Value)> _views = [];
    private readonly List<string> _shown = [];   // last painted text per tile, for change detection
    private string _signature = "";
    private CpuMonitor.Snapshot? _cpuSnapshot;   // set per Refresh, cleared at its end
    private bool _dragging;
    private bool _saveQueued;
    private bool _applyingPosition;      // our own corrective move, not a user drag
    private int _restoreAttempts;        // bounded: a WM that fights us must not cause an infinite loop

    public HudWindow(FanApp app)
    {
        _app = app;

        SystemDecorations = SystemDecorations.None;
        Topmost = _app.Settings.HudTopMost;
        ShowInTaskbar = false;
        ShowActivated = false;   // an overlay that steals focus on every launch is worse than none
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Title = "OpenFan overlay";

        _grid.Margin = new Thickness(6);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#E6171B1F")),
            CornerRadius = new CornerRadius(10),
            Child = _grid,
        };

        // Drag anywhere on the strip; right-click for the small menu. Both are why this is a window
        // rather than a widget: the user has to be able to get it out of the way.
        PointerPressed += OnPointerPressed;
        ContextFlyout = BuildMenu();
        PositionChanged += OnPositionChanged;

        RestoreGeometry();
    }

    /// <summary>
    /// Re-assert the saved position after mapping. A borderless window is re-placed by the WM *after*
    /// Show() returns, so setting Position in the ctor lands the overlay at 0,0; OnPositionChanged
    /// therefore nudges it back a bounded number of times before accepting whatever the WM insists on.
    /// </summary>
    public void ApplySavedPosition()
    {
        _restoreAttempts = 0;
        MoveToSavedPosition();
    }

    private void MoveToSavedPosition()
    {
        var s = _app.Settings;
        if (s.HudX is not int x || s.HudY is not int y) return;

        var want = ClampToScreens(x, y, s);
        if (Position == want) return;
        _applyingPosition = true;
        Position = want;
        _applyingPosition = false;
    }

    /// <summary>
    /// Resolves the saved point against the screens that exist *now*. A strip parked on a monitor that has
    /// since been unplugged would otherwise return at coordinates nobody can see, and an invisible window
    /// cannot be dragged back — which looks like a broken overlay rather than a changed desktop. When the
    /// clamp moves it, the corrected position is saved so we do not re-fight this on every start.
    /// </summary>
    private PixelPoint ClampToScreens(int x, int y, AppSettings settings)
    {
        var screens = Screens?.All;
        if (screens is null || screens.Count == 0)
            return new PixelPoint(Math.Max(x, 0), Math.Max(y, 0));   // headless: pass through

        var bounds = screens
            .Select(sc => new HudLayout.ScreenBounds(sc.Bounds.X, sc.Bounds.Y, sc.Bounds.Width, sc.Bounds.Height))
            .ToList();

        // Before the first layout Bounds is unset; assume a strip-wide box so the clamp still does
        // something sensible rather than treating the window as a pixel wide.
        var w = Bounds.Width > 1 ? Bounds.Width : 300;
        var h = Bounds.Height > 1 ? Bounds.Height : 200;

        var (nx, ny) = HudLayout.ClampIntoBounds(x, y, w, h, bounds);
        if (nx != x || ny != y)
        {
            settings.HudX = nx;
            settings.HudY = ny;
            _app.Save();
        }
        return new PixelPoint(nx, ny);
    }

    /// <summary>Call on every refresh tick (FanApp.Ticked). Cheap when nothing changed.</summary>
    public void Refresh()
    {
        var tiles = _app.Settings.HudTiles;

        // Everything Rebuild() reads must be hashed here. Columns and scale were once left out, so the
        // Tray page saved a new value and the strip sat there unchanged — invisible bug, obvious symptom.
        var signature = $"{_app.Settings.HudColumns}x{_app.Settings.HudScale}|" +
                        string.Join("|", tiles.Select(t => $"{t.SourceId}~{t.ColorHex}~{t.Label}"));
        if (signature != _signature)
            Rebuild(tiles, signature);

        _cpuSnapshot = null;
        for (var i = 0; i < _views.Count && i < tiles.Count; i++)
        {
            var text = HudFormat.Value(tiles[i].SourceId, Resolve(tiles[i].SourceId));
            if (i < _shown.Count && _shown[i] == text) continue;   // unchanged at shown precision
            _views[i].Value.Text = text;
            SetShown(i, text);
        }
    }

    private void Rebuild(IReadOnlyList<HudTileSettings> tiles, string signature)
    {
        _signature = signature;
        _views.Clear();
        _shown.Clear();
        _grid.Children.Clear();

        // Removing every tile must not leave a blank rounded box floating on the desktop. The strip
        // stays (so right-click still reaches Hide/Configure) but says what it is.
        if (tiles.Count == 0)
        {
            _grid.Columns = 1;
            _grid.Children.Add(new Border
            {
                Width = HudLayout.TileMetrics(_app.Settings.HudScale).Width,
                Height = 40 * HudLayout.ClampScale(_app.Settings.HudScale),
                Margin = new Thickness(4),
                Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.Parse("#2C363D")),
                Child = new TextBlock
                {
                    Text = "no sensors", FontSize = 12, Foreground = new SolidColorBrush(Color.Parse("#8FA0AA")),
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
            return;
        }

        var columns = HudLayout.ClampColumns(_app.Settings.HudColumns);
        var (tileW, tileH, valueFont, labelFont) = HudLayout.TileMetrics(_app.Settings.HudScale);
        _grid.Columns = Math.Min(columns, tiles.Count);

        foreach (var tile in tiles)
        {
            var bg = TryParseBrush(tile.ColorHex, out var brush) ? brush : Accent();
            var fg = new SolidColorBrush(Color.Parse(HudTheme.TextColorFor(tile.ColorHex)));

            var value = new TextBlock
            {
                Text = "—",
                FontSize = valueFont,
                FontWeight = FontWeight.SemiBold,
                Foreground = fg,
                LineHeight = valueFont + 1,
            };
            var label = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(tile.Label) ? HudFormat.LabelFor(tile.SourceId) : tile.Label,
                FontSize = labelFont,
                Opacity = 0.85,
                Foreground = fg,
            };

            _grid.Children.Add(new Border
            {
                Width = tileW,
                Height = tileH,
                Margin = new Thickness(4),
                Padding = new Thickness(10, 6, 10, 6),
                CornerRadius = new CornerRadius(7),
                Background = bg,
                Child = new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 1,
                    Children = { label, value },
                },
            });

            _views.Add((tile, value));
            _shown.Add("—");
        }
    }

    /// <summary>
    /// Resolves a tile's current value. Normal sensor ids come from the same readings dictionary the
    /// control loop uses; socket/board power is resolved here because those are not in that dictionary
    /// (it carries what curves need, and curves do not drive off wattage).
    /// </summary>
    private double? Resolve(string sourceId)
    {
        // One HSMP read per refresh shared by every CPU tile — three tiles would otherwise triple the
        // sysfs traffic each second for the same sample.
        var cpu = _cpuSnapshot ??= _cpu.Read();
        if (sourceId.Equals("cpu:power:w", StringComparison.OrdinalIgnoreCase)) return cpu.PowerW;
        if (sourceId.Equals("cpu:pptcap:w", StringComparison.OrdinalIgnoreCase)) return cpu.PptCapW;
        if (sourceId.Equals("cpu:temp:c", StringComparison.OrdinalIgnoreCase)) return cpu.TempC;

        if (sourceId.StartsWith("nvml:", StringComparison.OrdinalIgnoreCase) &&
            sourceId.Contains(":power", StringComparison.OrdinalIgnoreCase))
        {
            var parts = sourceId.Split(':');
            if (parts.Length >= 2)
                foreach (var snap in _app.Nvml.SnapshotAll())
                    if (snap.Uuid == parts[1])
                        return snap.PowerDrawW;
            return null;
        }

        return _app.SensorValue(sourceId);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed && !_dragging)
        {
            _dragging = true;
            try { BeginMoveDrag(e); }
            finally { _dragging = false; }
        }
        else if (props.IsRightButtonPressed)
        {
            ContextFlyout?.ShowAt(this);
        }
    }

    private MenuFlyout BuildMenu()
    {
        var menu = new MenuFlyout();

        var configure = new MenuItem { Header = "Configure sensors…" };
        configure.Click += (_, _) => App.ShowMainWindowRequested?.Invoke();
        menu.Items.Add(configure);

        var columns = new MenuItem { Header = "Tiles per row" };
        for (var n = 1; n <= 4; n++)
        {
            var captured = n;
            var item = new MenuItem { Header = n == 1 ? "1 (column)" : n.ToString() };
            item.Click += (_, _) =>
            {
                _app.Settings.HudColumns = HudLayout.ClampColumns(captured);
                _app.Save();
                _signature = "";   // force layout rebuild
                Refresh();
                App.HudUiSync?.Invoke();   // keep the Tray page's combo honest
            };
            columns.Items.Add(item);
        }
        menu.Items.Add(columns);

        var topmost = new MenuItem
        {
            Header = "Always on top",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _app.Settings.HudTopMost,
        };
        topmost.Click += (_, _) =>
        {
            _app.Settings.HudTopMost = topmost.IsChecked == true;
            Topmost = _app.Settings.HudTopMost;
            _app.Save();
            App.HudUiSync?.Invoke();
        };
        menu.Items.Add(topmost);

        var size = new MenuItem { Header = "Size" };
        foreach (var (sizeLabel, scale) in new[]
        {
            ("XSmall", 0.56), ("Small", 0.75), ("Normal", 1.0), ("Large", 1.35), ("Huge", 1.8),
        })
        {
            var captured = scale;
            var item = new MenuItem { Header = sizeLabel };
            item.Click += (_, _) =>
            {
                _app.Settings.HudScale = HudLayout.ClampScale(captured);
                _app.Save();
                _signature = "";   // geometry changed — force a rebuild
                Refresh();
                App.HudUiSync?.Invoke();
            };
            size.Items.Add(item);
        }
        menu.Items.Add(size);

        var reposition = new MenuItem { Header = "Reset position (top-left)" };
        reposition.Click += (_, _) =>
        {
            _app.Settings.HudX = 24;
            _app.Settings.HudY = 60;
            _app.Save();
            MoveToSavedPosition();
            App.HudUiSync?.Invoke();
        };
        menu.Items.Add(reposition);

        var hide = new MenuItem { Header = "Hide overlay" };
        hide.Click += (_, _) =>
        {
            _app.Settings.HudEnabled = false;
            _app.Save();
            Hide();
            App.HudUiSync?.Invoke();   // the Tray page's checkbox must not stay ticked
        };
        menu.Items.Add(hide);

        return menu;
    }

    private void RestoreGeometry()
    {
        var s = _app.Settings;
        Position = s.HudX is int x && s.HudY is int y
            ? ClampToScreens(x, y, s)
            : new PixelPoint(24, 60);   // below the top panel, out of the way of the menu
    }

    private void OnPositionChanged(object? sender, PixelPointEventArgs e)
    {
        if (_applyingPosition) return;   // our own move

        var saved = _app.Settings.HudX is int sx && _app.Settings.HudY is int sy
            ? ClampToScreens(sx, sy, _app.Settings)
            : (PixelPoint?)null;

        // The WM re-places borderless windows shortly after mapping; correct it a few times, then stop
        // fighting and treat the WM's choice as the truth worth saving.
        if (saved is not null && Position != saved && _restoreAttempts < 4)
        {
            _restoreAttempts++;
            DispatcherTimer.RunOnce(MoveToSavedPosition, TimeSpan.FromMilliseconds(120));
            return;
        }

        QueueGeometrySave();
    }

    /// <summary>
    /// Geometry saves are debounced to one per drag: PositionChanged fires for every pixel of movement
    /// and Settings.Save() writes the whole config file.
    /// </summary>
    private void QueueGeometrySave()
    {
        if (_saveQueued) return;
        _saveQueued = true;
        DispatcherTimer.RunOnce(SaveGeometry, TimeSpan.FromMilliseconds(600));
    }

    private void SaveGeometry()
    {
        _saveQueued = false;
        var s = _app.Settings;
        s.HudX = Position.X;
        s.HudY = Position.Y;
        _app.Save();
    }

    private static bool TryParseBrush(string hex, out IBrush brush)
    {
        brush = Brushes.Transparent;
        if (!HudTheme.TryParse(hex, out var r, out var g, out var b)) return false;
        brush = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
        return true;
    }

    private static IBrush Accent() => new SolidColorBrush(Color.Parse(AccentHex.Default));

    private void SetShown(int index, string text)
    {
        while (_shown.Count <= index) _shown.Add("");
        _shown[index] = text;
    }
}
