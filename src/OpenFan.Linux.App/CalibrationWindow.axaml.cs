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
/// Manual fan calibration: drives one board or GPU fan by
/// hand while the controller hands it over, samples (command %, RPM) pairs — manually or via an
/// auto sweep — flags avoid-zones, and validates before persisting into ControlSettings.Calibration.
/// Only real tach readings are recorded; a missing reading is never a zero-RPM sample.
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
    private readonly Action? _onDone;
    private readonly List<CalibrationSampleDto> _samples = [];
    private readonly List<RowCtl> _rows = [];
    private readonly DispatcherTimer _timer;

    private int[]? _sweep;
    private int _sweepIndex;
    private DateTime _dwellUntil;
    private bool _finalized;
    private bool _commandSucceeded;
    private bool _movingSweepSlider;

    public CalibrationWindow(FanApp app, HardwareItem item, Action? onDone = null)
    {
        if (app.Controller.PausedControlId is not null)
            throw new InvalidOperationException("Another fan is already being calibrated.");
        InitializeComponent();
        _app = app;
        _item = item;
        _onDone = onDone;
        _tachId = app.PairedTachId(item);

        var cfg = Cfg();
        // This is a transient takeover, not an edit to the saved Curve checkbox. Pausing
        // before the first manual command prevents the next curve tick undoing it.
        _app.Controller.PausedControlId = item.Id;

        FanNameText.Text = string.IsNullOrWhiteSpace(cfg.Name) ? item.Name : cfg.Name;
        SubtitleText.Text = "Use the card to adjust fan % and observe its RPM. " +
                            (item.Backend == "nvml" && item.MinPercent == 0
                                ? "At 0%, the GPU uses automatic fan-stop; positive targets use manual control."
                                : "The fan is under manual control while this window is open.");

        _samples.AddRange(cfg.Calibration);
        RebuildRows();

        DutySlider.Minimum = Math.Clamp(item.MinPercent, 0, 100);
        DutySlider.Value = DutySlider.Minimum;
        DutySlider.ValueChanged += (_, e) =>
        {
            if (_sweep is not null && !_movingSweepSlider) EndSweep();
            ApplyCommand((int)e.NewValue);
        };

        AddCurrentBtn.Click += (_, _) => AddMeasuredPoint((int)DutySlider.Value);
        AddPointBtn.Click += (_, _) => AddMeasuredPoint((int)(PointPctBox.Value ?? 0));
        AutoStepBtn.Click += (_, _) => ToggleSweep();

        CancelBtn.Click += (_, _) => Close();
        OkBtn.Click += (_, _) => OnOk();
        Closed += (_, _) => FinalizeCalibration(); // covers X, Cancel and Ok alike

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        EndSweep();
        ApplyCommand((int)DutySlider.Value); // explicitly apply even when Value was already zero
        Validate();
    }

    private void ApplyCommand(int percent)
    {
        if (_finalized) return;
        PctText.Text = $"{percent} %";
        PointPctBox.Value = percent; // manual edits remain an override until the next slider move
        _commandSucceeded = _app.ManualSetPercent(_item.Id, percent);
        StatusText.Text = _commandSucceeded ? "" : _app.WriteError(_item) ?? "Fan command failed; no sample will be recorded.";
        StatusText.IsVisible = !_commandSucceeded;
        if (!_commandSucceeded && _sweep is not null) EndSweep();
        UpdateAddCurrentLabel();
    }

    private double? MeasuredRpm()
    {
        var rpm = _app.Reading(_tachId);
        return rpm is double value && double.IsFinite(value) && value >= 0 ? value : null;
    }

    private void AddMeasuredPoint(int percent)
    {
        if (_commandSucceeded && MeasuredRpm() is double rpm)
            AddOrReplace(percent, rpm);
    }

    // Unconfigured fans have no settings row yet — create one (same as the main window does).
    private ControlSettings Cfg()
    {
        var list = _app.Settings.Controls;
        var c = list.FirstOrDefault(x => x.Id == _item.Id);
        if (c is null)
        {
            c = new ControlSettings { Id = _item.Id, Name = _item.Name };
            list.Add(c);
        }
        return c;
    }

    // ---- live readout + sweep state machine (1 s cadence) -------------------

    private void Tick()
    {
        var rpm = MeasuredRpm();
        UpdateAddCurrentLabel();

        if (_sweep is null || DateTime.Now < _dwellUntil)
            return;

        if (!_commandSucceeded || rpm is null)
        {
            EndSweep();
            StatusText.Text = "Sweep stopped: no measured RPM. No point was added for this step.";
            StatusText.IsVisible = true;
            return;
        }
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
        _sweep = CalibrationRules.SweepPercents(_item.MinPercent);
        _sweepIndex = 0;
        GoToSweepStep();
    }

    private void GoToSweepStep()
    {
        var p = _sweep![_sweepIndex];
        _movingSweepSlider = true;
        try
        {
            if ((int)DutySlider.Value == p) ApplyCommand(p);
            else DutySlider.Value = p; // slider handler drives the fan + readout
        }
        finally { _movingSweepSlider = false; }
        if (_sweep is null) return; // a rejected command stopped the sweep
        _dwellUntil = DateTime.Now.AddSeconds(_item.Backend == "nvml" ? 8 : 3.5);
        AutoStepBtn.Content = $"Sweeping… {p} % — click to stop";
    }

    private void EndSweep()
    {
        _sweep = null;
        AutoStepBtn.Content = $"Auto step {CalibrationRules.SweepPercents(_item.MinPercent)[0]}–100%";
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

            Grid.SetColumn(rpmBox, 1);
            var avoidHolder = new Panel { Children = { avoid } };
            Grid.SetColumn(avoidHolder, 2);
            Grid.SetColumn(del, 3);

            _rows.Add(row);
            Rows.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("150,210,90,60"),
                Children = { cmdBox, rpmBox, avoidHolder, del },
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
        var rpm = MeasuredRpm();
        LiveRpmText.Text = rpm is null ? "— RPM" : $"{(int)DutySlider.Value} %   ({rpm:0.#} RPM)";
        AddCurrentBtn.Content = $"Add ({(int)DutySlider.Value}%, {(rpm is null ? "—" : $"{rpm:0} RPM")})";
        AddCurrentBtn.IsEnabled = AddPointBtn.IsEnabled = _commandSucceeded && rpm is not null;
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
        _app.Controller.PausedControlId = null;
        _app.Controller.ResetApplies(); // …then resume the saved curve on the next sample
        _onDone?.Invoke();
    }
}
