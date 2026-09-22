using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Threading;
using OpenFan.Core.Curves;
using OpenFan.Core.Config;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.App;

public sealed partial class MainWindow : Window
{
    private static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#8FA0AA"));
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#1E2429"));
    private static readonly IBrush CardBorder = new SolidColorBrush(Color.Parse("#2C363D"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#F0A03C"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#EF6B6B"));
    private static readonly IBrush ValueText = new SolidColorBrush(Color.Parse("#F2F2F2"));
    private static readonly IBrush AreaFill = new SolidColorBrush(Color.FromArgb(0x59, 0xF0, 0xA0, 0x3C));

    private readonly FanApp _app;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, CardUi> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action> _curveUpdaters = [];

    /// <summary>Set by the tray Exit item — X alone only hides to tray (spec §3.2).</summary>
    public bool Exiting { get; set; }

    public MainWindow(FanApp app)
    {
        InitializeComponent();
        _app = app;
        RestoreWindowGeometry(app.Settings);

        ApplyCurvesBox.IsChecked = app.Settings.ApplyCurves;
        ApplyCurvesBox.IsCheckedChanged += (_, _) =>
        {
            _app.Settings.ApplyCurves = ApplyCurvesBox.IsChecked == true;
            if (!_app.Settings.ApplyCurves)
                _app.RestoreAll(); // release owned fans to auto immediately, not on next tick
            _app.Save();
            UpdateStatus();
            UpdateValues();
        };

        RefreshBtn.Click += (_, _) => _app.RefreshInventory(force: true);
        ExitBtn.Click += (_, _) =>
        {
            Exiting = true; // lifetime.Exit restores all owned fans + saves (spec §3.2)
            Close();
        };

        NewFlatFab.Click += (_, _) => CreateCurve("flat");
        NewGraphFab.Click += (_, _) => CreateCurve("graph");
        NewMixFab.Click += (_, _) => CreateCurve("mix");

        _homeSubtitle = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · hwmon + NVML";

        NavHome.Checked += (_, _) => ShowPage("home");
        NavGpus.Checked += (_, _) => ShowPage("gpus");
        NavTheme.Checked += (_, _) => ShowPage("theme");
        NavTray.Checked += (_, _) => ShowPage("tray");
        NavSettings.Checked += (_, _) => ShowPage("settings");
        NavAbout.Checked += (_, _) => ShowPage("about");
        PageSubtitle.Text = _homeSubtitle;
        ClockText.Text = DateTime.Now.ToString("h:mm:ss tt");

        _app.InventoryChanged += () =>
        {
            RebuildCards();
            RebuildCurveCards();
        };
        _app.Ticked += UpdateValues;
        RebuildCards();
        RebuildCurveCards();
        UpdateStatus();
        _app.Tick(); // first paint now, not one timer-tick late

        _timer.Tick += (_, _) =>
        {
            _app.Tick();
            ClockText.Text = DateTime.Now.ToString("h:mm:ss tt");
        };
        _timer.Start();
    }

    private string _homeSubtitle = "";

    private void ShowPage(string page)
    {
        HomePanel.IsVisible = page == "home";
        GpusPanel.IsVisible = page == "gpus";
        StubPage.IsVisible = page is not ("home" or "gpus");

        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "gpus" => ("GPUs", GpuSubtitle()),
            "theme" => ("Theme", ""),
            "tray" => ("Tray", ""),
            "settings" => ("Settings", ""),
            "about" => ("About", ""),
            _ => ("Home", _homeSubtitle),
        };

        StubPage.Text = page switch
        {
            "theme" => "Theme options arrive with the Phase 5 polish pass.",
            "tray" => "Tray is active now: closing the window hides to tray (curves keep applying);\ntray Exit restores every owned fan. Per-icon options pending.",
            "settings" => "Settings — disabled hwmon chips, refresh interval, start-at-login — land in Phase 5.",
            "about" => "OpenFan Linux — motherboard + NVIDIA fan control for Ubuntu.\nMIT licensed · curve engine shared with Windows OpenFan.",
            _ => "",
        };

        if (page == "gpus")
        {
            GpusContent.Children.Clear();
            GpusContent.Children.Add(new TextBlock
            {
                Text = "GPU detail panels — power limits, clocks, PCIe state and per-process usage —\narrive with the helper protocol extension (next task). Fan control on GPU cards\nfrom the Home page already works through openfan-helper.",
                Foreground = Secondary,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 60, 0, 0),
            });
        }
    }

    private string GpuSubtitle()
    {
        if (!_app.Nvml.Available)
            return "NVML unavailable";
        var count = _app.Inventory
            .Where(i => i.Id.StartsWith("nvml:", StringComparison.OrdinalIgnoreCase))
            .Select(i => i.Group).Distinct().Count();
        return $"{count} GPU(s) · {NvmlDriverVersion()}";
    }

    private string NvmlDriverVersion()
    {
        try { return $"driver {_app.Nvml.DriverVersion}"; }
        catch { return "driver ?"; }
    }

    private DispatcherTimer? _geometryDebounce;

    private void RestoreWindowGeometry(AppSettings s)
    {
        if (s.WindowWidth is double w && w >= 400 && s.WindowHeight is double h && h >= 300)
        {
            Width = w;
            Height = h;
        }
        if (s.WindowX is int x && s.WindowY is int y
            && x > -4000 && y > -4000 && (x != 0 || y != 0))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(x, y);
        }

        // Debounced autosave so dragging/resizing doesn't hammer the config file.
        _geometryDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        _geometryDebounce.Tick += (_, _) =>
        {
            _geometryDebounce!.Stop();
            CaptureGeometry();
        };
        PositionChanged += (_, _) => RestartGeometryDebounce();
        Resized += (_, _) => RestartGeometryDebounce();
    }

    private void RestartGeometryDebounce()
    {
        if (_geometryDebounce is null)
            return;
        _geometryDebounce.Stop();
        _geometryDebounce.Start();
    }

    private void CaptureGeometry()
    {
        var s = _app.Settings;
        s.WindowWidth = Bounds.Width;
        s.WindowHeight = Bounds.Height;
        s.WindowX = Position.X;
        s.WindowY = Position.Y;
        _app.Save();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        CaptureGeometry(); // covers both hide-to-tray and real exit paths
        if (!Exiting)
        {
            e.Cancel = true;
            Hide(); // curves keep applying from the tray process
            return;
        }
        _timer.Stop();
        base.OnClosing(e);
    }

    // ---- fan cards (Controls section) ---------------------------------------

    private sealed class CardUi
    {
        public required HardwareItem Item { get; init; }
        public required TextBlock ValueLine { get; init; }
        public required CheckBox CurveCheck { get; init; }
        public required ComboBox Mode { get; init; }
        public required TextBlock ErrorLine { get; init; }
    }

    private void RebuildCards()
    {
        _cards.Clear();
        var items = new List<Control>();
        foreach (var item in _app.Inventory.Where(i => i.Kind == HardwareKind.Control))
            items.Add(BuildCard(item));
        Cards.ItemsSource = items;
        UpdateValues();
    }

    private Control BuildCard(HardwareItem item)
    {
        var title = new TextBlock
        {
            Text = item.Name,
            FontSize = 15,
            FontWeight = FontWeight.SemiBold,
            Foreground = ValueText,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(title, $"{item.Name}\n{item.Id}");
        var group = new TextBlock { Text = item.Group, FontSize = 11, Foreground = Secondary };

        var existingCfg = _app.Settings.Controls.FirstOrDefault(c => c.Id == item.Id);
        var suppress = false; // checkbox ↔ dropdown mutual updates must not re-enter

        var check = new CheckBox
        {
            Content = "Curve",
            FontSize = 13,
            IsChecked = existingCfg is { Enabled: true },
        };

        var mode = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        PopulateCurveChoices(mode, item);

        var valueLine = new TextBlock
        {
            Text = "auto",
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = ValueText,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var errorLine = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = Warn,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        check.IsCheckedChanged += (_, _) =>
        {
            if (suppress) return;
            var c = FindOrCreateCfg(item);
            if (check.IsChecked == true)
            {
                var tag = (mode.SelectedItem as ComboBoxItem)?.Tag as string;
                if (tag is null)
                {
                    suppress = true;
                    check.IsChecked = false; // nothing to enable until a curve is chosen
                    suppress = false;
                    return;
                }
                c.CurveId = tag;
                c.Enabled = true;
            }
            else
            {
                c.Enabled = false; // FanController restores this control next tick
            }
            _app.Save();
            UpdateValues();
        };

        mode.SelectionChanged += (_, _) =>
        {
            if (suppress) return;
            var c = FindOrCreateCfg(item);
            var curveId = (mode.SelectedItem as ComboBoxItem)?.Tag as string;
            suppress = true;
            if (curveId is null)
            {
                c.Enabled = false; // Monitor
                check.IsChecked = false;
            }
            else
            {
                c.CurveId = curveId;
                c.Enabled = true;
                check.IsChecked = true; // picking a curve arms the fan (reference behavior)
            }
            suppress = false;
            _app.Save();
            UpdateValues();
        };

        var card = new Border
        {
            Width = 300,
            Margin = new Thickness(6),
            Padding = new Thickness(14, 12, 14, 12),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new StackPanel
            {
                Spacing = 5,
                Children = { title, group, check, mode, valueLine, errorLine },
            },
        };

        _cards[item.Id] = new CardUi
        {
            Item = item, ValueLine = valueLine, CurveCheck = check, Mode = mode, ErrorLine = errorLine,
        };
        return card;
    }

    private void UpdateValues()
    {
        var applying = _app.Settings.ApplyCurves;
        foreach (var (id, card) in _cards)
        {
            var cfg = _app.Settings.Controls.FirstOrDefault(c => c.Id == id);
            var commanded = applying && cfg is { Enabled: true } ? _app.CommandedPercent(cfg) : null;
            var rpm = _app.PairedRpm(card.Item);

            if (commanded is double pct)
            {
                card.ValueLine.Text = rpm is null ? $"{pct:0.#} %" : $"{pct:0.#} %     {rpm:0} RPM";
                card.ValueLine.Foreground = ValueText;
            }
            else
            {
                card.ValueLine.Text = rpm is null
                    ? (applying ? "—" : "auto")
                    : $"auto     {rpm:0} RPM"; // monitoring still shows real speed
                card.ValueLine.Foreground = Secondary;
            }

            // Surface exactly why a control is not moving (spec §3.4: no silent monitor-only).
            if (applying && cfg is { Enabled: true } && _app.HasWriteError(id))
            {
                card.ErrorLine.Text = _app.WriteError(card.Item) ?? "write failed";
                card.ErrorLine.IsVisible = true;
            }
            else
            {
                card.ErrorLine.IsVisible = false;
            }
        }

        foreach (var update in _curveUpdaters)
        {
            try { update(); } catch { /* a stale card mid-rebuild — next tick is fine */ }
        }

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var notes = new List<string>();

        if (!_app.Nvml.Available)
            notes.Add("NVML unavailable — board sensors only");

        if (_app.Settings.ApplyCurves)
        {
            notes.Add(_app.Controller.Errors.Count > 0
                ? $"{_app.Controller.Errors.Count} control(s) failing to write — see cards"
                : "Applying every 1 s");

            var gpuEnabled = _app.Settings.Controls.Any(c =>
                c.Enabled && c.Id.StartsWith("nvml:", StringComparison.OrdinalIgnoreCase));
            if (gpuEnabled && _app.GpuHelperMissing)
                notes.Add("GPU fan writes need openfan-helper — not running, those fans stay on driver control");
        }
        else
        {
            notes.Add("Monitor only — check Apply curves to take over");
            var probe = _app.StartupWriteProbe();
            if (probe is not null)
                notes.Add(probe);
        }

        StatusNote.Text = string.Join("   ·   ", notes);
    }

    // ---- curve cards (Curves section, reference: OpenFan-mainpage.jpg) ------

    private void RebuildCurveCards()
    {
        _curveUpdaters.Clear();
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["flat"] = 0, ["graph"] = 1, ["mix"] = 2,
        };
        var items = _app.Settings.Curves
            .OrderBy(c => order.GetValueOrDefault(c.Type, 3))
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(BuildCurveCard)
            .ToList();
        CurveCards.ItemsSource = items;
        CurvesHeader.IsVisible = items.Count > 0;
    }

    private Control BuildCurveCard(CurveSettings curve)
    {
        var body = new StackPanel { Spacing = 5 };

        // Header: type tag + editable name + delete.
        var nameBox = new TextBox
        {
            Text = curve.Name,
            Classes = { "cardname" },
        };
        nameBox.TextChanged += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(nameBox.Text))
                curve.Name = nameBox.Text.Trim();
        };
        nameBox.LostFocus += (_, _) =>
        {
            _app.Save();
            RebuildCards(); // fan dropdowns show the new name
        };

        var noteLine = new TextBlock
        {
            FontSize = 11,
            Foreground = Warn,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        var header = new DockPanel { LastChildFill = true };
        var delBtn = new Button
        {
            Content = "×",
            Padding = new Thickness(7, 0, 7, 1),
            Background = null,
            BorderThickness = new Thickness(0),
            Foreground = Secondary,
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(delBtn, Dock.Right);
        header.Children.Add(delBtn);
        header.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = curve.Type.ToUpperInvariant(),
                    FontSize = 9,
                    Foreground = Secondary,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                nameBox,
            },
        });

        delBtn.Click += (_, _) => DeleteCurve(curve, delBtn, noteLine);

        body.Children.Add(header); // name + delete live above the type-specific controls

        switch (curve.Type.ToLowerInvariant())
        {
            case "flat": BuildFlatBody(curve, body); break;
            case "mix": BuildMixBody(curve, body); break;
            default: BuildGraphBody(curve, body); break;
        }

        body.Children.Add(noteLine);

        return new Border
        {
            Width = curve.Type.Equals("mix", StringComparison.OrdinalIgnoreCase) ? 320 : 300,
            Margin = new Thickness(6),
            Padding = new Thickness(14, 12, 14, 12),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = body,
        };
    }

    private void BuildFlatBody(CurveSettings curve, StackPanel body)
    {
        body.Children.Add(new TextBlock { Text = "Fan speed", FontSize = 11, Foreground = Secondary });

        // Percent reads as plain text; click to type any value (reference: quiet affordance).
        var percentBox = new TextBox
        {
            Text = $"{curve.Percent:0}",
            FontSize = 20,
            FontWeight = FontWeight.Bold,
            Foreground = ValueText,
            BorderThickness = new Thickness(0),
            Background = null,
            Padding = new Thickness(0),
            MinWidth = 52,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Commit(bool normalize)
        {
            if (double.TryParse(percentBox.Text, out var v))
            {
                curve.Percent = Math.Clamp(v, 0, 100);
                _app.Save();
                UpdateValues();
            }
            if (normalize)
                percentBox.Text = $"{curve.Percent:0}"; // repair partial/garbage input on exit
        }

        percentBox.TextChanged += (_, _) => Commit(normalize: false);
        percentBox.LostFocus += (_, _) => Commit(normalize: true);
        percentBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                TopLevel.GetTopLevel(percentBox)?.FocusManager?.ClearFocus(); // blur → LostFocus commits + normalizes
        };

        void Adjust(double delta)
        {
            curve.Percent = Math.Clamp(curve.Percent + delta, 0, 100);
            percentBox.Text = $"{curve.Percent:0}";
            _app.Save();
            UpdateValues();
        }

        var minus = new Button { Content = "−", Width = 40 };
        var plus = new Button { Content = "+", Width = 40 };
        minus.Click += (_, _) => Adjust(-5);
        plus.Click += (_, _) => Adjust(5);

        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { minus, percentBox, new TextBlock { Text = "%", FontSize = 20, FontWeight = FontWeight.Bold, Foreground = ValueText, VerticalAlignment = VerticalAlignment.Center }, plus },
        });
    }

    private void BuildGraphBody(CurveSettings curve, StackPanel body)
    {
        body.Children.Add(new TextBlock { Text = "Temperature source", FontSize = 11, Foreground = Secondary });

        var sensorBox = new ComboBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        var sensorItems = new List<(ComboBoxItem Item, string Id, string BaseLabel)>();
        foreach (var t in _app.Inventory
                     .Where(i => i.Kind == HardwareKind.Temperature)
                     .OrderBy(i => i.Group).ThenBy(i => i.Name))
        {
            var item = new ComboBoxItem { Tag = t.Id };
            sensorBox.Items.Add(item);
            sensorItems.Add((item, t.Id, $"{t.Name}  ·  {t.Group}"));
        }
        var suppress = false;
        int WantedIndex() => sensorItems.FindIndex(s => s.Id == curve.SensorId);
        sensorBox.SelectedIndex = WantedIndex();

        sensorBox.SelectionChanged += (_, _) =>
        {
            if (suppress) return;
            var tag = (sensorBox.SelectedItem as ComboBoxItem)?.Tag as string;
            if (tag is null && curve.SensorId is not null)
            {
                // A spurious selection clear (rebuild/layout) must never unbind the sensor.
                suppress = true;
                sensorBox.SelectedIndex = WantedIndex();
                suppress = false;
                return;
            }
            curve.SensorId = tag;
            _app.Save();
        };

        var outputText = new TextBlock
        {
            Text = "—",
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            Foreground = ValueText,
        };

        var editBtn = new Button
        {
            Content = "Edit",
            Background = null,
            BorderThickness = new Thickness(0),
            Foreground = Accent,
            FontWeight = FontWeight.SemiBold,
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        editBtn.Click += (_, _) => OpenGraphEditor(curve);

        var outRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(editBtn, Dock.Right);
        outRow.Children.Add(editBtn);
        outRow.Children.Add(outputText);

        var preview = new Canvas { Height = 60, ClipToBounds = true };

        body.Children.Add(sensorBox);
        body.Children.Add(outRow);
        body.Children.Add(preview);

        _curveUpdaters.Add(() =>
        {
            // Re-assert the bound sensor if anything ever cleared the combo (belt & braces).
            var want = WantedIndex();
            if (want >= 0 && sensorBox.SelectedIndex != want)
            {
                suppress = true;
                sensorBox.SelectedIndex = want;
                suppress = false;
            }

            foreach (var (item, id, baseLabel) in sensorItems)
            {
                var r = _app.Reading(id);
                item.Content = r is null ? baseLabel : $"{baseLabel}   —   {r:0.#} °C";
            }

            // keep the combo's own label live too (shows selected sensor + reading when closed)
            if (sensorBox.SelectedItem is ComboBoxItem sel && sel.Tag is string selId)
            {
                var r = _app.Reading(selId);
                if (r is not null)
                    sel.Content = $"{(selItemsBase(sensorItems, selId) ?? "sensor")}   —   {r:0.#} °C";
            }

            var temp = curve.SensorId is null ? null : _app.Reading(curve.SensorId);
            outputText.Text = _app.CurveOutput(curve.Id) is double o ? $"{o:0.#} %" : "—";
            DrawMiniPreview(preview, curve, temp);
        });
    }

    private static string? selItemsBase(List<(ComboBoxItem Item, string Id, string BaseLabel)> items, string id)
        => items.FirstOrDefault(s => s.Id == id).BaseLabel;

    private void BuildMixBody(CurveSettings curve, StackPanel body)
    {
        body.Children.Add(new TextBlock { Text = "Function", FontSize = 11, Foreground = Secondary });

        var funcBox = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var fi in new[]
                 {
                     new ComboBoxItem { Content = "Max", Tag = "max" },
                     new ComboBoxItem { Content = "Min", Tag = "min" },
                     new ComboBoxItem { Content = "Average", Tag = "average" },
                 })
        {
            funcBox.Items.Add(fi);
        }
        funcBox.SelectedItem = funcBox.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => string.Equals((string?)i.Tag, curve.Function, StringComparison.OrdinalIgnoreCase))
            ?? funcBox.Items[0];
        funcBox.SelectionChanged += (_, _) =>
        {
            curve.Function = (funcBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "max";
            _app.Save();
            UpdateValues();
        };

        var childList = new StackPanel { Spacing = 3 };

        void RebuildChildren()
        {
            childList.Children.Clear();
            foreach (var childId in curve.ChildCurveIds.ToList())
            {
                var childName = _app.Settings.Curves
                    .FirstOrDefault(c => c.Id == childId)?.Name ?? childId;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                row.Children.Add(new TextBlock
                {
                    Text = "●",
                    FontSize = 8,
                    Foreground = Accent,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                row.Children.Add(new TextBlock
                {
                    Text = childName,
                    FontSize = 13,
                    Foreground = ValueText,
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var removeBtn = new Button
                {
                    Content = "×",
                    Background = null,
                    BorderThickness = new Thickness(0),
                    Foreground = Secondary,
                    Padding = new Thickness(5, 0, 5, 0),
                };
                removeBtn.Click += (_, _) =>
                {
                    curve.ChildCurveIds.Remove(childId);
                    _app.Save();
                    RebuildChildren();
                    UpdateValues();
                };
                row.Children.Add(removeBtn);
                childList.Children.Add(row);
            }
        }

        var addBox = new ComboBox
        {
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
            PlaceholderText = "Add fan curve",
        };

        void RebuildAddChoices()
        {
            addBox.ItemsSource = _app.Settings.Curves
                .Where(c => c.Id != curve.Id
                            && !c.Type.Equals("mix", StringComparison.OrdinalIgnoreCase) // no mix-in-mix cycles
                            && !curve.ChildCurveIds.Contains(c.Id))
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => (object)new ComboBoxItem { Content = c.Name, Tag = c.Id })
                .ToList();
        }

        var suppressAdd = false;
        addBox.SelectionChanged += (_, _) =>
        {
            if (suppressAdd) return;
            if ((addBox.SelectedItem as ComboBoxItem)?.Tag is string childId)
            {
                curve.ChildCurveIds.Add(childId);
                _app.Save();
                RebuildChildren();
                RebuildAddChoices();
                suppressAdd = true;
                addBox.SelectedItem = null;
                suppressAdd = false;
                UpdateValues();
            }
        };

        var outputText = new TextBlock
        {
            Text = "—",
            FontSize = 24,
            FontWeight = FontWeight.Bold,
            Foreground = ValueText,
            Margin = new Thickness(0, 4, 0, 0),
        };

        body.Children.Add(funcBox);
        body.Children.Add(addBox);
        body.Children.Add(childList);
        body.Children.Add(outputText);

        RebuildChildren();
        RebuildAddChoices();

        _curveUpdaters.Add(() =>
            outputText.Text = _app.CurveOutput(curve.Id) is double o ? $"{o:0.#} %" : "—");
    }

    /// <summary>Mini curve preview like the Windows cards: white line, orange fill, live dot.</summary>
    private static void DrawMiniPreview(Canvas canvas, CurveSettings curve, double? currentTemp)
    {
        canvas.Children.Clear();
        var w = Math.Max(canvas.Bounds.Width, 40);
        var h = Math.Max(canvas.Height, 20);
        const double pad = 3;
        var tMin = curve.MinTempC;
        var tMax = Math.Max(curve.MinTempC + 10, curve.MaxTempC);

        double X(double t) => pad + (t - tMin) / (tMax - tMin) * (w - 2 * pad);
        double Y(double p) => h - pad - p / 100.0 * (h - 2 * pad);

        var ordered = curve.Points.OrderBy(p => p.TempC).ToList();
        if (ordered.Count < 2)
            return;

        var areaPoints = new Avalonia.Collections.AvaloniaList<Avalonia.Point>(
            ordered.Select(p => new Avalonia.Point(X(p.TempC), Y(p.Percent))));
        areaPoints.Add(new Avalonia.Point(X(ordered[^1].TempC), Y(0)));
        areaPoints.Add(new Avalonia.Point(X(ordered[0].TempC), Y(0)));
        canvas.Children.Add(new Avalonia.Controls.Shapes.Polygon { Fill = AreaFill, Points = areaPoints });

        canvas.Children.Add(new Avalonia.Controls.Shapes.Polyline
        {
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Points = new Avalonia.Collections.AvaloniaList<Avalonia.Point>(
                ordered.Select(p => new Avalonia.Point(X(p.TempC), Y(p.Percent)))),
        });

        if (currentTemp is double t && t >= tMin && t <= tMax)
        {
            var pts = ordered.Select(q => new CurvePoint(q.TempC, q.Percent)).ToList();
            var pct = GraphCurve.Evaluate(pts, t, maxSpeed: Math.Clamp(curve.MaxSpeedPercent, 0, 100));
            var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, Fill = Accent };
            canvas.Children.Add(dot);
            Canvas.SetLeft(dot, X(t) - 3.5);
            Canvas.SetTop(dot, Y(pct) - 3.5);
        }
    }

    // ---- curve lifecycle ----------------------------------------------------

    private int CurveUsage(CurveSettings curve) =>
        _app.Settings.Controls.Count(c =>
            c.Enabled && string.Equals(c.CurveId, curve.Id, StringComparison.OrdinalIgnoreCase));

    private void CreateCurve(string type)
    {
        var curve = new CurveSettings
        {
            Id = $"curve-{Guid.NewGuid():N}",
            Type = type,
            Name = type switch { "graph" => "New graph", "mix" => "New mix", _ => "New flat" },
        };
        if (type == "graph")
            curve.Points = [new CurvePointDto(40, 20), new CurvePointDto(85, 90)];
        else if (type == "flat")
            curve.Percent = 50;
        else
            curve.Function = "max";

        _app.Settings.Curves.Add(curve);
        _app.Save();
        RebuildCurveCards();
        RebuildCards(); // new curve appears in fan dropdowns

        if (type == "graph")
            OpenGraphEditor(curve); // pick sensor + shape right away
    }

    private void DeleteCurve(CurveSettings curve, Button anchor, TextBlock noteLine)
    {
        var used = CurveUsage(curve);
        if (used == 0)
        {
            DoDeleteCurve(curve);
            return;
        }

        // In use: offer one-click unassign + delete instead of making the user hunt for fans.
        var fly = new MenuFlyout();
        fly.Items.Add(new MenuItem { Header = $"In use by {used} fan(s)", IsEnabled = false });
        var force = new MenuItem { Header = "Unassign all & delete" };
        force.Click += (_, _) => DoDeleteCurve(curve, unassign: true);
        fly.Items.Add(force);
        noteLine.IsVisible = false;
        FlyoutBase.SetAttachedFlyout(anchor, fly);
        FlyoutBase.ShowAttachedFlyout(anchor);
    }

    private void DoDeleteCurve(CurveSettings curve, bool unassign = false)
    {
        if (unassign)
        {
            foreach (var c in _app.Settings.Controls.Where(c =>
                         string.Equals(c.CurveId, curve.Id, StringComparison.OrdinalIgnoreCase)))
            {
                c.Enabled = false; // FanController restores these next tick
            }
        }
        _app.Settings.Curves.Remove(curve);
        _app.Save();
        RebuildCurveCards();
        RebuildCards();
    }

    // ---- Save / Load setup (⋮ menu, reference: Windows title-bar menu) ------

    private static string ProfilesDir =>
        Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath) ?? ".", "profiles");

    private async void OnSaveSetupAs(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ProfilesDir);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save setup",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync($"file://{ProfilesDir}/"),
                SuggestedFileName = "openfan.json",
                FileTypeChoices = [new FilePickerFileType("OpenFan setup") { Patterns = ["*.json"] }],
            });
            if (file?.Path.LocalPath is string path)
            {
                new SettingsStore(path).Save(_app.Settings);
                StatusNote.Text = $"Setup saved to {path}";
            }
        }
        catch (Exception ex)
        {
            // No portal/picker on this desktop — fall back to a timestamped profile file.
            var path = Path.Combine(ProfilesDir, $"openfan-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            Directory.CreateDirectory(ProfilesDir);
            new SettingsStore(path).Save(_app.Settings);
            StatusNote.Text = $"File dialog unavailable ({ex.GetType().Name}) — setup saved to {path}";
        }
    }

    private async void OnLoadSetup(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ProfilesDir);
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Load setup",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(new Uri($"file://{ProfilesDir}/")),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("OpenFan setup") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0 || files[0].Path.LocalPath is not string path)
                return;

            _app.LoadProfile(new SettingsStore(path).Load());
            ApplyCurvesBox.IsChecked = _app.Settings.ApplyCurves;
            RebuildCards();
            RebuildCurveCards();
            UpdateStatus();
            StatusNote.Text = $"Setup loaded from {path}";
        }
        catch (Exception ex)
        {
            StatusNote.Text = $"Load failed: {ex.Message}";
        }
    }

    // ---- shared helpers -----------------------------------------------------

    private ControlSettings FindOrCreateCfg(HardwareItem item)
    {
        var cfg = _app.Settings.Controls.FirstOrDefault(c => c.Id == item.Id);
        if (cfg is null)
        {
            cfg = new ControlSettings { Id = item.Id, Name = item.Name };
            _app.Settings.Controls.Add(cfg);
        }
        return cfg;
    }

    private void PopulateCurveChoices(ComboBox mode, HardwareItem item)
    {
        var cfg = _app.Settings.Controls.FirstOrDefault(c => c.Id == item.Id);
        var items = new List<ComboBoxItem> { new() { Content = "Monitor", Tag = null } };
        foreach (var curve in _app.Settings.Curves.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            items.Add(new ComboBoxItem { Content = curve.Name, Tag = curve.Id });
        mode.ItemsSource = items;
        mode.SelectedItem = cfg is { Enabled: true }
            ? items.FirstOrDefault(i => (string?)i.Tag == cfg.CurveId) ?? items[0]
            : items[0];
    }

    private CurveSettings? FindAssignedCurve(ControlSettings? cfg) =>
        cfg is { Enabled: true } && !string.IsNullOrEmpty(cfg.CurveId)
            ? _app.Settings.Curves.FirstOrDefault(c => c.Id == cfg.CurveId)
            : null;

    /// <summary>Editor edits a working copy; Ok commits via this callback (reference dialog semantics).</summary>
    private void OpenGraphEditor(CurveSettings curve)
    {
        var editor = new GraphEditorWindow(_app, curve, edited =>
        {
            var curves = _app.Settings.Curves;
            curves.RemoveAll(c => c.Id == edited.Id);
            curves.Add(edited);
            _app.Save();
            RebuildCurveCards();
            RebuildCards();
            UpdateValues();
        });
        editor.Show(this);
    }
}
