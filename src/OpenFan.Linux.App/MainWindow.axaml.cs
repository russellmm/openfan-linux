using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenFan.Core.Config;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.App;

public sealed partial class MainWindow : Window
{
    private static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#9A9A9A"));
    private static readonly IBrush CardBg = new SolidColorBrush(Color.Parse("#1E1E1E"));
    private static readonly IBrush CardBorder = new SolidColorBrush(Color.Parse("#2E2E2E"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#E24B4B"));
    private static readonly IBrush Warn = new SolidColorBrush(Color.Parse("#E5A64B"));

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

        VersionNote.Text = $"v{typeof(MainWindow).Assembly.GetName().Version?.ToString(3)} · hwmon + NVML";

        _app.InventoryChanged += RebuildCards;
        _app.Ticked += UpdateValues;
        RebuildCards();
        UpdateStatus();
        _app.Tick(); // first paint now, not one timer-tick late

        _timer.Tick += (_, _) => _app.Tick();
        _timer.Start();
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

        var mode = new ComboBox
        {
            ItemsSource = new[] { "Monitor", "Flat" },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var slider = new Slider
        {
            Minimum = item.MinPercent,
            Maximum = 100,
            Width = 290,
            HorizontalAlignment = HorizontalAlignment.Left,
            IsVisible = false,
        };

        // Initial state from settings — read-only lookup; entries are created on first user edit.
        var existingCfg = _app.Settings.Controls.FirstOrDefault(c => c.Id == item.Id);
        var existingFlat = _app.Settings.Curves.FirstOrDefault(c => c.Id == CurveIdFor(item.Id));
        mode.SelectedItem = existingCfg is { Enabled: true } ? "Flat" : "Monitor";
        slider.Value = Math.Clamp(existingFlat?.Percent ?? 50, item.MinPercent, 100);
        slider.IsVisible = existingCfg is { Enabled: true };

        mode.SelectionChanged += (_, _) =>
        {
            var c = FindOrCreateCfg(item);
            if ((string?)mode.SelectedItem == "Flat")
            {
                var curve = GetOrCreateFlatCurve(item.Id);
                curve.Percent = Math.Clamp(slider.Value, item.MinPercent, 100);
                c.CurveId = CurveIdFor(item.Id);
                c.Enabled = true;
                slider.IsVisible = true;
            }
            else
            {
                c.Enabled = false; // FanController restores this control next tick
                slider.IsVisible = false;
            }
            _app.Save();
            UpdateValues();
        };

        slider.ValueChanged += (_, _) =>
        {
            var c = FindOrCreateCfg(item);
            var curve = GetOrCreateFlatCurve(item.Id);
            curve.Percent = Math.Round(slider.Value);
            c.CurveId = CurveIdFor(item.Id);
            if (!c.Enabled)
            {
                c.Enabled = true;
                mode.SelectedItem = "Flat";
            }
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

    private static string CurveIdFor(string controlId) => $"flat:{controlId}";

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

    private CurveSettings GetOrCreateFlatCurve(string controlId)
    {
        var id = CurveIdFor(controlId);
        var curve = _app.Settings.Curves.FirstOrDefault(c => c.Id == id);
        if (curve is null)
        {
            curve = new CurveSettings { Id = id, Type = "flat", Name = "Flat", Percent = 50 };
            _app.Settings.Curves.Add(curve);
        }
        return curve;
    }
}
