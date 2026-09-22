using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OpenFan.Core.Config;
using OpenFan.Core.Curves;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.App;

/// <summary>
/// Manual fan calibration (reference: Windows "Manual Fan Calibration"): drives one board fan by
/// hand while the controller hands it over, samples (command %, RPM) pairs — manually or via an
/// auto sweep — flags avoid-zones, and validates before persisting into ControlSettings.Calibration.
/// GPU fans are excluded upstream: NVML exposes no tach on this driver, so % is taken at face value.
/// </summary>
public sealed partial class CalibrationWindow : Window
{
    private static readonly IBrush Good = new SolidColorBrush(Color.Parse("#7BC97B"));
    private static readonly IBrush Bad = new SolidColorBrush(Color.Parse("#EF6B6B"));

    private sealed class RowCtl
    {
        public TextBox Cmd = null!;
        public TextBox Rpm = null!;
        public CheckBox Avoid = null!;
        public int Key; // percent this row's sample had when the row was built
    }

    private readonly FanApp _app;
    private readonly HardwareItem _item;
    private readonly string _tachId;
    private readonly bool _wasEnabled;
    private readonly Action? _onDone;
    private readonly List<CalibrationSampleDto> _samples = [];
    private readonly List<RowCtl> _rows = [];
    private readonly DispatcherTimer _timer;

    private int[]? _sweep;
    private int _sweepIndex;
    private DateTime _dwellUntil;
    private bool _finalized;

    public CalibrationWindow(FanApp app, HardwareItem item, Action? onDone = null)
    {
        InitializeComponent();
        _app = app;
        _item = item;
        _onDone = onDone;
        _tachId = app.PairedTachId(item);

        var cfg = Cfg();
        _wasEnabled = cfg.Enabled;
        cfg.Enabled = false; // controller restores the control and skips it while we drive by hand
        _app.Save();

        FanNameText.Text = string.IsNullOrWhiteSpace(cfg.Name) ? item.Name : cfg.Name;
        SubtitleText.Text = "Use the card to adjust fan % and observe its RPM. " +
                            "The fan is under manual control while this window is open.";

        _samples.AddRange(cfg.Calibration);
        RebuildRows();

        DutySlider.ValueChanged += (_, e) =>
        {
            var p = (int)e.NewValue;
            PctText.Text = $"{p} %";
            if (!_finalized)
                _app.ManualSetPercent(_item.Id, p);
            UpdateAddCurrentLabel();
        };

        AddCurrentBtn.Click += (_, _) => AddOrReplace((int)DutySlider.Value, _app.Reading(_tachId) ?? 0);
        AddPointBtn.Click += (_, _) => AddOrReplace((int)(PointPctBox.Value ?? 0), _app.Reading(_tachId) ?? 0);
        AutoStepBtn.Click += (_, _) => ToggleSweep();

        CancelBtn.Click += (_, _) => Close();
        OkBtn.Click += (_, _) => OnOk();
        Closed += (_, _) => FinalizeCalibration(); // covers X, Cancel and Ok alike

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        DutySlider.Value = 0; // deterministic start: fan commanded to a known duty immediately
        Validate();
    }

    private ControlSettings Cfg() =>
        _app.Settings.Controls.First(c => c.Id == _item.Id);

    // ---- live readout + sweep state machine (1 s cadence) -------------------

    private void Tick()
    {
        var rpm = _app.Reading(_tachId);
        var p = (int)DutySlider.Value;
        LiveRpmText.Text = rpm is null ? "— RPM" : $"{p} %   ({rpm:0.#} RPM)";
        UpdateAddCurrentLabel();

        if (_sweep is null || DateTime.Now < _dwellUntil)
            return;

        if (rpm is not null)
            AddOrReplace(_sweep[_sweepIndex], rpm.Value); // dwell elapsed → record this step

        _sweepIndex++;
        if (_sweepIndex < _sweep.Length)
            GoToSweepStep();
        else
            EndSweep();
    }

    private void ToggleSweep()
    {
        if (_sweep is not null)
        {
            EndSweep(); // clicking again stops the sweep, keeping everything sampled so far
            return;
        }
        _sweep = [20, 30, 40, 50, 60, 70, 80, 90, 100];
        _sweepIndex = 0;
        GoToSweepStep();
    }

    private void GoToSweepStep()
    {
        var p = _sweep![_sweepIndex];
        DutySlider.Value = p; // slider handler drives the fan + readout
        _dwellUntil = DateTime.Now.AddSeconds(3.5);
        AutoStepBtn.Content = $"Sweeping… {p} % — click to stop";
    }

    private void EndSweep()
    {
        _sweep = null;
        AutoStepBtn.Content = "Auto step 20–100%";
    }

    // ---- sample table --------------------------------------------------------

    private void AddOrReplace(int percent, double rpm)
    {
        percent = Math.Clamp(percent, 0, 100);
        var i = _samples.FindIndex(s => s.Percent == percent);
        var keepAvoid = i >= 0 && _samples[i].Avoid;
        var sample = new CalibrationSampleDto(percent, Math.Round(rpm, 1), keepAvoid);
        if (i >= 0)
            _samples[i] = sample;
        else
            _samples.Add(sample);
        RebuildRows();
        Validate();
    }

    private void RebuildRows()
    {
        Rows.Children.Clear();
        _rows.Clear();

        foreach (var s in _samples.OrderBy(s => s.Percent))
        {
            var row = new RowCtl { Key = s.Percent };
            var cmdBox = new TextBox { Text = $"{s.Percent}", Width = 140 };
            var rpmBox = new TextBox { Text = $"{s.Rpm:0.#}", Width = 200 };
            var avoid = new CheckBox { HorizontalAlignment = HorizontalAlignment.Center, IsChecked = s.Avoid };
            row.Cmd = cmdBox;
            row.Rpm = rpmBox;
            row.Avoid = avoid;

            void Commit() => CommitRow(row);
            cmdBox.LostFocus += (_, _) => Commit();
            rpmBox.LostFocus += (_, _) => Commit();
            cmdBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    TopLevel.GetTopLevel(cmdBox)?.FocusManager?.ClearFocus();
            };
            rpmBox.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                    TopLevel.GetTopLevel(rpmBox)?.FocusManager?.ClearFocus();
            };

            avoid.Checked += (_, _) => Mutate(row.Key, x => x with { Avoid = true });
            avoid.Unchecked += (_, _) => Mutate(row.Key, x => x with { Avoid = false });

            var del = new Button
            {
                Content = "X",
                Width = 44,
                Background = null,
                BorderBrush = new SolidColorBrush(Color.Parse("#2C363D")),
            };
            del.Click += (_, _) =>
            {
                _samples.RemoveAll(x => x.Percent == row.Key);
                RebuildRows();
                Validate();
            };

            _rows.Add(row);
            Rows.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("150,210,90,60"),
                Children =
                {
                    cmdBox,
                    rpmBox,
                    new Panel { Children = { avoid } },
                    del,
                },
            });
        }
    }

    private void CommitRow(RowCtl row)
    {
        // Parse one edited row; keep the old sample while input is mid-edit.
        if (!int.TryParse(row.Cmd.Text, out var percent))
            return;
        double.TryParse(row.Rpm.Text, out var rpm);
        percent = Math.Clamp(percent, 0, 100);

        var next = new CalibrationSampleDto(percent, Math.Round(rpm, 1), row.Avoid.IsChecked == true);
        var i = _samples.FindIndex(x => x.Percent == row.Key);
        if (i >= 0)
            _samples[i] = next;
        else
            _samples.Add(next);
        row.Key = percent; // keep tracking after a command-% edit
        Validate();
    }

    private void Mutate(int percent, Func<CalibrationSampleDto, CalibrationSampleDto> f)
    {
        var i = _samples.FindIndex(s => s.Percent == percent);
        if (i >= 0)
            _samples[i] = f(_samples[i]);
        Validate();
    }

    // ---- validation + commit -------------------------------------------------

    private void Validate()
    {
        SetCheck(CheckTwoPoints, CalibrationRules.HasAtLeastTwoPoints(_samples), "At least 2 points required");
        SetCheck(CheckAscending, CalibrationRules.RpmIsAscending(_samples), "Ascending RPM values");
        SetCheck(CheckAvoidContiguous, CalibrationRules.AvoidPointsAreContiguous(_samples), "Contiguous avoid points");
        OkBtn.IsEnabled = CalibrationRules.IsValid(_samples);
    }

    private static void SetCheck(TextBlock block, bool pass, string label)
    {
        block.Text = (pass ? "✓  " : "✗  ") + label;
        block.Foreground = pass ? Good : Bad;
    }

    private void UpdateAddCurrentLabel()
    {
        var rpm = _app.Reading(_tachId);
        AddCurrentBtn.Content = $"Add ({(int)DutySlider.Value}%, {(rpm is null ? "—" : $"{rpm:0} RPM")})";
    }

    private void OnOk()
    {
        foreach (var r in _rows)
            CommitRow(r);
        if (!CalibrationRules.IsValid(_samples))
            return;

        Cfg().Calibration = [.. _samples.OrderBy(s => s.Percent)];
        _app.Save();
        FinalizeCalibration();
        Close();
    }

    private void FinalizeCalibration()
    {
        if (_finalized)
            return;
        _finalized = true;

        _timer.Stop();
        _sweep = null;
        _app.ManualRestore(_item.Id); // back to the pre-takeover auto mode…

        var cfg = Cfg();
        cfg.Enabled = _wasEnabled; // …and the controller resumes if the fan was armed
        _app.Save();
        _onDone?.Invoke();
    }
}
