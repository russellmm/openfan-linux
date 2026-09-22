using Avalonia.Controls;
using OpenFan.Core.Config;

namespace OpenFan.Linux.App;

/// <summary>
/// Curve library (reference model): curves are first-class named objects — graphs bind a
/// sensor, flats hold a percent — and fans reference them by id on the Home page. One curve
/// can drive many fans; deleting is blocked while any enabled fan still references it.
/// </summary>
public sealed partial class CurveLibraryWindow : Window
{
    private readonly FanApp _app;
    private readonly Action _onChanged;

    public CurveLibraryWindow(FanApp app, Action onChanged)
    {
        InitializeComponent();
        _app = app;
        _onChanged = onChanged;

        CurveList.SelectionChanged += (_, _) => SyncSelection();
        NameBox.TextChanged += (_, _) => RenameSelected();

        NewGraphBtn.Click += (_, _) => CreateCurve("graph");
        NewFlatBtn.Click += (_, _) => CreateCurve("flat");
        EditBtn.Click += (_, _) =>
        {
            if (Selected is CurveSettings curve)
                OpenEditor(curve);
        };
        DeleteBtn.Click += (_, _) => DeleteSelected();
        CloseBtn.Click += (_, _) => Close();

        RefreshList();
    }

    private CurveSettings? Selected =>
        CurveList.SelectedIndex >= 0 && CurveList.SelectedIndex < _app.Settings.Curves.Count
            ? Ordered()[CurveList.SelectedIndex]
            : null;

    private List<CurveSettings> Ordered() =>
        _app.Settings.Curves.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private int Usage(CurveSettings curve) =>
        _app.Settings.Controls.Count(c =>
            c.Enabled && string.Equals(c.CurveId, curve.Id, StringComparison.OrdinalIgnoreCase));

    private void RefreshList(int? keepIndex = null)
    {
        var list = Ordered();
        CurveList.ItemsSource = list
            .Select(c => $"{c.Name}   ({c.Type})   ·  used by {Usage(c)} fan(s)")
            .ToList();
        if (keepIndex is int i && i >= 0 && i < list.Count)
            CurveList.SelectedIndex = i;
    }

    private void SyncSelection()
    {
        var curve = Selected;
        NameBox.IsEnabled = curve is not null;
        EditBtn.IsEnabled = curve?.Type.Equals("graph", StringComparison.OrdinalIgnoreCase) == true;
        DeleteBtn.IsEnabled = curve is not null;
        NoteText.Text = "";
        if (curve is not null)
        {
            NameBox.Text = curve.Name;
        }
    }

    private void RenameSelected()
    {
        if (Selected is not CurveSettings curve || string.IsNullOrWhiteSpace(NameBox.Text))
            return;
        curve.Name = NameBox.Text.Trim();
        _app.Save();
        RefreshList(CurveList.SelectedIndex);
        _onChanged(); // fan dropdowns show names
    }

    private void CreateCurve(string type)
    {
        var curve = new CurveSettings
        {
            Id = $"curve-{Guid.NewGuid():N}",
            Type = type,
            Name = type == "graph" ? "New graph" : "New flat",
        };
        if (type == "graph")
            curve.Points = [new CurvePointDto(40, 20), new CurvePointDto(85, 90)];
        else
            curve.Percent = 50;

        _app.Settings.Curves.Add(curve);
        _app.Save();
        RefreshList();
        CurveList.SelectedIndex = Ordered().IndexOf(curve);
        _onChanged();

        if (type == "graph")
            OpenEditor(curve); // pick sensor + shape right away
    }

    private void DeleteSelected()
    {
        if (Selected is not CurveSettings curve)
            return;
        var used = Usage(curve);
        if (used > 0)
        {
            NoteText.Text = $"In use by {used} fan(s) — switch them to Monitor or another curve first.";
            return;
        }
        _app.Settings.Curves.Remove(curve);
        _app.Save();
        RefreshList();
        SyncSelection();
        _onChanged();
    }

    private void OpenEditor(CurveSettings curve)
    {
        var editor = new GraphEditorWindow(_app, curve, edited =>
        {
            var curves = _app.Settings.Curves;
            curves.RemoveAll(c => c.Id == edited.Id);
            curves.Add(edited);
            _app.Save();
            RefreshList();
            _onChanged();
        });
        editor.Show(this);
    }
}
