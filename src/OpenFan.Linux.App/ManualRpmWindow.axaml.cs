using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using OpenFan.Core.Config;

namespace OpenFan.Linux.App;

/// <summary>
/// Manual Command% -> RPM table for fans that cannot report speed (NVML GPUs on driver 595).
/// The saved points drive a virtual, interpolated RPM readout on the card.
/// </summary>
public sealed partial class ManualRpmWindow : Window
{
    private readonly Action<List<CalibrationSampleDto>> _onOk;
    private readonly List<(TextBox Pct, TextBox Rpm)> _rows = [];

    public ManualRpmWindow(string title, IEnumerable<CalibrationSampleDto> initial,
                           Action<List<CalibrationSampleDto>> onOk)
    {
        InitializeComponent();
        TitleText.Text = title;
        _onOk = onOk;

        foreach (var s in initial.OrderBy(s => s.Percent))
            AddRow(s.Percent.ToString(CultureInfo.InvariantCulture), s.Rpm.ToString("0", CultureInfo.InvariantCulture));
        if (_rows.Count == 0)
        {
            AddRow("50", "");
            AddRow("100", "");
        }

        AddRowBtn.Click += (_, _) => AddRow("", "");
        CancelBtn.Click += (_, _) => Close();
        OkBtn.Click += OnOk;
    }

    private void AddRow(string pct, string rpm)
    {
        var row = new Grid { ColumnDefinitions = new("120,120,*,Auto") };
        var pctBox = new TextBox { Text = pct, Width = 104 };
        var rpmBox = new TextBox { Text = rpm, Width = 104 };
        var del = new Button
        {
            Content = "×",
            Background = Brushes.Transparent, // whole padding box clickable, not just glyph ink
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(Color.Parse("#8FA0AA")),
            Padding = new Thickness(6, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        del.Click += (_, _) =>
        {
            _rows.RemoveAll(r => r.Pct == pctBox);
            Rows.Children.Remove(row);
        };
        Grid.SetColumn(pctBox, 0);
        Grid.SetColumn(rpmBox, 1);
        Grid.SetColumn(del, 3);
        row.Children.Add(pctBox);
        row.Children.Add(rpmBox);
        row.Children.Add(del);
        Rows.Children.Add(row);
        _rows.Add((pctBox, rpmBox));
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        var parsed = new List<CalibrationSampleDto>();
        foreach (var (pctBox, rpmBox) in _rows)
        {
            if (string.IsNullOrWhiteSpace(pctBox.Text) && string.IsNullOrWhiteSpace(rpmBox.Text))
                continue; // blank rows are ignored
            if (!int.TryParse(pctBox.Text.Trim(), out var pct) || pct is < 0 or > 100
                || !double.TryParse(rpmBox.Text.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var rpm) || rpm < 0)
            {
                Fail("Every row needs a command % (0–100) and a speed in RPM.");
                return;
            }
            parsed.Add(new CalibrationSampleDto(pct, rpm));
        }

        if (parsed.Count < 2)
        {
            Fail("At least 2 points are required.");
            return;
        }
        var byPct = parsed.OrderBy(p => p.Percent).ToList();
        for (var i = 1; i < byPct.Count; i++)
        {
            if (byPct[i].Percent == byPct[i - 1].Percent)
            {
                Fail("Command % values must be unique.");
                return;
            }
            if (byPct[i].Rpm <= byPct[i - 1].Rpm)
            {
                Fail("RPM values must rise as command % rises.");
                return;
            }
        }

        _onOk(byPct);
        Close();
    }

    private void Fail(string message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }
}
