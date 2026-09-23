using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Threading;
using OpenFan.Core.Config;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.App;

/// <summary>
/// Interactive °C → % editor matching the Windows reference (screenshots/OpenFan-graph.jpg):
/// white curve over orange area fill, blue dashed live-temperature marker, numeric editing of
/// the selected point, axis range + max speed, and Ok/Cancel — edits go to a working copy and
/// only commit on Ok. The apply loop picks up committed curves on its next tick.
/// </summary>
public sealed partial class GraphEditorWindow : Window
{
    private static readonly IBrush GridLine = new SolidColorBrush(Color.Parse("#262E34"));
    private static readonly IBrush AxisText = new SolidColorBrush(Color.Parse("#7D8B94"));
    private static readonly IBrush AreaFill = new SolidColorBrush(Color.FromArgb(0x59, 0xF0, 0xA0, 0x3C));
    private static readonly IBrush DotBrush = new SolidColorBrush(Color.Parse("#F0A03C"));
    private static readonly IBrush MarkerBrush = new SolidColorBrush(Color.Parse("#4FC3F7"));

    private const double Pad = 36;

    private readonly FanApp _app;
    private readonly CurveSettings _edit; // working copy — committed via onOk only
    private readonly Action<CurveSettings> _onOk;
    private readonly DispatcherTimer _tempTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly List<(ComboBoxItem Item, string Id, string BaseLabel)> _sensorItems = [];
    private int _dragging = -1;
    private int _selected = -1;
    private bool _suppress;

    public GraphEditorWindow(FanApp app, CurveSettings original, Action<CurveSettings> onOk)
    {
        InitializeComponent();
        _app = app;
        _edit = Clone(original);
        _onOk = onOk;

        Title = TitleText.Text = $"Edit graph — {original.Name}";

        _suppress = true;
        foreach (var t in app.Inventory
                     .Where(i => i.Kind == HardwareKind.Temperature)
                     .OrderBy(i => i.Group).ThenBy(i => i.Name))
        {
            var item = new ComboBoxItem { Tag = t.Id };
            SensorBox.Items.Add(item);
            _sensorItems.Add((item, t.Id, $"{app.SensorLabel(t)}  ·  {t.Group}"));
        }
        SensorBox.SelectedItem = SensorBox.Items
            .OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == _edit.SensorId);
        HystCBox.Value = (decimal)_edit.HysteresisC;
        HystSBox.Value = (decimal)_edit.HysteresisS;
        MaxSpeedBox.Value = (decimal)_edit.MaxSpeedPercent;
        MinTempBox.Value = (decimal)_edit.MinTempC;
        MaxTempBox.Value = (decimal)_edit.MaxTempC;
        NameBox.Text = _edit.Name;
        _suppress = false;

        NameBox.TextChanged += (_, _) =>
        {
            if (!_suppress && !string.IsNullOrWhiteSpace(NameBox.Text))
                _edit.Name = NameBox.Text.Trim();
        };

        SensorBox.SelectionChanged += (_, _) =>
        {
            if (_suppress) return;
            _edit.SensorId = (SensorBox.SelectedItem as ComboBoxItem)?.Tag as string;
            RefreshTemp();
        };
        HystCBox.ValueChanged += (_, e) =>
        {
            if (_suppress || e.NewValue is null) return;
            _edit.HysteresisC = (double)e.NewValue.Value;
        };
        HystSBox.ValueChanged += (_, e) =>
        {
            if (_suppress || e.NewValue is null) return;
            _edit.HysteresisS = (double)e.NewValue.Value;
        };
        MaxSpeedBox.ValueChanged += (_, e) =>
        {
            if (_suppress || e.NewValue is null) return;
            _edit.MaxSpeedPercent = (int)Math.Round((double)e.NewValue.Value);
            Render();
        };
        MinTempBox.ValueChanged += (_, e) =>
        {
            if (_suppress || e.NewValue is null) return;
            var v = (double)e.NewValue.Value;
            if (v < TMax - 5)
            {
                var (oldMin, oldMax) = (TMin, TMax);
                _edit.MinTempC = v;
                RemapPointsIntoRange(oldMin, oldMax);
                Render();
            }
        };
        MaxTempBox.ValueChanged += (_, e) =>
        {
            if (_suppress || e.NewValue is null) return;
            var v = (double)e.NewValue.Value;
            if (v > TMin + 5)
            {
                var (oldMin, oldMax) = (TMin, TMax);
                _edit.MaxTempC = v;
                RemapPointsIntoRange(oldMin, oldMax);
                Render();
            }
        };

        SelTempBox.ValueChanged += (_, e) =>
        {
            if (_suppress || _selected < 0 || e.NewValue is null) return;
            var pt = _edit.Points[_selected];
            _edit.Points[_selected] = pt with { TempC = (double)e.NewValue.Value };
            Render();
        };
        SelPctBox.ValueChanged += (_, e) =>
        {
            if (_suppress || _selected < 0 || e.NewValue is null) return;
            var pt = _edit.Points[_selected];
            _edit.Points[_selected] = pt with { Percent = (int)Math.Round((double)e.NewValue.Value) };
            Render();
        };

        AddPointBtn.Click += (_, _) => AddPoint();
        RemovePointBtn.Click += (_, _) => RemoveSelected();

        Plot.PointerPressed += OnPointerPressed;
        Plot.PointerMoved += OnPointerMoved;
        Plot.PointerReleased += OnPointerReleased;
        Plot.SizeChanged += (_, _) => Render();

        OkBtn.Click += (_, _) =>
        {
            _edit.Points = [.. _edit.Points.OrderBy(p => p.TempC)]; // persist tidy order
            _onOk(_edit);
            Close();
        };
        CancelBtn.Click += (_, _) => Close();

        _tempTimer.Tick += (_, _) => RefreshTemp();
        Opened += (_, _) =>
        {
            _tempTimer.Start();
            RefreshTemp();
        };
        Closed += (_, _) => _tempTimer.Stop();
    }

    private static CurveSettings Clone(CurveSettings c) =>
        JsonSerializer.Deserialize<CurveSettings>(JsonSerializer.Serialize(c))!;

    /// <summary>
    /// When Min/Max temp shrinks the axis below existing points, remap every point proportionally from the old
    /// axis onto the new one (shape preserved, nothing stranded off-screen). No-op while all points still fit.
    /// </summary>
    private void RemapPointsIntoRange(double oldMin, double oldMax)
    {
        if (_edit.Points.Count == 0) return;
        var (newMin, newMax) = (TMin, TMax);
        if (_edit.Points.All(p => p.TempC >= newMin && p.TempC <= newMax)) return;

        var oldSpan = Math.Max(oldMax - oldMin, 1e-9);
        var newSpan = Math.Max(newMax - newMin, 1e-9);
        for (var i = 0; i < _edit.Points.Count; i++)
        {
            var p = _edit.Points[i];
            var mapped = newMin + (p.TempC - oldMin) / oldSpan * newSpan;
            _edit.Points[i] = p with { TempC = Math.Clamp(mapped, newMin, newMax) };
        }

        if (_selected >= 0 && _selected < _edit.Points.Count)
        {
            _suppress = true;
            SelTempBox.Value = (decimal)_edit.Points[_selected].TempC;
            SelPctBox.Value = (decimal)_edit.Points[_selected].Percent;
            _suppress = false;
        }
    }

    // ---- plot geometry -----------------------------------------------------

    private double TMin => _edit.MinTempC;
    private double TMax => Math.Max(_edit.MinTempC + 10, _edit.MaxTempC);

    private double W => Math.Max(Plot.Bounds.Width, 100);
    private double H => Math.Max(Plot.Bounds.Height, 100);

    private double X(double tempC) => Pad + (tempC - TMin) / (TMax - TMin) * (W - 2 * Pad);
    private double Y(double percent) => H - Pad - percent / 100.0 * (H - 2 * Pad);
    private double InvX(double px) => TMin + (px - Pad) / (W - 2 * Pad) * (TMax - TMin);
    private double InvY(double py) => (H - Pad - py) / (H - 2 * Pad) * 100.0;

    // ---- rendering ---------------------------------------------------------

    private void Render()
    {
        Plot.Children.Clear();

        for (var p = 0.0; p <= 100; p += 25)
        {
            AddLine(Pad, Y(p), W - Pad / 2, Y(p), GridLine);
            AddLabel($"{p:0}", 8, Y(p) - 7, AxisText);
        }
        for (var t = Math.Ceiling(TMin / 10) * 10; t <= TMax + 0.01; t += 10)
        {
            AddLine(X(t), Pad / 2, X(t), H - Pad, GridLine);
            AddLabel($"{t:0}°", X(t) - 9, H - Pad + 8, AxisText);
        }

        // Live temperature marker (blue dashed vertical).
        var temp = CurrentTemp();
        if (temp is double t0 && t0 >= TMin && t0 <= TMax)
        {
            Plot.Children.Add(new Line
            {
                StartPoint = new Avalonia.Point(X(t0), Pad / 2),
                EndPoint = new Avalonia.Point(X(t0), H - Pad),
                Stroke = MarkerBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = [3, 3],
            });
        }

        // Max-speed cap line.
        if (_edit.MaxSpeedPercent is > 0 and < 100)
        {
            Plot.Children.Add(new Line
            {
                StartPoint = new Avalonia.Point(Pad, Y(_edit.MaxSpeedPercent)),
                EndPoint = new Avalonia.Point(W - Pad / 2, Y(_edit.MaxSpeedPercent)),
                Stroke = DotBrush,
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 3],
            });
        }

        var ordered = _edit.Points.OrderBy(p => p.TempC).ToList();

        // Area fill under the curve (reference look).
        if (ordered.Count >= 2)
        {
            var areaPoints = new Avalonia.Collections.AvaloniaList<Avalonia.Point>(
                ordered.Select(p => new Avalonia.Point(X(p.TempC), Y(p.Percent))));
            areaPoints.Add(new Avalonia.Point(X(ordered[^1].TempC), Y(0)));
            areaPoints.Add(new Avalonia.Point(X(ordered[0].TempC), Y(0)));
            Plot.Children.Add(new Polygon { Fill = AreaFill, Points = areaPoints });

            Plot.Children.Add(new Polyline
            {
                Stroke = Brushes.White,
                StrokeThickness = 2.5,
                Points = new Avalonia.Collections.AvaloniaList<Avalonia.Point>(
                    ordered.Select(p => new Avalonia.Point(X(p.TempC), Y(p.Percent)))),
            });
        }

        for (var i = 0; i < _edit.Points.Count; i++)
        {
            var pt = _edit.Points[i];
            var isSel = i == _selected;
            var size = isSel ? 15.0 : 11.0;
            Plot.Children.Add(new Ellipse
            {
                Width = size,
                Height = size,
                Fill = DotBrush,
                Stroke = isSel ? Brushes.White : null,
                StrokeThickness = isSel ? 2 : 0,
            });
            Canvas.SetLeft(Plot.Children[^1], X(pt.TempC) - size / 2);
            Canvas.SetTop(Plot.Children[^1], Y(pt.Percent) - size / 2);
        }
    }

    private void AddLine(double x1, double y1, double x2, double y2, IBrush brush)
        => Plot.Children.Add(new Line
        {
            StartPoint = new Avalonia.Point(x1, y1),
            EndPoint = new Avalonia.Point(x2, y2),
            Stroke = brush,
            StrokeThickness = 1,
        });

    private void AddLabel(string text, double x, double y, IBrush brush)
    {
        var tb = new TextBlock { Text = text, FontSize = 10, Foreground = brush };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        Plot.Children.Add(tb);
    }

    // ---- interaction -------------------------------------------------------

    private double? CurrentTemp() => _edit.SensorId is null ? null : _app.Reading(_edit.SensorId);

    private void RefreshTemp()
    {
        var t = CurrentTemp();
        TempReadout.Text = t is null ? "no sensor selected" : $"{t:0.0} °C";

        // Live readings inside the sensor dropdown, e.g. "Composite · nvme — 41 °C".
        foreach (var (item, id, baseLabel) in _sensorItems)
        {
            var r = _app.Reading(id);
            item.Content = r is null ? baseLabel : $"{baseLabel}   —   {r:0.#} °C";
        }

        Render();
    }

    private void SelectPoint(int index)
    {
        _selected = index;
        _suppress = true;
        if (index >= 0 && index < _edit.Points.Count)
        {
            SelTempBox.Value = (decimal)_edit.Points[index].TempC;
            SelPctBox.Value = (decimal)_edit.Points[index].Percent;
        }
        else
        {
            SelTempBox.Value = null;
            SelPctBox.Value = null;
        }
        _suppress = false;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(Plot);

        var hit = -1;
        for (var i = 0; i < _edit.Points.Count; i++)
        {
            var dx = X(_edit.Points[i].TempC) - pos.X;
            var dy = Y(_edit.Points[i].Percent) - pos.Y;
            if (dx * dx + dy * dy <= 13 * 13)
            {
                hit = i;
                break;
            }
        }

        if (hit >= 0)
        {
            _dragging = hit;
            SelectPoint(hit);
        }
        else
        {
            var temp = Math.Round(Math.Clamp(InvX(pos.X), TMin, TMax) * 2) / 2;
            var pct = (int)Math.Round(Math.Clamp(InvY(pos.Y), 0, 100));
            _edit.Points.Add(new CurvePointDto(temp, pct));
            _dragging = _edit.Points.Count - 1;
            SelectPoint(_dragging);
        }

        e.Pointer.Capture(Plot);
        Render();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging < 0 || _dragging >= _edit.Points.Count)
            return;

        var pos = e.GetPosition(Plot);
        var temp = Math.Round(Math.Clamp(InvX(pos.X), TMin, TMax) * 2) / 2;
        var pct = (int)Math.Round(Math.Clamp(InvY(pos.Y), 0, 100));
        _edit.Points[_dragging] = new CurvePointDto(temp, pct);

        _suppress = true;
        SelTempBox.Value = (decimal)temp;
        SelPctBox.Value = (decimal)pct;
        _suppress = false;
        Render();
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging < 0)
            return;
        _dragging = -1;
        e.Pointer.Capture(null);
    }

    private void AddPoint()
    {
        // Insert at the midpoint of the widest temperature gap — where a point helps most.
        var ordered = _edit.Points.OrderBy(p => p.TempC).ToList();
        CurvePointDto added;
        if (ordered.Count == 0)
        {
            added = new CurvePointDto((TMin + TMax) / 2, 50);
        }
        else if (ordered.Count == 1)
        {
            added = new CurvePointDto(Math.Min(TMax, ordered[0].TempC + 10), ordered[0].Percent);
        }
        else
        {
            var bestGap = 0.0;
            var at = 0;
            for (var i = 1; i < ordered.Count; i++)
            {
                var gap = ordered[i].TempC - ordered[i - 1].TempC;
                if (gap > bestGap)
                {
                    bestGap = gap;
                    at = i;
                }
            }
            added = new CurvePointDto(
                Math.Round((ordered[at - 1].TempC + ordered[at].TempC) / 2 * 2) / 2,
                (ordered[at - 1].Percent + ordered[at].Percent) / 2);
        }

        _edit.Points.Add(added);
        SelectPoint(_edit.Points.Count - 1);
        Render();
    }

    private void RemoveSelected()
    {
        if (_selected < 0 || _edit.Points.Count <= 2)
            return; // graphs need at least two points
        _edit.Points.RemoveAt(_selected);
        SelectPoint(-1);
        Render();
    }
}
