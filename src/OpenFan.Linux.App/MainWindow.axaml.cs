using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using System.Diagnostics;
using Avalonia.Threading;
using OpenFan.Core.Curves;
using OpenFan.Core.Config;
using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;

namespace OpenFan.Linux.App;

public sealed partial class MainWindow : Window
{
    private static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#8FA0AA"));
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#1E2429"));
    private static readonly IBrush CardBorder = new SolidColorBrush(Color.Parse("#2C363D"));
    private static IBrush Accent => AccentTheme.Accent; // shared mutable brush — live recolor via Theme page
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#EF6B6B"));
    private static readonly IBrush ValueText = new SolidColorBrush(Color.Parse("#F2F2F2"));
    private static readonly IBrush AreaFill = new SolidColorBrush(Color.FromArgb(0x59, 0xF0, 0xA0, 0x3C));
    // Windows Openfan look: card combos sit flat with a full-width underline rule beneath them.
    private static readonly IBrush ComboRule = new SolidColorBrush(Color.Parse("#4A5862"));

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
            App.TrayApplySync?.Invoke();
            if (!_app.Settings.ApplyCurves)
                _app.RestoreAll(); // release owned fans to auto immediately, not on next tick
            _app.Save();
            UpdateStatus();
            UpdateValues();
        };

        ToolTip.SetTip(NavHome, "Ctrl+1");
        ToolTip.SetTip(NavCpu, "Ctrl+2");
        ToolTip.SetTip(NavGpus, "Ctrl+3");
        ToolTip.SetTip(NavSensors, "Ctrl+4");
        ToolTip.SetTip(NavTheme, "Ctrl+5");
        ToolTip.SetTip(NavTray, "Ctrl+6");
        ToolTip.SetTip(NavSettings, "Ctrl+7");

        RefreshBtn.Click += (_, _) => _app.RefreshInventory(force: true);
        // Windows-style blur: clicking anywhere that is NOT an editable control clears focus from
        // text boxes / combos / spinners. Before this, focus could only move field-to-field or via
        // menus, leaving name fields stuck in edit mode and mix-card dropdowns open across cards.
        AddHandler(PointerPressedEvent, OnGlobalPointerPressedForBlur, RoutingStrategies.Tunnel);

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
        NavCpu.Checked += (_, _) => ShowPage("cpu");
        NavGpus.Checked += (_, _) => ShowPage("gpus");
        NavSensors.Checked += (_, _) => ShowPage("sensors");
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
            InvalidateGpuPage();
            _sensorsPageBuilt = false;
        };
        _app.Ticked += UpdateValues;
        RebuildCards();
        RebuildCurveCards();
        UpdateStatus();
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(_app.Settings.RefreshMs, 250, 5000));

        var bootWait = Math.Clamp(_app.Settings.StartupDelaySeconds, 0, 60);
        if (bootWait == 0)
            _app.Tick(); // first paint now, not one timer-tick late

        _timer.Tick += (_, _) =>
        {
            _app.Tick();
            ClockText.Text = DateTime.Now.ToString("h:mm:ss tt");
        };
        UpdateTitle(); // "OpenFan — myconfig.json" when a named config is active
        if (bootWait == 0)
            _timer.Start();
        else
            _ = DelayedStartAsync(bootWait); // let modules-load.d chips appear before first scan/loop
    }

    private string _homeSubtitle = "";

    private void ShowPage(string page)
    {
        HomePanel.IsVisible = page == "home";
        CpusPanel.IsVisible = page == "cpu";
        GpusPanel.IsVisible = page == "gpus";
        SensorsPanel.IsVisible = page == "sensors";
        SettingsPanel.IsVisible = page == "settings";
        if (page == "settings")
            BuildSettingsPage(); // fresh state each visit (helper status, conflicts, hidden list)
        ThemePanel.IsVisible = page == "theme";
        AboutPanel.IsVisible = page == "about";
        if (page == "theme")
            BuildThemePage();
        if (page == "about")
            BuildAboutPage();
        StubPage.IsVisible = page is not ("home" or "cpu" or "gpus" or "sensors" or "settings" or "theme" or "about");

        (PageTitle.Text, PageSubtitle.Text) = page switch
        {
            "cpu" => ("CPU", CpuSubtitle()),
            "gpus" => ("GPUs", GpuSubtitle()),
            "sensors" => ("Sensors", SensorsSubtitle()),
            "theme" => ("Theme", "accent color, applied live"),
            "tray" => ("Tray", ""),
            "settings" => ("Settings", "app preferences"),
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

        if (page == "cpu")
            EnsureCpuPage();
        if (page == "gpus")
            EnsureGpuPage();
        if (page == "sensors")
            EnsureSensorsPage();
    }

    // ---- CPU page ------------------------------------------------------------

    private readonly OpenFan.Linux.Hw.CpuMonitor _cpu = new();
    private bool _cpuPageBuilt;
    private Action? _cpuCardUpdate;

    private string CpuSubtitle()
    {
        var s = _cpu.Read();
        return $"{s.Cores} threads" + (s.PowerW is null ? " · no HSMP power source" : " · amd_hsmp");
    }

    private void EnsureCpuPage()
    {
        if (_cpuPageBuilt)
            return;
        _cpuPageBuilt = true;
        CpusContent.Children.Clear();
        var snap = _cpu.Read();

        StackPanel Stat(string label, out TextBlock valueOut)
        {
            valueOut = new TextBlock { FontSize = 15, FontWeight = FontWeight.Bold, Foreground = ValueText };
            return new StackPanel
            {
                Spacing = 2,
                MinWidth = 130,
                Children =
                {
                    new TextBlock { Text = label, Foreground = Secondary, FontSize = 12 },
                    valueOut,
                },
            };
        }

        var tempV = Stat("Temp", out var temp);
        var powerV = Stat("Socket power", out var power);
        var capV = Stat("Socket cap", out var cap);
        var loadV = Stat("Load", out var load);
        var freqV = Stat("Frequency", out var freq);
        var ramV = Stat("RAM used", out var ramPct);

        var barBg = new SolidColorBrush(Color.Parse("#2C363D"));
        var loadBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, CornerRadius = new CornerRadius(4), Foreground = Accent, Background = barBg };
        var loadPct = new TextBlock { Foreground = ValueText, FontSize = 13 };
        var powerBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, CornerRadius = new CornerRadius(4), Foreground = new SolidColorBrush(Color.Parse("#4FC3F7")), Background = barBg };
        var powerText = new TextBlock { Foreground = ValueText, FontSize = 13 };
        var ramBar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 8, CornerRadius = new CornerRadius(4), Foreground = new SolidColorBrush(Color.Parse("#B388FF")), Background = barBg };
        var ramText = new TextBlock { Foreground = ValueText, FontSize = 13 };

        var noteLine = new TextBlock { Foreground = Secondary, FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };

        _cpuCardUpdate = () =>
        {
            var s = _cpu.Read();
            temp.Text = s.TempC is double t ? $"{t:0.#} °C" : "—";
            power.Text = s.PowerW is double pw ? $"{pw:0.#} W" : "—";
            cap.Text = s.PptCapW is double c ? $"{c:0} W" : "—";
            load.Text = s.LoadPct is double l ? $"{l:0} %" : "—";
            freq.Text = s.MaxGHz is double g ? $"{g:0.##} GHz" : "—";
            ramPct.Text = s.RamUsedGiB is double ru2 && s.RamTotalGiB is double rt2 && rt2 > 0
                ? $"{ru2 / rt2 * 100:0} %"
                : "—";
            loadBar.Value = s.LoadPct ?? 0;
            loadPct.Text = $"Load   {s.LoadPct:0.#} %";
            if (s.PowerW is double pw2 && s.PptCapW is double c2 && c2 > 0)
            {
                powerBar.IsVisible = true;
                powerText.IsVisible = true;
                powerBar.Value = Math.Clamp(pw2 / c2 * 100, 0, 100);
                powerText.Text = $"Socket draw   {pw2:0.#} / {c2:0} W (HSMP-reported cap)";
            }
            else
            {
                powerBar.IsVisible = false;
                powerText.IsVisible = false;
            }
            if (s.RamUsedGiB is double ru && s.RamTotalGiB is double rt && rt > 0)
            {
                ramBar.IsVisible = true;
                ramText.IsVisible = true;
                ramBar.Value = Math.Clamp(ru / rt * 100, 0, 100);
                ramText.Text = $"RAM   {ru:0.##} / {rt:0.#} GiB";
            }
            else
            {
                ramBar.IsVisible = false;
                ramText.IsVisible = false;
            }
            noteLine.Text = s.PowerW is null
                ? "Package power needs the amd_hsmp kernel module (sensors-detect / modules-load.d). Temp, load and frequency work without it."
                : $"Socket power + cap read from amd_hsmp_hwmon (HSMP-reported, not decoded BIOS PBO/PPT registers) · temp from board hwmon · load from /proc/stat · RAM from /proc/meminfo · {s.Cores} threads";
        };
        _cpuCardUpdate();

        CpusContent.Children.Add(new Border
        {
            Padding = new Thickness(18, 14, 18, 16),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = snap.Model, FontSize = 18, FontWeight = FontWeight.Bold, Foreground = ValueText },
                    new WrapPanel { Orientation = Orientation.Horizontal, Children = { tempV, powerV, capV, loadV, freqV, ramV } },
                    new StackPanel { Spacing = 3, Margin = new Thickness(0, 6, 0, 0), Children = { loadBar, loadPct } },
                    new StackPanel { Spacing = 3, Children = { powerBar, powerText } },
                    new StackPanel { Spacing = 3, Children = { ramBar, ramText } },
                    noteLine,
                },
            },
        });
    }

    private void UpdateCpuPage()
    {
        if (!_cpuPageBuilt || !CpusPanel.IsVisible)
            return;
        try { _cpuCardUpdate?.Invoke(); } catch { /* transient sysfs read — next tick */ }
    }

    // ---- GPUs page -----------------------------------------------------------

    private bool _gpuPageBuilt;
    private readonly List<(string Uuid, Action<NvmlBackend.GpuSnapshot> Update)> _gpuCardUpdaters = [];

    private void EnsureGpuPage()
    {
        if (_gpuPageBuilt)
            return;
        _gpuPageBuilt = true;
        GpusContent.Children.Clear();
        _gpuCardUpdaters.Clear();
        foreach (var snap in _app.Nvml.SnapshotAll())
            GpusContent.Children.Add(BuildGpuCard(snap));
    }

    public void InvalidateGpuPage() => _gpuPageBuilt = false;

    private static string Gib(double bytes) => $"{bytes / 1073741824.0:0.##}";

    private static string LinkRate(double? kbs) =>
        kbs is null ? "—" : kbs >= 1024 ? $"{kbs / 1024:0.##} MiB/s" : $"{kbs:0} KiB/s";

    private static T Col<T>(T el, int column) where T : Control
    {
        Grid.SetColumn(el, column);
        return el;
    }

    private Control BuildGpuCard(NvmlBackend.GpuSnapshot snap)
    {
        StackPanel Stat(string label, out TextBlock valueOut)
        {
            valueOut = new TextBlock { FontSize = 15, FontWeight = FontWeight.Bold, Foreground = ValueText };
            return new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = label, Foreground = Secondary, FontSize = 12 },
                    valueOut,
                },
            };
        }

        var tempV = Stat("Temp", out var temp);
        var powerV = Stat("Power", out var power);
        var pstateV = Stat("P-State", out var pstate);
        var fanPctV = Stat("Fan %", out var fanPct);
        var fanRpmV = Stat("Fan RPM", out var fanRpm);
        var clocksV = Stat("Clocks", out var clocks);

        var gpuBar = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Height = 8, CornerRadius = new CornerRadius(4),
            Foreground = Accent, Background = new SolidColorBrush(Color.Parse("#2C363D")),
        };
        var gpuPct = new TextBlock { Foreground = ValueText, FontSize = 13 };
        var vramBar = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Height = 8, CornerRadius = new CornerRadius(4),
            Foreground = new SolidColorBrush(Color.Parse("#4FC3F7")), Background = new SolidColorBrush(Color.Parse("#2C363D")),
        };
        var vramText = new TextBlock { Foreground = ValueText, FontSize = 13 };

        var pcieLine = new TextBlock { Foreground = Secondary, FontSize = 13, Margin = new Thickness(0, 8, 0, 0) };

        // Power limit editor (writes route through the helper; the driver persists the value itself).
        var savedLimit = _app.Settings.GpuPowerLimitsW.GetValueOrDefault(snap.Uuid);
        var powerBox = new NumericUpDown
        {
            Width = 120,
            Minimum = (decimal)Math.Max(50, snap.PowerMinW ?? 50),
            Maximum = (decimal)(snap.PowerMaxW ?? 800),
            Value = (decimal)(savedLimit > 0 ? savedLimit : snap.PowerLimitW ?? 0),
            FormatString = "0",
            Increment = 5,
        };
        var applyBtn = new Button { Content = "Apply", Height = 32 };
        var powerNote = new TextBlock { Foreground = Secondary, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        applyBtn.Click += (_, _) =>
        {
            var watts = (int)Math.Round(powerBox.Value ?? 0);
            if (_app.SetGpuPowerLimit(snap.Uuid, watts))
            {
                powerNote.Text = $"applied {watts} W";
                powerNote.Foreground = new SolidColorBrush(Color.Parse("#7BC97B"));
            }
            else
            {
                powerNote.Text = _app.GpuPowerError ?? "failed";
                powerNote.Foreground = new SolidColorBrush(Color.Parse("#EF6B6B"));
            }
        };

        var procList = new StackPanel { Spacing = 2 };
        string lastProcKey = "";

        Action<NvmlBackend.GpuSnapshot> updater = sn =>
        {
            temp.Text = sn.TempC is null ? "—" : $"{sn.TempC:0}°";
            power.Text = sn.PowerDrawW is null || sn.PowerLimitW is null
                ? "—"
                : $"{sn.PowerDrawW:0.#}/{sn.PowerLimitW:0}W";
            pstate.Text = sn.PState is null ? "—" : $"P{sn.PState}";
            fanPct.Text = sn.FanPct is null ? "—" : $"{sn.FanPct:0}%";
            fanRpm.Text = sn.FanRpm is null ? "—" : $"{sn.FanRpm:0}";
            clocks.Text = sn.ClockG is null ? "—" : $"G{sn.ClockG} S{sn.ClockS} M{sn.ClockM}";

            gpuBar.Value = sn.UtilGpuPct ?? 0;
            gpuPct.Text = sn.UtilGpuPct is null ? "—" : $"{sn.UtilGpuPct:0}%";
            if (sn.MemUsed is not null && sn.MemTotal is not null && sn.MemTotal > 0)
            {
                var frac = sn.MemUsed.Value / (double)sn.MemTotal.Value;
                vramBar.Value = frac * 100;
                vramText.Text = $"{Gib(sn.MemUsed.Value)} / {Gib(sn.MemTotal.Value)} GiB ({frac * 100:0.#}%)";
            }

            pcieLine.Text = $"PCIe Gen{sn.PcieGen ?? 0} x{sn.PcieWidth ?? 0}      RX {LinkRate(sn.RxKBs)}      TX {LinkRate(sn.TxKBs)}";

            // Rebuild the process table only when membership actually changed.
            var key = string.Join("|", sn.Processes.Select(pr => $"{pr.Pid}:{pr.Name}:{pr.MemBytes}"));
            if (key == lastProcKey)
                return;
            lastProcKey = key;
            procList.Children.Clear();
            foreach (var pr in sn.Processes.OrderByDescending(pr => pr.MemBytes).Take(12))
            {
                var pidTb = Col(new TextBlock { Text = $"{pr.Pid}", FontSize = 13, Foreground = Secondary, TextAlignment = TextAlignment.Right }, 1);
                var kindTb = Col(new TextBlock { Text = pr.Compute ? "Compute" : "Graphics", FontSize = 13, Foreground = Secondary, TextAlignment = TextAlignment.Right }, 2);
                var memTb = Col(new TextBlock { Text = $"{pr.MemBytes / 1048576.0:0.#} MiB", FontSize = 13, Foreground = Secondary, TextAlignment = TextAlignment.Right }, 3);
                procList.Children.Add(new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,80,70,110"),
                    Children =
                    {
                        new TextBlock { Text = pr.Name, FontSize = 13, Foreground = ValueText },
                        pidTb, kindTb, memTb,
                    },
                });
            }
            if (sn.Processes.Count > 12)
                procList.Children.Add(new TextBlock { Text = $"+{sn.Processes.Count - 12} more", FontSize = 12, Foreground = Secondary });
        };
        _gpuCardUpdaters.Add((snap.Uuid, updater));

        // ---- header row ----
        var titleBlock = new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = $"GPU {snap.Index} · {snap.Name} {snap.PciBus}", FontSize = 17, FontWeight = FontWeight.Bold, Foreground = ValueText },
                new TextBlock { Text = snap.Uuid, Foreground = Secondary, FontSize = 12 },
            },
        };
        var powerControls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                powerNote,
                new TextBlock { Text = "Power", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center },
                powerBox,
                new TextBlock { Text = "W", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center },
                applyBtn,
            },
        };
        Grid.SetColumn(powerControls, 1);

        // ---- utilization bars ----
        var gpuBarPanel = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(0, 0, 24, 0),
            Children =
            {
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { new TextBlock { Text = "GPU", FontWeight = FontWeight.SemiBold }, Col(gpuPct, 1) }},
                gpuBar,
            },
        };
        var vramLabel = new TextBlock { Text = "VRAM", FontWeight = FontWeight.SemiBold };
        Grid.SetColumn(vramText, 1);
        var vramBarPanel = Col(new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { vramLabel, vramText } },
                vramBar,
            },
        }, 1);

        // ---- process header ----
        var procHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,80,70,110"),
            Margin = new Thickness(0, 6, 0, 2),
            Children =
            {
                new TextBlock { Text = "Processes", Foreground = Secondary, FontSize = 12 },
                Col(new TextBlock { Text = "PID", Foreground = Secondary, FontSize = 12, TextAlignment = TextAlignment.Right }, 1),
                Col(new TextBlock { Text = "Kind", Foreground = Secondary, FontSize = 12, TextAlignment = TextAlignment.Right }, 2),
                Col(new TextBlock { Text = "VRAM", Foreground = Secondary, FontSize = 12, TextAlignment = TextAlignment.Right }, 3),
            },
        };

        return new Border
        {
            Margin = new Thickness(0, 0, 0, 18),
            Padding = new Thickness(18, 14, 18, 16),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { titleBlock, powerControls } },
                    new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), Children = { gpuBarPanel, vramBarPanel } },
                    new Grid
                    {
                        ColumnDefinitions = new ColumnDefinitions("1.2*,1.4*,0.8*,1.2*,1.2*,1.6*"),
                        Children = { Col(tempV, 0), Col(powerV, 1), Col(pstateV, 2), Col(fanPctV, 3), Col(fanRpmV, 4), Col(clocksV, 5) },
                    },
                    pcieLine,
                    procHeader,
                    procList,
                },
            },
        };
    }

    // ---- Sensors page --------------------------------------------------------

    private bool _sensorsPageBuilt;
    private readonly List<(string Id, Action<double?> Update)> _sensorValueUpdaters = [];

    private string SensorsSubtitle()
    {
        var count = _app.Inventory.Count(IsSensorish);
        return $"{count} sensors · click a name to rename it";
    }

    /// <summary>Coarse grouping for the Sensors page, from the hwmon chip / backend.</summary>
    private static string SensorGroup(HardwareItem item)
    {
        if (item.Id.StartsWith("nvml:", StringComparison.OrdinalIgnoreCase))
            return "GPU";
        var parts = item.Id.Split(':');
        var chip = parts.Length > 1 ? parts[1] : "";
        return chip switch
        {
            "k10temp" => "CPU",
            "nct6799" or "nct6775" or "nct6798" or "nct6796" or "it87" => "Motherboard",
            "asusec" => "Chipset / ASUS EC",
            "nvme" => "Storage (NVMe)",
            "amd_hsmp_hwmon" => "AMD HSMP (SoC)",
            _ when chip.StartsWith("enp", StringComparison.OrdinalIgnoreCase)
                || chip.StartsWith("eth", StringComparison.OrdinalIgnoreCase) => "Network",
            _ => "Other",
        };
    }

    /// <summary>Rows shown on the Sensors page: temps, tach speeds, and GPU fan percent readback.</summary>
    private static bool IsSensorish(HardwareItem i) =>
        i.Kind is HardwareKind.Temperature or HardwareKind.Tach
        || (i.Kind == HardwareKind.Control && i.Id.StartsWith("nvml:", StringComparison.OrdinalIgnoreCase));

    /// <summary>Device key within a section: per NVMe drive, per GPU card; otherwise the chip group.</summary>
    private static string DeviceKey(HardwareItem item)
    {
        var parts = item.Id.Split(':');
        if (parts.Length > 2 && parts[1] == "nvme")
            return "nvme:" + parts[2]; // hwmon instance — drives share one group otherwise
        return item.Group;
    }

    // Maps hwmon index -> drive model. NVMe temp chips are /sys/class/hwmon/hwmonN (name "nvme");
    // the model file sits behind the device symlink and reads through it directly.
    private static readonly Lazy<Dictionary<string, string>> NvmeLabels = new(() =>
    {
        var map = new Dictionary<string, string>();
        try
        {
            foreach (var hm in Directory.GetDirectories("/sys/class/hwmon"))
            {
                string chip;
                try { chip = File.ReadAllText(Path.Combine(hm, "name")).Trim(); } catch { continue; }
                if (chip != "nvme")
                    continue;

                var idx = Path.GetFileName(hm).Replace("hwmon", "");
                try
                {
                    var model = File.ReadAllText(Path.Combine(hm, "device", "model")).Trim();
                    if (model.Length > 0)
                        map[idx] = model;
                }
                catch { }
            }
        }
        catch { }
        return map;
    });

    private static string DeviceLabel(string key) =>
        key.StartsWith("nvme:") && NvmeLabels.Value.TryGetValue(key[5..], out var lbl) ? lbl : key;

    private static readonly string[] GroupOrder =
        ["CPU", "GPU", "Motherboard", "Chipset / ASUS EC", "AMD HSMP (SoC)", "Storage (NVMe)", "Network", "Other"];

    private void EnsureSensorsPage()
    {
        if (_sensorsPageBuilt)
            return;
        _sensorsPageBuilt = true;
        SensorsContent.Children.Clear();
        _sensorValueUpdaters.Clear();

        var sensors = _app.Inventory
            .Where(IsSensorish)
            .GroupBy(SensorGroup)
            .OrderBy(g => Array.IndexOf(GroupOrder, g.Key) is var ix && ix >= 0 ? ix : 99);

        foreach (var group in sensors)
        {
            SensorsContent.Children.Add(new TextBlock
            {
                Text = group.Key,
                Foreground = Secondary,
                FontSize = 13,
                FontWeight = FontWeight.SemiBold,
                Margin = new Thickness(2, 0, 0, 0),
            });

            var body = new StackPanel();
            var devices = group.GroupBy(DeviceKey).OrderBy(g => DeviceLabel(g.Key), StringComparer.OrdinalIgnoreCase).ToList();

            if (devices.Count > 1)
            {
                foreach (var dev in devices)
                {
                    var inner = new StackPanel();
                    foreach (var item in dev.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                        inner.Children.Add(BuildSensorRow(item));

                    bool open = true;
                    var arrow = new TextBlock { Text = "▾", Foreground = Secondary, Width = 18, FontSize = 13 };
                    var hdrText = new TextBlock
                    {
                        Text = DeviceLabel(dev.Key),
                        Foreground = ValueText,
                        FontSize = 13.5,
                        FontWeight = FontWeight.SemiBold,
                    };
                    var hdrCount = new TextBlock
                    {
                        Text = dev.Count() == 1 ? "1 sensor" : $"{dev.Count()} sensors",
                        Foreground = Secondary,
                        FontSize = 12,
                        Margin = new Thickness(8, 0, 0, 0),
                    };
                    var header = new Button
                    {
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(0, 5, 0, 5),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Content = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 2,
                            Children = { arrow, hdrText, hdrCount },
                        },
                    };
                    header.Click += (_, _) =>
                    {
                        open = !open;
                        inner.IsVisible = open;
                        arrow.Text = open ? "▾" : "▸";
                    };

                    body.Children.Add(header);
                    body.Children.Add(inner);
                }
            }
            else
            {
                foreach (var item in group.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
                    body.Children.Add(BuildSensorRow(item));
            }

            SensorsContent.Children.Add(new Border
            {
                Background = CardBg,
                BorderBrush = CardBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 8, 14, 8),
                Child = body,
            });
        }
    }

    private Control BuildSensorRow(HardwareItem item)
    {
        bool isPercent = item.Kind == HardwareKind.Control;
        var original = isPercent ? item.Name + " %" : item.Name;
        var valueText = new TextBlock
        {
            MinWidth = 96,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 14,
            FontWeight = FontWeight.Bold,
            Foreground = ValueText,
        };

        // Quiet affordance again: the name is a borderless textbox; tooltip keeps the hardware truth.
        var nameBox = new TextBox
        {
            Text = isPercent ? _app.Settings.SensorAliases.GetValueOrDefault(item.Id, original) : _app.SensorLabel(item),
            Classes = { "cardname" },
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(nameBox, $"{original}\n{item.Group}\n{item.Id}");

        nameBox.LostFocus += (_, _) =>
        {
            var text = (nameBox.Text ?? "").Trim();
            if (text.Length == 0 || text == original)
            {
                _app.Settings.SensorAliases.Remove(item.Id);
                nameBox.Text = original; // hardware default, "%"-suffixed for GPU fan rows
            }
            else
            {
                _app.Settings.SensorAliases[item.Id] = text;
            }
            _app.Save();
            RebuildCurveCards(); // dropdowns across the app pick up the new friendly name
        };
        nameBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                TopLevel.GetTopLevel(nameBox)?.FocusManager?.ClearFocus();
        };

        _sensorValueUpdaters.Add((item.Id, v =>
            valueText.Text = v is null
                ? "—"
                : item.Kind == HardwareKind.Tach ? $"{v:0} RPM"
                : isPercent ? $"{v:0} %" : $"{v:0.#} °C"));

        return new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 3, 0, 3),
            Children = { nameBox, Col(valueText, 1) },
        };
    }

    private void UpdateSensorsPage()
    {
        if (!_sensorsPageBuilt || !SensorsPanel.IsVisible)
            return;
        foreach (var (id, update) in _sensorValueUpdaters)
            try { update(_app.Reading(id)); } catch { /* stale row */ }
    }

    private void UpdateGpuPage()
    {
        if (!_gpuPageBuilt || !GpusPanel.IsVisible)
            return;
        foreach (var snap in _app.Nvml.SnapshotAll())
            foreach (var (uuid, update) in _gpuCardUpdaters)
                if (uuid == snap.Uuid)
                    try { update(snap); } catch { /* stale card — next tick */ }
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

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Config-file shortcuts (menu parity): Ctrl+N / Ctrl+S / Ctrl+Shift+S / Ctrl+L.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.Key is Key.N or Key.S or Key.L)
        {
            if (e.Key == Key.N && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                OnNewConfig(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (e.Key == Key.S && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) OnSaveSetupAs(this, new RoutedEventArgs());
                else OnSaveConfig(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            if (e.Key == Key.L && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                OnLoadSetup(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        // PageUp/PageDown scroll whichever page is showing, regardless of child focus.
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.PageUp or Key.PageDown)
        {
            ScrollViewer? sv = SensorsPanel.IsVisible ? SensorsPanel
                : GpusPanel.IsVisible ? GpusPanel
                : HomePanel.IsVisible ? HomeScroll : null;
            if (sv is not null && sv.Extent.Height > sv.Viewport.Height)
            {
                double dy = sv.Viewport.Height * 0.85 * (e.Key == Key.PageDown ? 1 : -1);
                sv.Offset = new Vector(sv.Offset.X,
                    Math.Clamp(sv.Offset.Y + dy, 0, sv.Extent.Height - sv.Viewport.Height));
                e.Handled = true;
                return;
            }
        }

        if (e.KeyModifiers == KeyModifiers.Control)
        {
            RadioButton? target = e.Key switch
            {
                Key.D1 or Key.NumPad1 => NavHome,
                Key.D2 or Key.NumPad2 => NavCpu,
                Key.D3 or Key.NumPad3 => NavGpus,
                Key.D4 or Key.NumPad4 => NavSensors,
                Key.D5 or Key.NumPad5 => NavTheme,
                Key.D6 or Key.NumPad6 => NavTray,
                Key.D7 or Key.NumPad7 => NavSettings,
                _ => null,
            };
            if (target is not null)
            {
                target.IsChecked = true; // Checked handler performs the page switch
                e.Handled = true;
                return;
            }
        }
        base.OnKeyDown(e);
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

    private async Task DelayedStartAsync(int seconds)
    {
        await Task.Delay(seconds * 1000);
        Dispatcher.UIThread.Post(() =>
        {
            _app.Tick();
            _timer.Start();
        });
    }

    // ---------------------------------------------------------------- Theme page

    private static readonly (string Name, string Hex)[] AccentPresets =
    [
        ("Amber", "#F0A03C"), ("Gold", "#FFD54F"), ("Lime", "#9CCC65"), ("Green", "#7BC97B"),
        ("Teal", "#3CBFB4"), ("Sky", "#4FC3F7"), ("Blue", "#6C8AE4"), ("Violet", "#B07CE8"),
        ("Magenta", "#F06292"), ("Coral", "#EF6B6B"),
    ];

    private void BuildThemePage()
    {
        ThemeContent.Children.Clear();
        ThemeContent.Children.Add(ColHeader("Accent color"));

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var (name, hex) in AccentPresets)
        {
            var active = string.Equals(_app.Settings.AccentColor, hex, StringComparison.OrdinalIgnoreCase);
            var swatch = new Border
            {
                Width = 132, Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 10, 10),
                Background = CardBg,
                BorderBrush = active ? AccentTheme.Accent : CardBorder,
                BorderThickness = new Thickness(active ? 2 : 1),
                CornerRadius = new CornerRadius(10),
                Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new Border
                        {
                            Height = 34, CornerRadius = new CornerRadius(6),
                            Background = new SolidColorBrush(Color.Parse(hex)),
                        },
                        new TextBlock
                        {
                            Text = active ? $"{name} ✓" : name, FontSize = 13,
                            FontWeight = active ? FontWeight.SemiBold : FontWeight.Normal,
                        },
                    },
                },
            };
            swatch.PointerPressed += (_, _) =>
            {
                _app.Settings.AccentColor = hex;
                AccentTheme.Apply(hex);
                _app.Save();
                BuildThemePage(); // move the checkmark
            };
            wrap.Children.Add(swatch);
        }
        ThemeContent.Children.Add(wrap);

        // Custom hex input
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var hexBox = new TextBox
        {
            Text = _app.Settings.AccentColor, Width = 140, Watermark = "#RRGGBB",
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var applyBtn = new Button { Content = "Apply custom", Classes = { "accent" }, Padding = new Thickness(14, 6, 14, 6) };
        var note = new TextBlock
        {
            Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center, FontSize = 12,
        };
        void ApplyCustom()
        {
            var t = (hexBox.Text ?? "").Trim();
            if (!t.StartsWith('#')) t = "#" + t;
            if (!AccentTheme.IsValidHex(t))
            {
                note.Foreground = Warn;
                note.Text = "Not a valid color — use #RRGGBB.";
                return;
            }
            _app.Settings.AccentColor = t;
            AccentTheme.Apply(t);
            _app.Save();
            BuildThemePage();
        }
        applyBtn.Click += (_, _) => ApplyCustom();
        hexBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) ApplyCustom(); };
        row.Children.Add(hexBox);
        row.Children.Add(applyBtn);
        row.Children.Add(note);
        ThemeContent.Children.Add(row);
    }

    // ---------------------------------------------------------------- About page

    private void BuildAboutPage()
    {
        AboutContent.Children.Clear();
        AboutContent.Children.Add(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "OpenFan Linux", FontSize = 26, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = "v0.4.0 · hwmon + NVML · Avalonia / .NET 8",
                    Foreground = Secondary, FontSize = 13,
                },
                new TextBlock
                {
                    Text = "Native Ubuntu fan control: motherboard, case and AIO fans via hwmon sysfs, NVIDIA GPU fans and power limits via NVML. Curves (flat / graph / mix) are named library objects you assign to any sensor. Design homage to the Windows OpenFan program.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 640, Margin = new Thickness(0, 6, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left,
                },
            },
        });

        var sysCard = new Border
        {
            Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10), Padding = new Thickness(16, 12, 16, 12), MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "This system", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = GpuSubtitle(), Foreground = Secondary, FontSize = 12 },
                    new TextBlock { Text = $"{_app.Inventory.Count} sensors · {_app.Settings.Curves.Count} curves · config ~/.config/openfan/config.json", Foreground = Secondary, FontSize = 12 },
                },
            },
        };
        AboutContent.Children.Add(sysCard);

        var gh = new Button
        {
            Content = "github.com/russellmm/openfan-linux",
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = AccentTheme.Accent, FontWeight = FontWeight.SemiBold, FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0),
        };
        gh.Click += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo("xdg-open", "https://github.com/russellmm/openfan-linux") { UseShellExecute = true });
            }
            catch { }
        };
        AboutContent.Children.Add(gh);
    }

    // ---------------------------------------------------------------- Settings page

    private static TextBlock ColHeader(string text) => new()
    {
        Text = text, FontSize = 20, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4),
    };

    private Border SettingRow(string label, Control editor, string? tip = null)
    {
        var content = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var lbl = new TextBlock
        {
            Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
        };
        if (tip != null) ToolTip.SetTip(lbl, tip);
        Grid.SetColumn(lbl, 0);
        editor.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(editor, 1);
        content.Children.Add(lbl);
        content.Children.Add(editor);
        return new Border
        {
            Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10), Child = content,
        };
    }

    private static string AutostartFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "autostart", "openfan.desktop");

    private void WriteAutostart(bool enable)
    {
        try
        {
            if (!enable)
            {
                if (File.Exists(AutostartFilePath)) File.Delete(AutostartFilePath);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFilePath)!);
            // BaseDirectory always holds the apphost, even when launched as `dotnet openfan.dll`.
            var exe = Path.Combine(AppContext.BaseDirectory, "openfan");
            File.WriteAllText(AutostartFilePath,
                "[Desktop Entry]\nType=Application\nName=OpenFan\nComment=hwmon + NVML fan control\n" +
                $"Exec=\"{exe}\"\nTerminal=false\nIcon=openfan\nStartupWMClass=openfan\nX-GNOME-Autostart-enabled=true\n");
        }
        catch { /* settings checkbox state still reflects intent; file ops are best-effort */ }
    }

    private static string[] FindFanControlConflicts()
    {
        try
        {
            return new[] { "coolercontrold", "fancontrol", "speedfan", "nbfc" }
                .SelectMany(Process.GetProcessesByName)
                .Select(p => p.ProcessName)
                .Distinct().ToArray();
        }
        catch { return []; }
    }

    private void BuildSettingsPage()
    {
        SettingsContent.Children.Clear();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };

        // ---- column 1: General ----
        var general = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 20, 0) };
        Grid.SetColumn(general, 0);
        general.Children.Add(ColHeader("General"));

        var minimized = new CheckBox { IsChecked = _app.Settings.StartMinimized };
        minimized.IsCheckedChanged += (_, _) =>
        {
            _app.Settings.StartMinimized = minimized.IsChecked == true;
            _app.Save();
        };
        general.Children.Add(SettingRow("Start minimized", minimized, "Launch to the tray instead of the main window."));

        var autostart = new CheckBox { IsChecked = File.Exists(AutostartFilePath) };
        autostart.IsCheckedChanged += (_, _) =>
        {
            _app.Settings.StartAtLogin = autostart.IsChecked == true;
            WriteAutostart(_app.Settings.StartAtLogin);
            _app.Save();
        };
        general.Children.Add(SettingRow("Start app at user log on", autostart, "~/.config/autostart/openfan.desktop"));

        var bootWait = new NumericUpDown
        {
            Minimum = 0, Maximum = 60,
            Value = Math.Clamp(_app.Settings.StartupDelaySeconds, 0, 60), Width = 120,
        };
        ToolTip.SetTip(bootWait, "Pause before the first sensor scan — helps when hwmon modules (nct6775) load slowly after boot.");
        bootWait.ValueChanged += (_, _) =>
        {
            _app.Settings.StartupDelaySeconds = (int)bootWait.Value; // applied next launch
            _app.Save();
        };
        general.Children.Add(SettingRow("Wait for sensors at start (seconds)", bootWait));

        var refresh = new NumericUpDown
        {
            Minimum = 250, Maximum = 5000,
            Value = Math.Clamp(_app.Settings.RefreshMs, 250, 5000), Width = 120,
        };
        refresh.ValueChanged += (_, _) =>
        {
            _app.Settings.RefreshMs = (int)refresh.Value;
            _timer.Interval = TimeSpan.FromMilliseconds((int)refresh.Value);
            _app.Save();
        };
        general.Children.Add(SettingRow("Refresh interval (ms)", refresh));

        var cardScale = new NumericUpDown
        {
            Minimum = 60, Maximum = 130, Increment = 5,
            Value = (decimal)Math.Clamp(_app.Settings.CardScale * 100, 60, 130), Width = 120, FormatString = "{0:0} %",
        };
        cardScale.ValueChanged += (_, _) =>
        {
            _app.Settings.CardScale = (double)cardScale.Value / 100.0;
            _app.Save();
            RebuildCards();      // re-wrap every Home card at the new scale
            RebuildCurveCards();
        };
        general.Children.Add(SettingRow("Card size", cardScale, "Shrinks or grows the Home cards and their text."));

        grid.Children.Add(general);

        // ---- column 2: Hidden controls ----
        var hiddenCol = new StackPanel { Spacing = 10, Margin = new Thickness(0, 0, 20, 0) };
        Grid.SetColumn(hiddenCol, 1);
        hiddenCol.Children.Add(ColHeader("Hidden controls"));

        var hiddenItems = _app.Settings.Controls.Where(c => c.Hidden).ToList();
        if (hiddenItems.Count == 0)
        {
            hiddenCol.Children.Add(new TextBlock
            {
                Text = "None — hide a card from its ⋮ menu on the Home page.",
                Foreground = Secondary, FontSize = 13,
            });
        }
        foreach (var hc in hiddenItems)
        {
            var hwItem = _app.Inventory.FirstOrDefault(i => i.Id == hc.Id);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(hc.Name) ? (hwItem?.Name ?? hc.Id) : hc.Name,
                VerticalAlignment = VerticalAlignment.Center, FontSize = 13,
            };
            ToolTip.SetTip(name, hc.Id);
            Grid.SetColumn(name, 0);
            var unhide = new Button
            {
                Content = "Unhide", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Foreground = Accent, FontWeight = FontWeight.SemiBold, FontSize = 13,
            };
            Grid.SetColumn(unhide, 1);
            unhide.Click += (_, _) =>
            {
                hc.Hidden = false;
                _app.Save();
                RebuildCards();
                BuildSettingsPage();
            };
            row.Children.Add(name);
            row.Children.Add(unhide);
            hiddenCol.Children.Add(row);
        }
        grid.Children.Add(hiddenCol);

        // ---- column 3: System ----
        var system = new StackPanel { Spacing = 10 };
        Grid.SetColumn(system, 2);
        system.Children.Add(ColHeader("System"));

        var conflicts = FindFanControlConflicts();
        if (conflicts.Length > 0)
        {
            system.Children.Add(new Border
            {
                Background = CardBg, BorderBrush = Warn, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = "Fan-control conflict detected", Foreground = Warn, FontWeight = FontWeight.SemiBold },
                        new TextBlock { Text = $"Running: {string.Join(", ", conflicts)} — two controllers writing the same fans will fight. Stop it (e.g. sudo systemctl stop fancontrol) before ticking Apply curves.", Foreground = Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    },
                },
            });
        }

        var helperOn = _app.NvmlHelper.IsAvailable;
        system.Children.Add(new Border
        {
            Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock
                    {
                        Text = helperOn ? "GPU write helper: connected ✓" : "GPU write helper: not installed",
                        Foreground = helperOn ? new SolidColorBrush(Color.Parse("#7BC97B")) : Secondary, FontWeight = FontWeight.SemiBold,
                    },
                    new TextBlock
                    {
                        Text = helperOn
                            ? "/run/openfan/helper.sock (root) — GPU fan curves and power limits available. Check: systemctl status openfan-helper"
                            : "GPU fan control and power limits need the root helper daemon. Install steps: packaging/README.md",
                        Foreground = Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        });

        system.Children.Add(new Border
        {
            Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 10, 14, 10),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = $"Sensor sources: hwmon + NVML ({_app.Inventory.Count} sensors)", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "Chips and GPUs are detected automatically every second — plug in hardware and it appears. Rename anything on the Sensors page.", Foreground = Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                },
            },
        });

        grid.Children.Add(system);
        SettingsContent.Children.Add(grid);
    }

    /// <summary>Tray toggled Apply curves — refresh the header checkbox without re-triggering the handler.</summary>
    public void SyncApplyCurvesBox()
    {
        ApplyCurvesBox.IsChecked = _app.Settings.ApplyCurves;
    }

    private void RebuildCards()
    {
        _cards.Clear();
        var controls = _app.Inventory.Where(i => i.Kind == HardwareKind.Control).ToList();

        // User order first (ControlOrder), unranked cards keep natural position after them.
        var order = _app.Settings.ControlOrder;
        controls = controls
            .Select((c, nat) => (c, nat))
            .OrderBy(x => order.IndexOf(x.c.Id) is var ix && ix >= 0 ? ix : int.MaxValue)
            .ThenBy(x => x.nat)
            .Select(x => x.c)
            .ToList();

        var items = new List<Control>();
        var hidden = new List<HardwareItem>();
        foreach (var item in controls)
        {
            if (_app.Settings.Controls.FirstOrDefault(c => c.Id == item.Id)?.Hidden == true)
                hidden.Add(item);
            else
                items.Add(BuildCard(item));
        }

        foreach (var h in hidden)
        {
            var name = _app.Settings.Controls.FirstOrDefault(c => c.Id == h.Id)?.Name is { Length: > 0 } n ? n : h.Name;
            var unhide = new Button
            {
                Content = $"{name} — unhide",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = CardBorder,
                Foreground = Secondary,
                FontSize = 12,
                Margin = new Thickness(6),
                Padding = new Thickness(10, 5, 10, 5),
            };
            unhide.Click += (_, _) =>
            {
                var cfg = FindOrCreateCfg(h);
                cfg.Hidden = false;
                _app.Save();
                RebuildCards();
            };
            items.Add(unhide);
        }

        Cards.ItemsSource = items;
        UpdateValues();
    }

    /// <summary>⋮ menu for a fan card: tach pairing, ordering, hide, release.</summary>
    private Button BuildCardMenu(HardwareItem item)
    {
        var fly = new MenuFlyout();

        // Pair tachometer — Auto (id swap) plus every tach in the inventory.
        var pair = new MenuItem { Header = "Pair tachometer" };
        var current = _app.PairedTachId(item);
        var autoId = item.Backend.Equals("nvml", StringComparison.OrdinalIgnoreCase)
            ? item.Id.Replace(":fan:", ":tach:")
            : item.Id.Replace(":pwm:", ":fan:");

        var autoMi = new MenuItem { Header = "Auto", IsChecked = current == autoId };
        autoMi.Click += (_, _) =>
        {
            FindOrCreateCfg(item).PairedTachId = null;
            _app.Save();
            RebuildCards();
        };
        pair.Items.Add(autoMi);

        foreach (var t in _app.Inventory.Where(i => i.Kind == HardwareKind.Tach)
                     .OrderBy(i => i.Group).ThenBy(i => i.Name))
        {
            var mi = new MenuItem
            {
                Header = $"{_app.SensorLabel(t)} · {t.Group}",
                IsChecked = t.Id == current,
            };
            mi.Click += (_, _) =>
            {
                FindOrCreateCfg(item).PairedTachId = t.Id;
                _app.Save();
                RebuildCards();
            };
            pair.Items.Add(mi);
        }
        fly.Items.Add(pair);

        var up = new MenuItem { Header = "Move up" };
        up.Click += (_, _) => MoveCard(item, -1);
        var down = new MenuItem { Header = "Move down" };
        down.Click += (_, _) => MoveCard(item, +1);
        fly.Items.Add(up);
        fly.Items.Add(down);

        var hide = new MenuItem { Header = "Hide this card" };
        hide.Click += (_, _) =>
        {
            FindOrCreateCfg(item).Hidden = true;
            _app.Save();
            RebuildCards();
        };
        fly.Items.Add(hide);

        var release = new MenuItem { Header = "Release to board default" };
        release.Click += (_, _) =>
        {
            var cfg = FindOrCreateCfg(item);
            cfg.Enabled = false;
            _app.Save();
            _app.ManualRestore(item.Id); // writes the pre-takeover enable mode back
            RebuildCards();
        };
        fly.Items.Add(release);

        var btn = new Button
        {
            Content = "⋮",
            Background = Brushes.Transparent,
            
            BorderThickness = new Thickness(0),
            Foreground = Secondary,
            Padding = new Thickness(4, 0, 4, 0),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Flyout = fly; // Button.Flyout auto-opens on click (same mechanism as the header menu)
        return btn;
    }

    /// <summary>Swap a card with its visible neighbor, materializing ControlOrder on first use.</summary>
    private void MoveCard(HardwareItem item, int delta)
    {
        var visible = _app.Inventory
            .Where(i => i.Kind == HardwareKind.Control
                     && _app.Settings.Controls.FirstOrDefault(c => c.Id == i.Id)?.Hidden != true)
            .Select((c, nat) => (c, nat))
            .OrderBy(x => _app.Settings.ControlOrder.IndexOf(x.c.Id) is var ix && ix >= 0 ? ix : int.MaxValue)
            .ThenBy(x => x.nat)
            .Select(x => x.c.Id)
            .ToList();

        var i0 = visible.IndexOf(item.Id);
        var i1 = i0 + delta;
        if (i0 < 0 || i1 < 0 || i1 >= visible.Count)
            return;
        (visible[i0], visible[i1]) = (visible[i1], visible[i0]);

        // Rank visible cards; hidden/unranked ids follow behind untouched.
        _app.Settings.ControlOrder = visible;
        _app.Save();
        RebuildCards();
    }

    private Control BuildCard(HardwareItem item)
    {
        var existingCfg = _app.Settings.Controls.FirstOrDefault(c => c.Id == item.Id);

        // Friendly name: click to rename (persisted per control; original id stays in the tooltip).
        var title = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(existingCfg?.Name) ? item.Name : existingCfg!.Name,
            Classes = { "cardname" },
        };
        ToolTip.SetTip(title, $"{item.Name}\n{item.Id}\nClick to rename.");
        title.LostFocus += (_, _) =>
        {
            var c = FindOrCreateCfg(item);
            var text = (title.Text ?? "").Trim();
            if (text.Length == 0)
            {
                title.Text = c.Name = item.Name; // never leave a card unnamed
                _app.Save();
                return;
            }
            if (text != c.Name)
            {
                c.Name = text;
                _app.Save();
            }
        };
        title.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                TopLevel.GetTopLevel(title)?.FocusManager?.ClearFocus(); // blur → commit
        };
        var group = new TextBlock { Text = item.Group, FontSize = 11, Foreground = Secondary };

        var suppress = false; // checkbox ↔ dropdown mutual updates must not re-enter

        var check = new CheckBox
        {
            Content = "Curve",
            FontSize = 13,
            IsChecked = existingCfg is { Enabled: true },
        };

        var mode = new ComboBox { Width = 280, HorizontalAlignment = HorizontalAlignment.Left };
        FlatCombo(mode);
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

        // Calibrate link — only where a tachometer exists to verify speed (GPU fans have none
        // on this driver, so their commanded % is taken at face value).
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        var tachId = _app.PairedTachId(item);
        // GPU fans keep tach items in inventory but NVML reports no RPM on driver 595 —
        // calibration needs real speed feedback, so require an actual reading for nvml controls.
        var tachWorks = _app.Inventory.Any(i => i.Id == tachId)
            && (!item.Id.StartsWith("nvml:", StringComparison.OrdinalIgnoreCase) || _app.Reading(tachId) is not null);
        if (tachWorks)
        {
            var calLink = new Button
            {
                Content = "Calibrate",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = Accent,
                FontWeight = FontWeight.SemiBold,
            };
            calLink.Click += (_, _) =>
                new CalibrationWindow(_app, item).Show(this); // hands the fan over; resumes on close
            links.Children.Add(calLink);

            if (existingCfg is { Calibration.Count: >= 2 })
                links.Children.Add(new TextBlock
                {
                    Text = $"calibrated ✓ ({existingCfg.Calibration.Count} pts)",
                    Foreground = Secondary,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 12,
                });
        }
        else if (item.Id.StartsWith("nvml:", StringComparison.OrdinalIgnoreCase))
        {
            // No tachometer feedback on these fans: let the user supply a Command% -> RPM table
            // (e.g. measured with a tachometer or copied from Windows OpenFan), and the card shows
            // an interpolated virtual speed instead of bare %.
            var tableLink = new Button
            {
                Content = existingCfg is { Calibration.Count: >= 2 } ? "RPM table ✓" : "RPM table",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Foreground = Accent,
                FontWeight = FontWeight.SemiBold,
            };
            tableLink.Click += (_, _) =>
            {
                var cfgForTable = existingCfg ?? FindOrCreateCfg(item);
                new ManualRpmWindow($"{item.Name} — fan speed table", cfgForTable.Calibration, pts =>
                {
                    cfgForTable.Calibration = pts;
                    _app.Save();
                    RebuildCards();
                    UpdateValues();
                }).Show(this);
            };
            links.Children.Add(tableLink);
        }

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
                Children =
                {
                    new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { title, Col(BuildCardMenu(item), 1) } },
                    group, check, mode, ComboUnderline(), valueLine, links, errorLine,
                },
            },
        };

        _cards[item.Id] = new CardUi
        {
            Item = item, ValueLine = valueLine, CurveCheck = check, Mode = mode, ErrorLine = errorLine,
        };
        return Scaled(card);
    }

    /// <summary>Interpolated fan speed from a manual Command%-&gt;RPM table (fans with no tachometer).</summary>
    private static double? EstimatedRpm(ControlSettings? cfg, double? percent)
    {
        if (cfg is not { Calibration.Count: >= 2 } || percent is not double p)
            return null;
        return new CalibrationMap(cfg.Calibration.Select(s => new CalibrationSample(s.Percent, s.Rpm))).RpmAt(p);
    }

    private void UpdateValues()
    {
        var applying = _app.Settings.ApplyCurves;
        foreach (var (id, card) in _cards)
        {
            var cfg = _app.Settings.Controls.FirstOrDefault(c => c.Id == id);
            var commanded = applying && cfg is { Enabled: true } ? _app.CommandedPercent(cfg) : null;
            var rpm = _app.PairedRpm(card.Item);
            // NVML fan controls report their measured duty under the control's own id.
            var actual = id.StartsWith("nvml:", StringComparison.OrdinalIgnoreCase) ? _app.Reading(id) : null;
            // Fans with no tachometer but a manual RPM table get an interpolated virtual speed.
            var estRpm = rpm is null ? EstimatedRpm(cfg, commanded ?? actual) : null;

            if (commanded is double pct)
            {
                card.ValueLine.Text = rpm is not null
                    ? $"{pct:0.#} %     {rpm:0} RPM"
                    : estRpm is double ev1
                        ? $"{pct:0.#} %     {ev1:0} RPM (est)"
                        : actual is not null && Math.Abs(actual.Value - pct) > 1.5
                            ? $"{pct:0.#} %   now {actual:0} %" // mid-ramp: target vs measured
                            : $"{pct:0.#} %";
                card.ValueLine.Foreground = ValueText;
            }
            else if (actual is not null)
            {
                card.ValueLine.Text = rpm is not null
                    ? $"{actual:0} %     {rpm:0} RPM"
                    : estRpm is double ev2
                        ? $"{actual:0} %     {ev2:0} RPM (est)"
                        : $"{actual:0} %";
                card.ValueLine.Foreground = Secondary; // monitoring shows the real fan speed
            }
            else
            {
                card.ValueLine.Text = rpm is null
                    ? (applying ? "—" : "auto")
                    : $"auto     {rpm:0} RPM";
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

        UpdateCpuPage();
        UpdateGpuPage();
        UpdateSensorsPage();
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
            Background = Brushes.Transparent,
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

        return Scaled(new Border
        {
            Width = curve.Type.Equals("mix", StringComparison.OrdinalIgnoreCase) ? 320 : 300,
            Margin = new Thickness(6),
            Padding = new Thickness(14, 12, 14, 12),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = body,
        });
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
            Background = Brushes.Transparent,
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
        FlatCombo(sensorBox);
        var sensorItems = new List<(ComboBoxItem Item, string Id, string BaseLabel)>();
        foreach (var t in _app.Inventory
                     .Where(i => i.Kind == HardwareKind.Temperature)
                     .OrderBy(i => i.Group).ThenBy(i => i.Name))
        {
            var item = new ComboBoxItem { Tag = t.Id };
            sensorBox.Items.Add(item);
            sensorItems.Add((item, t.Id, _app.SensorLabel(t))); // sensor name only — group is noise here
        }
        var suppress = false;
        string paintedSelection = ""; // last string pushed through the combo's selection box
        int WantedIndex() => sensorItems.FindIndex(s => s.Id == curve.SensorId);

        void ApplySelection()
        {
            var want = WantedIndex();
            // Force through -1 so a stale (blank) selection box rebuilds.
            suppress = true;
            sensorBox.SelectedIndex = -1;
            sensorBox.SelectedIndex = want;
            suppress = false;
        }

        ApplySelection();
        // Code-created ComboBoxes drop pre-attach selection — re-assert once in the tree.
        sensorBox.AttachedToVisualTree += (_, _) => ApplySelection();

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
            Background = Brushes.Transparent, // whole padding box clickable, not just glyph ink
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
        body.Children.Add(ComboUnderline());
        body.Children.Add(outRow);
        body.Children.Add(preview);

        _curveUpdaters.Add(() =>
        {
            // Re-assert the bound sensor if anything cleared or failed to render the combo.
            // SelectionBoxItem == null catches the sneaky case: rebuilt while Home was hidden (renaming a
            // sensor on the Sensors page) — SelectedIndex is correct but nothing ever painted the box.
            var want = WantedIndex();
            if (want >= 0 && (sensorBox.SelectedIndex != want || sensorBox.SelectedItem is null ||
                              (sensorBox.IsEffectivelyVisible && sensorBox.SelectionBoxItem is null)))
                ApplySelection();

            foreach (var (item, id, baseLabel) in sensorItems)
            {
                var r = _app.Reading(id);
                item.Content = r is null ? baseLabel : $"{baseLabel}   —   {r:0.#} °C";
            }

            // The closed combo paints a snapshot of the selected item's content — mutating Content above
            // never repaints it. Re-assert the selection whenever the label changed (skipped while the
            // popup is open, which would dismiss it mid-click).
            if (!sensorBox.IsDropDownOpen && sensorBox.SelectedItem is ComboBoxItem sel)
            {
                var wantContent = sel.Content as string;
                if (wantContent != paintedSelection)
                {
                    paintedSelection = wantContent;
                    ApplySelection();
                }
            }

            var temp = curve.SensorId is null ? null : _app.Reading(curve.SensorId);
            outputText.Text = _app.CurveOutput(curve.Id) is double o ? $"{o:0.#} %" : "—";
            DrawMiniPreview(preview, curve, temp);
        });
    }

    private void BuildMixBody(CurveSettings curve, StackPanel body)
    {
        body.Children.Add(new TextBlock { Text = "Function", FontSize = 11, Foreground = Secondary });

        var funcBox = new ComboBox { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        FlatCombo(funcBox);
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
                    Background = Brushes.Transparent, // null would make only the glyph strokes clickable
                    BorderThickness = new Thickness(0),
                    Foreground = Secondary,
                    Padding = new Thickness(5, 0, 5, 0),
                };
                removeBtn.Click += (_, _) =>
                {
                    curve.ChildCurveIds.Remove(childId);
                    _app.Save();
                    RebuildChildren();
                    RebuildAddChoices(); // the removed curve is available again — put it back in the dropdown
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
        FlatCombo(addBox); // same flat look as the other card dropdowns

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
        body.Children.Add(ComboUnderline());
        body.Children.Add(addBox);
        body.Children.Add(childList);
        body.Children.Add(outputText);

        RebuildChildren();
        RebuildAddChoices();

        // Curve renames elsewhere must not leave stale names in this card's lists — rebuild only when the
        // (ids+names) signature actually changes, so an open dropdown is never rebuilt out from under the user.
        var mixSig = "";
        void RefreshMixLists()
        {
            string NameOf(string id) => _app.Settings.Curves.FirstOrDefault(c => c.Id == id)?.Name ?? id;
            var sig = string.Join("|", curve.ChildCurveIds.Select(NameOf))
                + "#" + string.Join("|", _app.Settings.Curves
                    .Where(c => c.Id != curve.Id
                                && !c.Type.Equals("mix", StringComparison.OrdinalIgnoreCase)
                                && !curve.ChildCurveIds.Contains(c.Id))
                    .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(c => c.Id + ":" + c.Name));
            if (sig == mixSig)
                return;
            mixSig = sig;
            RebuildChildren();
            RebuildAddChoices();
        }

        _curveUpdaters.Add(() =>
        {
            outputText.Text = _app.CurveOutput(curve.Id) is double o ? $"{o:0.#} %" : "—";
            RefreshMixLists();
        });
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

    /// <summary>Floating confirmation at the bottom of the window; auto-hides after 3 s.</summary>
    private DispatcherTimer? _toastTimer;

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.IsVisible = true;
        if (_toastTimer is null)
        {
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer!.Stop();
                Toast.IsVisible = false;
            };
        }
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Ctrl+S / menu: save into whichever config file is currently active.</summary>
    private void OnSaveConfig(object? sender, RoutedEventArgs e)
    {
        _app.Save();
        ShowToast($"Configuration saved — {Path.GetFileName(_app.ActiveConfigPath)}");
    }

    /// <summary>Ctrl+N: start a fresh empty configuration in a new file and switch to it.</summary>
    private async void OnNewConfig(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(ProfilesDir);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Create new configuration",
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync($"file://{ProfilesDir}/"),
                SuggestedFileName = "new-config.json",
                FileTypeChoices = [new FilePickerFileType("OpenFan configuration") { Patterns = ["*.json"] }],
            });
            if (file?.Path.LocalPath is string path)
            {
                new SettingsStore(path).Save(new AppSettings()); // empty config on disk first…
                _app.SwitchConfig(path);                          // …then load it as the live settings
                AfterConfigSwitch();
                ShowToast($"New configuration — {Path.GetFileName(path)} (changes save here)");
            }
        }
        catch (Exception ex)
        {
            StatusNote.Text = $"Create failed: {ex.Message}";
        }
    }

    private void OnOpenErrorLog(object? sender, RoutedEventArgs e)
    {
        var path = ErrorLog.Path;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo("xdg-open", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusNote.Text = $"Could not open {path} ({ex.Message})";
        }
    }

    private void OnMenuExit(object? sender, RoutedEventArgs e) => ExitBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    /// <summary>Common UI refresh after the active config file changes underneath us.</summary>
    private void AfterConfigSwitch()
    {
        AccentTheme.Apply(_app.Settings.AccentColor); // per-config accent
        ApplyCurvesBox.IsChecked = _app.Settings.ApplyCurves;
        _sensorsPageBuilt = false;                    // aliases may differ now
        RebuildCards();
        RebuildCurveCards();
        UpdateStatus();
        UpdateTitle();
    }

    /// <summary>Title mirrors the Windows app: "OpenFan — myconfig.json" when a named file is active.</summary>
    private void UpdateTitle() =>
        Title = _app.ActiveConfigPath == SettingsStore.DefaultPath
            ? "OpenFan"
            : $"OpenFan — {Path.GetFileName(_app.ActiveConfigPath)}";

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
                new SettingsStore(path).Save(_app.Settings); // write FIRST…
                _app.SwitchConfig(path);                      // …then make it the active config file
                UpdateTitle();
                ShowToast($"Configuration saved — {Path.GetFileName(path)}");
            }
        }
        catch (Exception ex)
        {
            // No portal/picker on this desktop — fall back to a timestamped profile file.
            var path = Path.Combine(ProfilesDir, $"openfan-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            Directory.CreateDirectory(ProfilesDir);
            new SettingsStore(path).Save(_app.Settings); // write FIRST (picker unavailable)
            _app.SwitchConfig(path);
            UpdateTitle();
            ShowToast($"Configuration saved — {Path.GetFileName(path)}");
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

            _app.SwitchConfig(path); // load + all future changes save into this file
            AfterConfigSwitch();
            ShowToast($"Configuration loaded — {Path.GetFileName(path)} (changes save here)");
        }
        catch (Exception ex)
        {
            StatusNote.Text = $"Load failed: {ex.Message}";
        }
    }

    // ---- shared helpers -----------------------------------------------------

    /// <summary>Optional uniform scale (Settings ▸ Card size): shrinks/grows a Home card and its text as one unit.</summary>
    private Control Scaled(Control card)
    {
        var s = _app.Settings.CardScale;
        if (s <= 0 || Math.Abs(s - 1.0) < 0.005)
            return card;
        return new LayoutTransformControl { LayoutTransform = new ScaleTransform(s, s), Child = card };
    }

    private void OnGlobalPointerPressedForBlur(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source)
            return;
        // Walk up from the click target: anything inside (or part of) an editable control keeps focus.
        for (Visual? v = source; v is not null && v != this; v = v.Parent as Visual)
            if (v is TextBox or ComboBox or ComboBoxItem or NumericUpDown)
                return;
        FocusManager?.ClearFocus(); // click on card body, background, scrollbar … → leave edit mode
    }

    /// <summary>Flatten a card combo so only its content + chevron show (the underline rule does the framing).</summary>
    private static void FlatCombo(ComboBox cb)
    {
        cb.Background = Brushes.Transparent;
        cb.BorderThickness = new Thickness(0);
    }

    /// <summary>The horizontal rule under a card's dropdown, spanning the card width.</summary>
    private static Border ComboUnderline() => new()
    {
        Height = 1,
        Background = ComboRule,
        Margin = new Thickness(0, 2, 0, 4),
    };

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

        void ApplySelection()
        {
            var want = cfg is { Enabled: true }
                ? items.FirstOrDefault(i => (string?)i.Tag == cfg.CurveId) ?? items[0]
                : items[0];
            if (!ReferenceEquals(mode.SelectedItem, want))
                mode.SelectedItem = want;
        }

        ApplySelection();
        mode.AttachedToVisualTree += (_, _) => ApplySelection(); // pre-attach selection is dropped
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
