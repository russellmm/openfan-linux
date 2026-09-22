using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
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

    private readonly FanApp _app;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, CardUi> _cards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set by the tray Exit item — X alone only hides to tray (spec §3.2).</summary>
    public bool Exiting { get; set; }

    public MainWindow(FanApp app)
    {
        InitializeComponent();
        _app = app;

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

        _homeSubtitle = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · hwmon + NVML";

        NavHome.Checked += (_, _) => ShowPage("home");
        NavGpus.Checked += (_, _) => ShowPage("gpus");
        NavTheme.Checked += (_, _) => ShowPage("theme");
        NavTray.Checked += (_, _) => ShowPage("tray");
        NavSettings.Checked += (_, _) => ShowPage("settings");
        NavAbout.Checked += (_, _) => ShowPage("about");
        ManageCurvesBtn.Click += (_, _) =>
            new CurveLibraryWindow(_app, RebuildCards).Show(this);

        PageSubtitle.Text = _homeSubtitle;
        ClockText.Text = DateTime.Now.ToString("h:mm:ss tt");

        _app.InventoryChanged += RebuildCards;
        _app.Ticked += UpdateValues;
        RebuildCards();
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
        return $"{count} GPU(s) · {(NvmlDriverVersion())}";
    }

    private string NvmlDriverVersion()
    {
        try { return $"driver {_app.Nvml.DriverVersion}"; }
        catch { return "driver ?"; }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!Exiting)
        {
            e.Cancel = true;
            Hide(); // curves keep applying from the tray process
            return;
        }
        _timer.Stop();
        base.OnClosing(e);
    }

    private sealed class CardUi
    {
        public required HardwareItem Item { get; init; }
        public required TextBlock BigValue { get; init; }
        public required TextBlock RpmLine { get; init; }
        public required ComboBox Mode { get; init; }
        public required Slider FlatSlider { get; init; }
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
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTip.SetTip(title, $"{item.Name}\n{item.Id}");
        var group = new TextBlock { Text = item.Group, FontSize = 11, Foreground = Secondary };

        var big = new TextBlock { Text = "auto", FontSize = 30, FontWeight = FontWeight.Bold };
        var rpmLine = new TextBlock { Text = "", FontSize = 12, Foreground = Secondary };
        var errorLine = new TextBlock
        {
            Text = "",
            FontSize = 11,
            Foreground = Warn,
            TextWrapping = TextWrapping.Wrap,
            IsVisible = false,
        };

        // Assignment model (matches Windows OpenFan): curves are independent named objects —
        // a graph binds its own sensor — and any number of fans may share one curve.
        var mode = new ComboBox
        {
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        PopulateCurveChoices(mode, item);

        var slider = new Slider
        {
            Minimum = item.MinPercent,
            Maximum = 100,
            Width = 290,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
        };

        var editCurveBtn = new Button
        {
            Content = "Edit curve…",
            IsVisible = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var existingCfg = _app.Settings.Controls.FirstOrDefault(c => c.Id == item.Id);
        var assigned = FindAssignedCurve(existingCfg);
        if (assigned?.Type == "flat")
        {
            slider.Value = Math.Clamp(assigned.Percent, item.MinPercent, 100);
            slider.IsVisible = true;
        }
        editCurveBtn.IsVisible = assigned?.Type == "graph";

        editCurveBtn.Click += (_, _) =>
        {
            var cur = FindAssignedCurve(FindOrCreateCfg(item));
            if (cur is not null)
                OpenGraphEditor(cur);
        };

        mode.SelectionChanged += (_, _) =>
        {
            var c = FindOrCreateCfg(item);
            var curveId = (mode.SelectedItem as ComboBoxItem)?.Tag as string;
            if (curveId is null)
            {
                c.Enabled = false; // Monitor: FanController restores this control next tick
                slider.IsVisible = false;
                editCurveBtn.IsVisible = false;
            }
            else
            {
                var curve = _app.Settings.Curves.FirstOrDefault(k => k.Id == curveId);
                c.CurveId = curveId;
                c.Enabled = true;
                slider.IsVisible = curve?.Type == "flat";
                if (curve?.Type == "flat")
                    slider.Value = Math.Clamp(curve.Percent, item.MinPercent, 100);
                editCurveBtn.IsVisible = curve?.Type == "graph";
            }
            _app.Save();
            UpdateValues();
        };

        slider.ValueChanged += (_, _) =>
        {
            var c = FindOrCreateCfg(item);
            var curve = FindAssignedCurve(c);
            if (curve is null || !string.Equals(curve.Type, "flat", StringComparison.OrdinalIgnoreCase))
                return;
            curve.Percent = Math.Round(slider.Value); // shared flat: moves every fan using it
            _app.Save();
        };

        var card = new Border
        {
            Width = 340,
            Margin = new Thickness(6),
            Padding = new Thickness(16),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    title,
                    group,
                    big,
                    rpmLine,
                    errorLine,
                    mode,
                    slider,
                    editCurveBtn,
                },
            },
        };

        _cards[item.Id] = new CardUi
        {
            Item = item, BigValue = big, RpmLine = rpmLine, Mode = mode, FlatSlider = slider, ErrorLine = errorLine,
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

            if (commanded is double pct)
            {
                card.BigValue.Text = $"{pct:0} %";
                card.BigValue.Foreground = Accent;
            }
            else
            {
                card.BigValue.Text = applying ? "—" : "auto";
                card.BigValue.Foreground = Secondary;
            }

            var rpm = _app.PairedRpm(card.Item);
            card.RpmLine.Text = rpm is null ? "" : $"{rpm:0} RPM";

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
            items.Add(new ComboBoxItem { Content = $"{curve.Name}  ({curve.Type})", Tag = curve.Id });
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
            UpdateValues();
        });
        editor.Show(this);
    }

}
