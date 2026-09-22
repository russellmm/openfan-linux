using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using OpenFan.Core.Config;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.App;

/// <summary>
/// Interactive °C → % editor (spec §6): click empty plot to add a point, drag points,
/// pick the sensor, set hysteresis / max speed. Edits save live; the apply loop picks
/// them up on its next tick — same GraphCurve math as Windows OpenFan.
/// </summary>
public sealed partial class GraphEditorWindow : Window
{
    private static readonly IBrush GridLine = new SolidColorBrush(Color.Parse("#2A2A2A"));
    private static readonly IBrush AxisText = new SolidColorBrush(Color.Parse("#777777"));
    private static readonly IBrush Accent = new SolidColorBrush(Color.Parse("#E24B4B"));
    private static readonly IBrush CapLine = new SolidColorBrush(Color.Parse("#E5A64B"));

    private const double Pad = 30;

    private readonly FanApp _app;
    private readonly CurveSettings _curve;
    private int _dragging = -1;
    private bool _suppress;

    public GraphEditorWindow(FanApp app, CurveSettings curve)
    {
        InitializeComponent();
        _app = app;
        _curve = curve;

        _suppress = true;
        foreach (var t in app.Inventory
                     .Where(i => i.Kind == HardwareKind.Temperature)
                     .OrderBy(i => i.Group).ThenBy(i => i.Name))
        {
            SensorBox.Items.Add(new ComboBoxItem { Content = $"{t.Name}  ·  {t.Group}", Tag = t.Id });
        }
        SensorBox.SelectedItem = SensorBox.Items
            .OfType<ComboBoxItem>().FirstOrDefault(i => (string?)i.Tag == curve.SensorId);
        HystCBox.Value = (decimal)curve.HysteresisC;
        HystSBox.Value = (decimal)curve.HysteresisS;
        MaxSpeedBox.Value = (decimal)curve.MaxSpeedPercent;
        _suppress = false;

        SensorBox.SelectionChanged += (_, _) =>
        {
            if (_suppress) return;
            _curve.SensorId = (SensorBox.SelectedItem as ComboBoxItem)?.Tag as string;
            Commit();
        };
        HystCBox.ValueChanged += (_, e) =>
        {
            if (_suppress) return;
            _curve.HysteresisC = (double)e.NewValue;
            Commit();
        };
        HystSBox.ValueChanged += (_, e) =>
        {
            if (_suppress) return;
            _curve.HysteresisS = (double)e.NewValue;
            Commit();
        };
        MaxSpeedBox.ValueChanged += (_, e) =>
        {
            if (_suppress) return;
            _curve.MaxSpeedPercent = (int)Math.Round((double)e.NewValue);
            Render();
            Commit();
        };

        PointList.SelectionChanged += (_, _) => Render();
        RemovePointBtn.Click += (_, _) => RemoveSelected();
        ResetPointsBtn.Click += (_, _) =>
        {
            _curve.Points = [new CurvePointDto(40, 20), new CurvePointDto(85, 90)];
            RefreshList();
            Render();
            Commit();
        };

        Plot.PointerPressed += OnPointerPressed;
        Plot.PointerMoved += OnPointerMoved;
        Plot.PointerReleased += OnPointerReleased;
        Plot.SizeChanged += (_, _) => Render();

        Opened += (_, _) =>
        {
            RefreshList();
            Render();
        };
    }

    // ---- plot geometry -----------------------------------------------------

    private double TMin() => _curve.Points.Count == 0
        ? 30
        : Math.Min(30, Math.Floor(_curve.Points.Min(p => p.TempC) / 5) * 5);

    private double TMax() => _curve.Points.Count == 0
        ? 100
        : Math.Max(100, Math.Ceiling(_curve.Points.Max(p => p.TempC) / 5) * 5);

    private double W => Math.Max(Plot.Bounds.Width, 100);
    private double H => Math.Max(Plot.Bounds.Height, 100);

    private double X(double tempC) => Pad + (tempC - TMin()) / (TMax() - TMin()) * (W - 2 * Pad);
    private double Y(double percent) => H - Pad - percent / 100.0 * (H - 2 * Pad);
    private double InvX(double px) => TMin() + (px - Pad) / (W - 2 * Pad) * (TMax() - TMin());
    private double InvY(double py) => (H - Pad - py) / (H - 2 * Pad) * 100.0;

    // ---- rendering ---------------------------------------------------------

    private void Render()
    {
        Plot.Children.Clear();

        for (var p = 0.0; p <= 100; p += 25)
        {
            AddLine(Pad, Y(p), W - Pad, Y(p), GridLine);
            AddLabel($"{p:0}%", 2, Y(p) - 8, AxisText);
        }
        for (var t = TMin(); t <= TMax() + 0.01; t += 10)
        {
            AddLine(X(t), Pad / 2, X(t), H - Pad, GridLine);
            AddLabel($"{t:0}°", X(t) - 8, H - Pad + 6, AxisText);
        }

        if (_curve.MaxSpeedPercent is > 0 and < 100)
        {
            var cap = new Line
            {
                StartPoint = new Avalonia.Point(Pad, Y(_curve.MaxSpeedPercent)),
                EndPoint = new Avalonia.Point(W - Pad, Y(_curve.MaxSpeedPercent)),
                Stroke = CapLine,
                StrokeThickness = 1.5,
                StrokeDashArray = [4, 3],
            };
            Plot.Children.Add(cap);
        }

        var ordered = _curve.Points.OrderBy(p => p.TempC).ToList();
        if (ordered.Count >= 2)
        {
            var poly = new Polyline
            {
                Stroke = Accent,
                StrokeThickness = 2,
                Points = new Avalonia.Collections.AvaloniaList<Avalonia.Point>(
                    ordered.Select(p => new Avalonia.Point(X(p.TempC), Y(p.Percent)))),
            };
            Plot.Children.Add(poly);
        }

        for (var i = 0; i < _curve.Points.Count; i++)
        {
            var pt = _curve.Points[i];
            var isSelected = PointList.SelectedIndex == i;
            var dot = new Ellipse
            {
                Width = isSelected ? 14 : 10,
                Height = isSelected ? 14 : 10,
                Fill = Accent,
                Stroke = isSelected ? Brushes.White : null,
                StrokeThickness = isSelected ? 1.5 : 0,
            };
            Canvas.SetLeft(dot, X(pt.TempC) - (isSelected ? 7 : 5));
            Canvas.SetTop(dot, Y(pt.Percent) - (isSelected ? 7 : 5));
            Plot.Children.Add(dot);
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

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(Plot);

        var hit = -1;
        for (var i = 0; i < _curve.Points.Count; i++)
        {
            var dx = X(_curve.Points[i].TempC) - pos.X;
            var dy = Y(_curve.Points[i].Percent) - pos.Y;
            if (dx * dx + dy * dy <= 12 * 12)
            {
                hit = i;
                break;
            }
        }

        if (hit >= 0)
        {
            _dragging = hit;
            _suppress = true;
            PointList.SelectedIndex = hit;
            _suppress = false;
        }
        else
        {
            var temp = Math.Round(Math.Clamp(InvX(pos.X), TMin(), TMax()) * 2) / 2;
            var pct = (int)Math.Round(Math.Clamp(InvY(pos.Y), 0, 100));
            _curve.Points.Add(new CurvePointDto(temp, pct));
            SortPoints();
            _dragging = _curve.Points.FindIndex(p => p.TempC == temp && p.Percent == pct);
            RefreshList();
        }

        e.Pointer.Capture(Plot);
        Render();
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragging < 0 || _dragging >= _curve.Points.Count)
            return;

        var pos = e.GetPosition(Plot);
        var temp = Math.Round(Math.Clamp(InvX(pos.X), TMin(), TMax()) * 2) / 2;
        var pct = (int)Math.Round(Math.Clamp(InvY(pos.Y), 0, 100));
        _curve.Points[_dragging] = new CurvePointDto(temp, pct);
        Render(); // live drag; persist on release
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragging < 0)
            return;
        _dragging = -1;
        e.Pointer.Capture(null);
        SortPoints();
        RefreshList();
        Render();
        Commit();
    }

    private void RemoveSelected()
    {
        var i = PointList.SelectedIndex;
        if (i < 0 || _curve.Points.Count <= 2)
            return; // graphs need at least two points
        _curve.Points.RemoveAt(i);
        RefreshList();
        Render();
        Commit();
    }

    private void SortPoints()
        => _curve.Points = [.. _curve.Points.OrderBy(p => p.TempC)];

    private void RefreshList()
    {
        var keep = PointList.SelectedIndex;
        _suppress = true;
        PointList.ItemsSource = _curve.Points
            .Select(p => $"{p.TempC:0.#} °C  →  {p.Percent:0} %")
            .ToList();
        PointList.SelectedIndex = Math.Clamp(keep, -1, _curve.Points.Count - 1);
        _suppress = false;
    }

    private void Commit() => _app.Save();
}
