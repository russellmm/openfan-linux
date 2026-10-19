using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenFan.Linux.Hw;

namespace OpenFan.Linux.App;

public sealed partial class MainWindow
{
    private readonly CpuFrequencyMonitor _frequencyMonitor = new();
    private readonly DispatcherTimer _frequencyTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _frequencyReading, _frequencyClosed, _frequencyReset;
    private IReadOnlyList<CpuFrequencyMonitor.CoreReading> _frequencyCores = [];
    private readonly Dictionary<string, TextBlock[]> _frequencyRows = [];
    private TextBlock? _frequencySummary, _frequencyStatus, _frequencyRange;
    private WrapPanel? _frequencyTables;
    private CheckBox? _frequencyIndividual;
    private WrapPanel? _frequencyAllControls;
    private Button? _frequencyIndividualApply;
    private readonly Dictionary<string, TextBox> _frequencyEditors = [];
    private readonly HashSet<string> _frequencyDirty = [];
    private readonly Dictionary<string, string?> _frequencyPopulatedText = [];
    private bool _frequencyPopulating, _frequencyAllInitialized;
    private NumericUpDown? _frequencyLimit;
    private Button? _frequencyApply, _frequencyDefault;
    private bool _frequencyApplying;
    private void BuildCpuFrequencyPage()
    {
        _frequencySummary = new TextBlock { Foreground = Secondary, FontSize = 12, Text = "Reading CPU clocks…" };
        var reset = new Button { Content = "Reset statistics", Height = 30, FontSize = 12 };
        reset.Click += (_, _) => { _frequencyReset = true; _ = ReadCpuFrequenciesAsync(); };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new StackPanel { Spacing = 3, Children =
        {
            new TextBlock { Text = "Core clocks", FontSize = 17, FontWeight = FontWeight.SemiBold, Foreground = ValueText },
            _frequencySummary,
        }});
        heading.Children.Add(Col(reset, 1));
        _frequencyIndividual = new CheckBox { Content = "Set individual cores", FontSize = 13 };
        _frequencyIndividualApply = new Button { Content = "Apply individual caps", Height = 32, IsEnabled = false };
        _frequencyIndividualApply.Click += async (_, _) => await ApplyIndividualFrequenciesAsync();
        _frequencyIndividual.IsCheckedChanged += (_, _) =>
        {
            _frequencyDirty.Clear();
            PopulateFrequencyEditors(force: true);
            UpdateFrequencyRange(resetValue: true);
            _frequencyStatus!.Foreground = Secondary;
            _frequencyStatus.Text = _frequencyIndividual.IsChecked == true
                ? "Edit the caps on the right, then Apply individual caps. Live readings do not overwrite your edits."
                : "All-core mode: one ceiling for every core. Changing modes does not change any live cap.";
        };
        _frequencyLimit = new NumericUpDown { Width = 120, Minimum = 1, Maximum = 10000, Increment = 100, FormatString = "0.###" };
        _frequencyApply = new Button { Content = "Apply cap", Height = 32 };
        _frequencyDefault = new Button { Content = "Hardware maximum", Height = 32 };
        _frequencyRange = new TextBlock { Foreground = Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        _frequencyStatus = new TextBlock { Foreground = Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "Caps remain until reboot or another power tool changes them. They are not saved or re-applied at startup." };
        _frequencyApply.Click += async (_, _) => await ApplyFrequencyAsync(restore: false);
        _frequencyDefault.Click += async (_, _) => await ApplyFrequencyAsync(restore: true);
        _frequencyTables = new WrapPanel { Orientation = Orientation.Horizontal };
        _frequencyTables.SizeChanged += (_, _) => SizeFrequencyTables();
        _frequencyAllControls = new WrapPanel { Orientation = Orientation.Horizontal, Children =
        {
            new TextBlock { Text = "All cores", Foreground = ValueText, FontSize = 13, Margin = new Thickness(0, 6, 12, 6) },
            _frequencyLimit,
            new TextBlock { Text = "MHz", Foreground = Secondary, Margin = new Thickness(6, 6, 12, 6) },
            _frequencyApply, _frequencyDefault,
        }};
        var individualControls = new WrapPanel { Orientation = Orientation.Horizontal, Children =
        {
            _frequencyIndividual, _frequencyIndividualApply,
        }};
        _frequencyIndividual.Margin = new Thickness(0, 0, 18, 0);
        _frequencyApply.Margin = new Thickness(0, 0, 8, 6);
        _frequencyDefault.Margin = new Thickness(0, 0, 0, 6);
        CpusContent.Children.Add(new Border
        {
            Padding = new Thickness(16), Background = CardBg, BorderBrush = CardBorder,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Child = new StackPanel { Spacing = 12, Children =
            {
                heading, _frequencyAllControls, individualControls, _frequencyRange, _frequencyStatus,
                new TextBlock { Text = "MHz · current / minimum / maximum / average since this page opened or statistics were reset",
                    Foreground = Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                _frequencyTables,
                new TextBlock { Text = "Each row is one physical core. Current is the highest kernel-reported clock of its two threads. Idle threads may retain a previous reading; these are sampled clocks, not effective clocks. Hardware may round a requested cap slightly.",
                    Foreground = Secondary, FontSize = 12, TextWrapping = TextWrapping.Wrap },
            }},
        });
        _frequencyTimer.Tick += async (_, _) => await ReadCpuFrequenciesAsync();
        _frequencyTimer.Start();
        Closed += (_, _) => { _frequencyClosed = true; _frequencyTimer.Stop(); };
        _ = ReadCpuFrequenciesAsync();
    }

    private async Task ReadCpuFrequenciesAsync()
    {
        if (_frequencyClosed || _frequencyReading || !IsVisible || !CpusPanel.IsVisible) return;
        _frequencyReading = true;
        var reset = _frequencyReset; _frequencyReset = false;
        try
        {
            var cores = await Task.Run(() => { if (reset) _frequencyMonitor.ResetStatistics(); return _frequencyMonitor.ReadCores(); });
            if (_frequencyClosed) return;
            var changed = !cores.Select(c => c.Key).SequenceEqual(_frequencyCores.Select(c => c.Key)) ||
                !cores.SelectMany(c => c.Threads).Select(t => (t.Id, t.FrequencyPolicy?.Id))
                    .SequenceEqual(_frequencyCores.SelectMany(c => c.Threads).Select(t => (t.Id, t.FrequencyPolicy?.Id)));
            _frequencyCores = cores;
            if (changed) RebuildFrequencyRows();
            var drivers = cores.SelectMany(c => c.Threads).Select(t => t.FrequencyPolicy?.Driver)
                .Where(s => !string.IsNullOrEmpty(s)).Distinct();
            _frequencySummary!.Text = $"{cores.Count} cores · {cores.Sum(c => c.Threads.Count)} threads · {string.Join(", ", drivers)} · updated {DateTime.Now:HH:mm:ss}";
            if (CpusPanel.IsVisible && cores.Count > 0)
                PageSubtitle.Text = $"{cores.Count} cores · {cores.Sum(c => c.Threads.Count)} threads";
            if (cores.Count == 0) _frequencySummary.Text = "CPU topology unavailable";
            foreach (var core in cores)
            {
                if (!_frequencyRows.TryGetValue(core.Key, out var cells)) continue;
                cells[1].Text = core.Threads.All(t => !t.Online) ? "offline" : MHz(core.CurrentMHz);
                cells[2].Text = MHz(core.MinimumMHz); cells[3].Text = MHz(core.MaximumMHz); cells[4].Text = MHz(core.AverageMHz);
                var caps = core.Threads.Select(t => t.FrequencyPolicy?.MaximumKHz).Distinct().ToArray();
                cells[5].Text = caps.Length == 1 ? MHz(caps[0] / 1000.0) : "mixed";
                ToolTip.SetTip(cells[0], $"Socket {core.Package?.ToString() ?? "?"}, core {core.Core?.ToString() ?? "?"}; Linux CPUs {string.Join(", ", core.Threads.Select(t => t.Id))}");
                ToolTip.SetTip(cells[1], string.Join("\n", core.Threads.Select(t => $"CPU {t.Id}: {MHz(t.CurrentMHz)} MHz ({(t.Online ? t.Source : "offline")})")));
            }
            PopulateFrequencyEditors(force: false);
            UpdateFrequencyRange(resetValue: !_frequencyAllInitialized);
            _frequencyAllInitialized = cores.Count > 0;
        }
        catch (Exception ex) { _frequencySummary!.Text = $"CPU clocks unavailable: {ex.Message}"; }
        finally { _frequencyReading = false; }
    }

    private static string MHz(double? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
    private static string CoreLabel(CpuFrequencyMonitor.CoreReading core, bool multiSocket) =>
        core.Core is { } id ? (multiSocket ? $"S{core.Package} C{id}" : $"Core {id}") : $"CPU {core.Threads[0].Id}";

    private void RebuildFrequencyRows()
    {
        var pending = _frequencyEditors.Where(p => _frequencyDirty.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value.Text);
        _frequencyRows.Clear(); _frequencyEditors.Clear(); _frequencyPopulatedText.Clear(); _frequencyTables!.Children.Clear();
        var multiSocket = _frequencyCores.Select(c => c.Package).Distinct().Count() > 1;
        var split = (_frequencyCores.Count + 1) / 2;
        foreach (var group in _frequencyCores.Chunk(Math.Max(1, split)))
        {
            var table = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            table.Children.Add(FrequencyRow(["Core", "Current", "Min", "Max", "Average", "Cap", "Set MHz"], header: true, out _));
            foreach (var core in group)
            {
                var row = FrequencyRow([CoreLabel(core, multiSocket), "—", "—", "—", "—", "—"], header: false, out var cells);
                _frequencyRows[core.Key] = cells;
                var editor = new TextBox
                {
                    Width = 78, Height = 25, MinHeight = 25, FontSize = 12, Padding = new Thickness(5, 2),
                    FontFamily = new FontFamily("monospace"), HorizontalContentAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center, IsEnabled = false,
                };
                _frequencyEditors[core.Key] = editor;
                editor.TextChanged += (_, _) =>
                {
                    // TextChanged may arrive after a programmatic update's guard has ended.
                    if (_frequencyPopulating || _frequencyIndividual!.IsChecked != true ||
                        (_frequencyPopulatedText.TryGetValue(core.Key, out var populated) && editor.Text == populated)) return;
                    _frequencyDirty.Add(core.Key);
                    UpdateFrequencyRange(resetValue: false);
                };
                if (pending.TryGetValue(core.Key, out var text))
                {
                    _frequencyPopulatedText[core.Key] = text;
                    _frequencyPopulating = true; editor.Text = text; _frequencyPopulating = false;
                }
                ((Grid)((Border)row).Child!).Children.Add(Col(editor, 6));
                table.Children.Add(row);
            }
            _frequencyTables.Children.Add(table);
        }
        SizeFrequencyTables();
        _frequencyDirty.RemoveWhere(key => !_frequencyEditors.ContainsKey(key));
        PopulateFrequencyEditors(force: false);
    }

    private Control FrequencyRow(string[] values, bool header, out TextBlock[] cells)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("60,*,54,54,58,58,86"), MinHeight = header ? 27 : 25 };
        cells = values.Select((value, index) => new TextBlock
        {
            Text = value, FontSize = 12, Foreground = header || index is 2 or 3 or 4 ? Secondary : index == 1 ? Accent : ValueText,
            FontWeight = header || index == 1 ? FontWeight.SemiBold : FontWeight.Normal,
            HorizontalAlignment = index == 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 2),
            FontFamily = header || index == 0 ? FontFamily.Default : new FontFamily("monospace"),
        }).ToArray();
        for (var i = 0; i < cells.Length; i++) grid.Children.Add(Col(cells[i], i));
        return new Border { Child = grid, BorderBrush = CardBorder, BorderThickness = new Thickness(0, 0, 0, 1),
            Background = header ? new SolidColorBrush(Color.Parse("#171B1F")) : Brushes.Transparent };
    }

    private void SizeFrequencyTables()
    {
        if (_frequencyTables is null || _frequencyTables.Bounds.Width <= 0) return;
        var width = _frequencyTables.Bounds.Width;
        var columns = width >= 950 ? 2 : 1;
        foreach (var panel in _frequencyTables.Children.OfType<StackPanel>()) panel.Width = Math.Max(460, width / columns - 12);
    }

    private void PopulateFrequencyEditors(bool force)
    {
        _frequencyPopulating = true;
        try
        {
            foreach (var core in _frequencyCores)
            {
                if (!_frequencyEditors.TryGetValue(core.Key, out var editor)) continue;
                if (!force && _frequencyIndividual!.IsChecked == true && _frequencyDirty.Contains(core.Key)) continue;
                var caps = core.Threads.Select(t => t.FrequencyPolicy?.MaximumKHz).Distinct().ToArray();
                var text = caps.Length == 1 && caps[0] is { } khz ? (khz / 1000m).ToString("0.###", CultureInfo.CurrentCulture) : "";
                _frequencyPopulatedText[core.Key] = text;
                editor.Text = text;
                editor.Watermark = "mixed";
                ToolTip.SetTip(editor, $"Maximum clock in MHz for {CoreLabel(core, false)}; applies to both sibling threads. Live cap: {string.Join(", ", caps.Select(c => c is null ? "unavailable" : $"{c / 1000m:0.###} MHz"))}");
            }
        }
        finally { _frequencyPopulating = false; }
    }

    private void UpdateFrequencyRange(bool resetValue)
    {
        var individual = _frequencyIndividual?.IsChecked == true;
        var policies = _frequencyCores.SelectMany(c => c.Threads).Select(t => t.FrequencyPolicy).OfType<CpuFrequencyMonitor.Policy>()
            .DistinctBy(p => p.Id).ToArray();
        var valid = policies.Length > 0 && policies.All(p => p.MinimumKHz.HasValue && p.MaximumKHz.HasValue && p.HardwareMaximumKHz.HasValue);
        var min = valid ? policies.Max(p => p.MinimumKHz!.Value) / 1000m : 1;
        var max = valid ? policies.Min(p => p.HardwareMaximumKHz!.Value) / 1000m : 10000;
        valid &= min <= max;
        _frequencyAllControls!.IsEnabled = valid && !individual && !_frequencyApplying;
        _frequencyIndividual!.IsEnabled = !_frequencyApplying;
        _frequencyIndividualApply!.IsEnabled = individual && !_frequencyApplying && _frequencyDirty.Count > 0;
        foreach (var core in _frequencyCores)
            if (_frequencyEditors.TryGetValue(core.Key, out var editor))
                editor.IsEnabled = individual && !_frequencyApplying && core.Threads.All(t =>
                    t.FrequencyPolicy is { MinimumKHz: not null, MaximumKHz: not null, HardwareMaximumKHz: not null });
        if (!valid) { _frequencyRange!.Text = "No common frequency range is available for all cores."; return; }
        _frequencyLimit!.Minimum = Math.Min(_frequencyLimit.Minimum, min);
        _frequencyLimit.Maximum = Math.Max(_frequencyLimit.Maximum, max);
        _frequencyLimit.Value = Math.Clamp(resetValue ? policies.Min(p => p.MaximumKHz!.Value) / 1000m : _frequencyLimit.Value ?? max, min, max);
        _frequencyLimit.Minimum = min; _frequencyLimit.Maximum = max;
        _frequencyRange!.Text = individual
            ? $"Individual caps · {_frequencyDirty.Count} edited cores · each edit applies to both sibling threads"
            : $"All-core range {min:N3}–{max:N3} MHz · {policies.Length} policies" + (policies.All(p => p.Boost == false) ? " · boost off" : "");
    }

    private async Task ApplyIndividualFrequenciesAsync()
    {
        if (_frequencyApplying || _frequencyIndividual?.IsChecked != true || _frequencyDirty.Count == 0) return;
        var limits = new Dictionary<int, long>();
        foreach (var core in _frequencyCores.Where(c => _frequencyDirty.Contains(c.Key)))
        {
            var label = CoreLabel(core, _frequencyCores.Select(c => c.Package).Distinct().Count() > 1);
            if (!decimal.TryParse(_frequencyEditors[core.Key].Text, NumberStyles.Number, CultureInfo.CurrentCulture, out var mhz) ||
                mhz <= 0 || mhz > long.MaxValue / 1000m || mhz * 1000m != decimal.Truncate(mhz * 1000m))
            {
                SetFrequencyError($"{label}: enter a frequency in MHz with up to three decimal places."); return;
            }
            var khz = (long)(mhz * 1000m);
            foreach (var thread in core.Threads)
            {
                if (thread.FrequencyPolicy is not { MinimumKHz: { } min, HardwareMaximumKHz: { } max } policy || khz < min || khz > max)
                {
                    SetFrequencyError($"{label}: the cap must be within the current kernel limits. No caps changed."); return;
                }
                if (limits.TryGetValue(policy.Id, out var prior) && prior != khz)
                {
                    SetFrequencyError("Some cores share a frequency policy. Give those cores the same cap. No caps changed."); return;
                }
                limits[policy.Id] = khz;
            }
        }
        var edited = _frequencyDirty.Count;
        _frequencyApplying = true; UpdateFrequencyRange(resetValue: false);
        _frequencyStatus!.Text = "Applying individual frequency ceilings…";
        try
        {
            var result = await Task.Run(() =>
            {
                using var client = new NvmlHelperClient();
                var ok = client.TrySetCpuFrequencyMaximums(limits);
                return (Ok: ok, Error: client.LastError);
            });
            if (result.Ok)
            {
                _frequencyDirty.Clear();
                _frequencyStatus.Text = $"Applied caps for {edited} cores. Verified by kernel readback.";
                _frequencyStatus.Foreground = new SolidColorBrush(Color.Parse("#7BC97B"));
            }
            else SetFrequencyError(result.Error is "err unknown command" ? "Update openfan-helper for individual caps." : result.Error ?? "Individual caps failed");
        }
        finally
        {
            _frequencyApplying = false; UpdateFrequencyRange(resetValue: false);
            await ReadCpuFrequenciesAsync();
        }
    }

    private void SetFrequencyError(string message)
    {
        _frequencyStatus!.Text = message; _frequencyStatus.Foreground = Warn;
    }

    private async Task ApplyFrequencyAsync(bool restore)
    {
        if (_frequencyApplying || _frequencyIndividual?.IsChecked == true) return;
        var khz = restore ? (long?)null : (long)Math.Round((_frequencyLimit!.Value ?? 0) * 1000m);
        _frequencyApplying = true; UpdateFrequencyRange(resetValue: false);
        _frequencyStatus!.Text = "Applying frequency ceiling…";
        try
        {
            var result = await Task.Run(() =>
            {
                using var client = new NvmlHelperClient();
                var ok = client.TrySetCpuFrequencyMaximum(null, khz);
                return (Ok: ok, Error: client.LastError);
            });
            _frequencyStatus.Text = result.Ok
                ? $"All cores: {(restore ? "hardware maximum restored" : $"cap set to {khz / 1000.0:N3} MHz")}. Verified by kernel readback."
                : result.Error is "err unknown command" ? "The helper needs updating for CPU frequency controls. See packaging/README.md."
                : result.Error ?? "Frequency cap failed";
            _frequencyStatus.Foreground = result.Ok ? new SolidColorBrush(Color.Parse("#7BC97B")) : Warn;
        }
        finally
        {
            _frequencyApplying = false; UpdateFrequencyRange(resetValue: false);
            await ReadCpuFrequenciesAsync();
        }
    }
}
