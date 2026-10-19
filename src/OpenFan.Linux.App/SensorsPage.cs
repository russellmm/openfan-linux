using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.App;

public sealed partial class MainWindow
{
    private bool _sensorsPageBuilt;
    private readonly List<(string Id, Control Row, Action<SensorStatistics.Snapshot> Update)> _sensorValueUpdaters = [];
    private TextBox? _activeSensorEditor;
    private bool _homeAliasesDirty;
    private const string SensorColumns = "*,88,88,88,88,52";

    private string SensorsSubtitle()
    {
        var count = _app.Inventory.Count(i => IsSensorish(i) && SensorPollingPlan.TableVisible(i, _app.Settings));
        return $"{count} sensors · {_app.SensorReaderDescription}";
    }

    /// <summary>
    /// Escapes text for a MenuItem header. Avalonia parses "_" as an access-key marker and swallows the
    /// first one, which silently mangles sensor names — "WD_BLACK SN850X" showed up as "WDBLACK SN850X" and
    /// "PCHCHIP_CPU_MAX_TEMP" as "PCHCHIPCPU_MAX_TEMP". Doubling it renders a literal underscore.
    /// </summary>
    internal static string MenuText(string text) =>
        text.Contains('_') ? text.Replace("_", "__") : text;

    /// <summary>Measurements, counters, optional status flags and readback of known fan controls.</summary>
    private static bool IsSensorish(HardwareItem i) =>
        i.Kind != HardwareKind.Control || i.Backend is "hwmon" or "nvml";

    // Maps hwmon index -> (drive model, PCI address). NVMe temp chips are /sys/class/hwmon/hwmonN (name
    // "nvme"); the model file sits behind the device symlink and reads through it directly. The PCI address
    // comes from that same resolved path — two drives of the same model are otherwise indistinguishable,
    // and unlike the hwmon index the address is stable across reboots.
    private static Lazy<Dictionary<string, (string Model, string PciBus)>> NvmeLabels = new(ReadNvmeLabels);
    private static Dictionary<string, (string Model, string PciBus)> ReadNvmeLabels()
    {
        var map = new Dictionary<string, (string, string)>();
        try
        {
            foreach (var hm in Directory.GetDirectories("/sys/class/hwmon"))
            {
                string chip;
                try { chip = File.ReadAllText(Path.Combine(hm, "name")).Trim(); } catch { continue; }
                if (chip != "nvme")
                    continue;

                var idx = Path.GetFileName(hm).Replace("hwmon", "");

                // The hwmon `device` link is relative and the class directories are themselves links, so a
                // fully resolved path is unreliable here. The link's *name* is all that is needed: it is the
                // NVMe controller (nvmeN), whose class directory holds model and PCI address as plain files.
                string controller;
                try
                {
                    controller = new DirectoryInfo(Path.Combine(hm, "device")).ResolveLinkTarget(false)?.Name ?? "";
                }
                catch { controller = ""; }
                if (controller.Length == 0) continue;

                string model = "", pci = "";
                try { model = File.ReadAllText($"/sys/class/nvme/{controller}/model").Trim(); } catch { }
                try { pci = GpuFormat.CompactPciBus(File.ReadAllText($"/sys/class/nvme/{controller}/address").Trim()); } catch { }

                if (model.Length > 0 || pci.Length > 0)
                    map[idx] = (model, pci);
            }
        }
        catch { }
        return map;
    }

    /// <summary>Drive model for a "nvme:&lt;hwmon index&gt;" device key, or the key itself when unknown.</summary>
    internal static string NvmeModel(string key) =>
        key.StartsWith("nvme:") && NvmeLabels.Value.TryGetValue(key[5..], out var lbl) ? (lbl.Model.Length > 0 ? lbl.Model : key) : key;

    /// <summary>PCI address for a "nvme:&lt;hwmon index&gt;" device key, "" when the path carried none.</summary>
    internal static string NvmePciBus(string key) =>
        key.StartsWith("nvme:") && NvmeLabels.Value.TryGetValue(key[5..], out var lbl) ? lbl.PciBus : "";

    private void EnsureSensorsPage()
    {
        if (_sensorsPageBuilt) return;
        _sensorsPageBuilt = true;
        var previousOffset = SensorsPanel.Offset;
        SensorsContent.Children.Clear();
        _sensorValueUpdaters.Clear();
        PageSubtitle.Text = SensorsSubtitle();

        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock
        {
            Text = "Statistics since launch or last reset",
            FontSize = 12, Foreground = Secondary, TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var flags = new CheckBox { Content = "Show status flags", IsChecked = _app.Settings.ShowSensorStatus, FontSize = 12, Margin = new Thickness(0, 0, 12, 0) };
        flags.IsCheckedChanged += (_, _) => { _app.Settings.ShowSensorStatus = flags.IsChecked == true; _app.Save(); _sensorsPageBuilt = false; UpdateSensorsPage(); };
        var reset = new Button { Content = "Reset statistics", Height = 30, FontSize = 12 };
        reset.Click += (_, _) => { _app.ResetSensorStatistics(); UpdateSensorsPage(); };
        heading.Children.Add(Col(new StackPanel { Orientation = Orientation.Horizontal, Children = { BuildSensorGroupPicker(), flags, reset } }, 1));
        SensorsContent.Children.Add(heading);
        if (_app.SensorReaderError is { } error)
            SensorsContent.Children.Add(new TextBlock { Text = "Sensor reader: " + error, Foreground = Warn, FontSize = 12, TextWrapping = TextWrapping.Wrap });

        var sensors = _app.Inventory.Where(i => IsSensorish(i) && SensorPollingPlan.TableVisible(i, _app.Settings)).GroupBy(SensorPollingPlan.Group)
            .OrderBy(g => Array.IndexOf(SensorPollingPlan.GroupOrder, g.Key) is var ix && ix >= 0 ? ix : 99).ToArray();
        if (sensors.Length == 0)
            SensorsContent.Children.Add(new TextBlock { Text = "No groups selected. Use Sensor groups to show them.", Foreground = Secondary });
        foreach (var group in sensors)
        {
            var body = new StackPanel { Spacing = 0 };
            var groupRows = new StackPanel { IsVisible = !_app.Settings.CollapsedSensorGroups.Contains(group.Key) };
            var groupArrow = new TextBlock { Text = groupRows.IsVisible ? "▾" : "▸", Foreground = Secondary, VerticalAlignment = VerticalAlignment.Center };
            var title = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto") };
            title.Children.Add(groupArrow);
            title.Children.Add(Col(new TextBlock { Text = group.Key, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = ValueText }, 1));
            title.Children.Add(Col(new TextBlock { Text = $"{group.Count()} sensors", FontSize = 12, Foreground = Secondary,
                VerticalAlignment = VerticalAlignment.Center }, 2));
            var groupHeader = new Button { Content = title, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(5, 2, 5, 8), HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch };
            ToolTip.SetTip(groupHeader, "Collapse to pause optional readings for this group.");
            groupHeader.Click += (_, _) =>
            {
                groupRows.IsVisible = !groupRows.IsVisible;
                if (groupRows.IsVisible) _app.Settings.CollapsedSensorGroups.RemoveAll(g => g == group.Key);
                else if (!_app.Settings.CollapsedSensorGroups.Contains(group.Key)) _app.Settings.CollapsedSensorGroups.Add(group.Key);
                groupArrow.Text = groupRows.IsVisible ? "▾" : "▸";
                _app.Save();
                UpdateSensorsPage();
            };
            body.Children.Add(groupHeader);
            body.Children.Add(groupRows);
            var devices = group.GroupBy(SensorPollingPlan.DeviceKey).OrderBy(g => SensorSortKey(NvmeModel(g.Key)), StringComparer.OrdinalIgnoreCase)
                .ThenBy(g => NvmePciBus(g.Key), StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var device in devices)
            {
                var inner = new StackPanel();
                inner.Children.Add(BuildSensorTableHeader());
                foreach (var item in device.OrderBy(i => i.Kind).ThenBy(i => SensorSortKey(i.Name), StringComparer.OrdinalIgnoreCase))
                    inner.Children.Add(BuildSensorRow(item));
                if (devices.Length > 1)
                {
                    var key = group.Key + "|" + device.Key;
                    inner.IsVisible = !_app.Settings.CollapsedSensorDevices.Contains(key);
                    var arrow = new TextBlock { Text = inner.IsVisible ? "▾" : "▸", Foreground = Secondary, FontSize = 12 };
                    var label = SensorDeviceLabel(device.Key);
                    var headerText = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeight.SemiBold,
                        Foreground = ValueText, TextTrimming = TextTrimming.CharacterEllipsis };
                    ToolTip.SetTip(headerText, label);
                    var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*,Auto") };
                    headerGrid.Children.Add(arrow); headerGrid.Children.Add(Col(headerText, 1));
                    headerGrid.Children.Add(Col(new TextBlock { Text = $"{device.Count()} sensors", FontSize = 11, Foreground = Secondary }, 2));
                    var header = new Button { Content = headerGrid, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                        Padding = new Thickness(5, 7), HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        HorizontalAlignment = HorizontalAlignment.Stretch };
                    header.Click += (_, _) =>
                    {
                        inner.IsVisible = !inner.IsVisible;
                        if (inner.IsVisible) _app.Settings.CollapsedSensorDevices.RemoveAll(k => k == key); else _app.Settings.CollapsedSensorDevices.Add(key);
                        _app.Save();
                        UpdateSensorsPage();
                        arrow.Text = inner.IsVisible ? "▾" : "▸";
                    };
                    groupRows.Children.Add(header);
                }
                groupRows.Children.Add(inner);
            }
            SensorsContent.Children.Add(new Border
            {
                Background = CardBg, BorderBrush = CardBorder, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(12, 10), Child = body,
            });
        }
        Dispatcher.UIThread.Post(() => SensorsPanel.Offset = previousOffset, DispatcherPriority.Background);
    }

    private Control BuildSensorGroupPicker()
    {
        var flyout = new Flyout();
        var body = new StackPanel { Spacing = 5, Width = 320 };
        body.Children.Add(new TextBlock { Text = "Choose sensor groups", FontWeight = FontWeight.SemiBold });
        body.Children.Add(new TextBlock { Text = "Hidden or collapsed groups pause sampling. Fan curves, the overlay and visible pages keep the readings they need.",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = Secondary, Margin = new Thickness(0, 0, 0, 8) });
        var choices = new StackPanel { Spacing = 3 };
        var groups = _app.Inventory.Where(i => _app.Settings.ShowSensorStatus || i.Kind != HardwareKind.Status).GroupBy(SensorPollingPlan.Group).ToDictionary(g => g.Key, g => g.Count());
        foreach (var group in SensorPollingPlan.GroupOrder.Where(groups.ContainsKey))
        {
            var option = new CheckBox { Content = $"{group} ({groups[group]})", IsChecked = !_app.Settings.HiddenSensorGroups.Contains(group) };
            option.IsCheckedChanged += (_, _) =>
            {
                if (option.IsChecked == true) _app.Settings.HiddenSensorGroups.RemoveAll(g => g == group);
                else if (!_app.Settings.HiddenSensorGroups.Contains(group)) _app.Settings.HiddenSensorGroups.Add(group);
                _app.Save();
            };
            choices.Children.Add(option);
        }
        body.Children.Add(new ScrollViewer { MaxHeight = 350, Content = choices });
        var done = new Button { Content = "Done", HorizontalAlignment = HorizontalAlignment.Right };
        done.Click += (_, _) => flyout.Hide();
        body.Children.Add(done);
        flyout.Content = body;
        flyout.Closed += (_, _) => { _sensorsPageBuilt = false; UpdateSensorsPage(); };
        return new Button { Content = "Sensor groups", FontSize = 12, Height = 30, Margin = new Thickness(0, 0, 12, 0), Flyout = flyout };
    }

    private static string SensorDeviceLabel(string key)
    {
        if (key.StartsWith("nvme:", StringComparison.Ordinal))
        {
            var model = NvmeModel(key); var pci = NvmePciBus(key);
            return pci.Length > 0 ? $"{pci} · {model}" : model;
        }
        var last = key.LastIndexOf(' ');
        return last > 0 && System.Text.RegularExpressions.Regex.IsMatch(key[(last + 1)..], @"^[0-9A-Fa-f]+:[0-9A-Fa-f]+\.[0-9A-Fa-f]+$")
            ? $"{key[(last + 1)..]} · {key[..last]}" : key;
    }

    private static string SensorSortKey(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, @"\d+", m => m.Value.PadLeft(12, '0'));

    private static TextBlock SensorCell(string text, bool current = false) => new()
    {
        Text = text, FontSize = 12, FontFamily = new FontFamily("monospace"),
        Foreground = current ? Accent : Secondary, FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal,
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 2),
    };

    private Control BuildSensorTableHeader()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(SensorColumns), MinHeight = 27 };
        var labels = new[] { "Sensor", "Current", "Min", "Max", "Average", "Unit" };
        for (var i = 0; i < labels.Length; i++)
        {
            var text = SensorCell(labels[i]); text.FontFamily = FontFamily.Default; text.FontWeight = FontWeight.SemiBold;
            if (i == 0) text.HorizontalAlignment = HorizontalAlignment.Left;
            grid.Children.Add(Col(text, i));
        }
        return new Border { Child = grid, Background = new SolidColorBrush(Color.Parse("#171B1F")),
            BorderBrush = CardBorder, BorderThickness = new Thickness(0, 0, 0, 1) };
    }

    private static string DefaultSensorName(HardwareItem item)
    {
        if (item.Backend == "nvml" && item.Name.StartsWith(item.Group + " ", StringComparison.Ordinal))
        {
            var shortName = item.Name[(item.Group.Length + 1)..];
            return item.Kind == HardwareKind.Temperature ? "Core temperature" : shortName;
        }
        if (item.Backend == "hwmon" && item.Kind == HardwareKind.Control)
            return "PWM " + item.Id.Split(':').Last() + " duty";
        return item.Name;
    }

    private Control BuildSensorRow(HardwareItem item)
    {
        var defaultName = DefaultSensorName(item);
        var savedName = _app.Settings.SensorAliases.GetValueOrDefault(item.Id, defaultName);
        var label = new TextBlock { Text = savedName, FontSize = 12, Foreground = ValueText,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 2), TextTrimming = TextTrimming.CharacterEllipsis };
        var nameHost = new Border { Height = 25, Background = Brushes.Transparent, Child = label };
        ToolTip.SetTip(nameHost, $"{item.Name}\n{item.Group}\n{item.Id}\n{item.Details}\nClick to rename; Enter saves, Esc cancels.");
        nameHost.PointerPressed += (_, e) =>
        {
            if (nameHost.Child is TextBox || !e.GetCurrentPoint(nameHost).Properties.IsLeftButtonPressed) return;
            var editor = new TextBox { Text = savedName, Classes = { "cardname" }, FontSize = 12,
                FontWeight = FontWeight.Normal, Height = 25, MinHeight = 25, Padding = new Thickness(5, 2) };
            _activeSensorEditor = editor;
            _app.DeferInventoryDiscovery = true;
            var finished = false;
            void Finish(bool cancel)
            {
                if (finished) return;
                finished = true;
                var text = cancel ? savedName : (editor.Text ?? "").Trim();
                if (text != savedName)
                {
                    if (text.Length == 0 || text == defaultName || text == item.Name)
                    { _app.Settings.SensorAliases.Remove(item.Id); text = defaultName; }
                    else _app.Settings.SensorAliases[item.Id] = text;
                    savedName = text;
                    _app.Save();
                    _homeAliasesDirty = true; // Build hidden curve dropdowns only when Home is visited.
                }
                label.Text = savedName;
                nameHost.Child = label;
                _activeSensorEditor = null;
                _app.DeferInventoryDiscovery = false;
                Dispatcher.UIThread.Post(UpdateSensorsPage, DispatcherPriority.Background);
            }
            editor.LostFocus += (_, _) => Finish(cancel: false);
            editor.KeyDown += (_, key) =>
            {
                if (key.Key is not (Key.Enter or Key.Escape)) return;
                key.Handled = true;
                Finish(cancel: key.Key == Key.Escape);
            };
            nameHost.Child = editor;
            Dispatcher.UIThread.Post(() => { if (!finished) { editor.Focus(); editor.SelectAll(); } }, DispatcherPriority.Input);
            e.Handled = true;
        };
        var current = SensorCell("—", current: true); var minimum = SensorCell("—");
        var maximum = SensorCell("—"); var average = SensorCell("—");
        var unit = item.Unit.Length > 0 ? item.Unit : item.Kind switch { HardwareKind.Temperature => "°C", HardwareKind.Tach => "RPM", _ => "%" };
        var format = item.Kind switch
        {
            HardwareKind.Voltage or HardwareKind.Current or HardwareKind.Energy => "N3",
            HardwareKind.Temperature or HardwareKind.Power or HardwareKind.Humidity or HardwareKind.Data => "N1",
            _ => "N0",
        };
        string Format(double? value) => value?.ToString(format, CultureInfo.CurrentCulture) ?? "—";
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(SensorColumns), MinHeight = 26,
            Children = { nameHost, Col(current, 1), Col(minimum, 2), Col(maximum, 3), Col(average, 4), Col(SensorCell(unit), 5) } };
        var row = new Border { Child = grid, BorderBrush = CardBorder, BorderThickness = new Thickness(0, 0, 0, 1) };
        static void Set(TextBlock cell, string text) { if (cell.Text != text) cell.Text = text; }
        ToolTip.SetTip(current, "Valid readings since launch or Reset statistics. Hidden/collapsed groups pause optional sampling.");
        _sensorValueUpdaters.Add((item.Id, row, sample =>
        {
            Set(current, Format(sample.Current)); Set(minimum, Format(sample.Minimum)); Set(maximum, Format(sample.Maximum));
            Set(average, item.Kind is HardwareKind.Status or HardwareKind.Counter ? "—" : Format(sample.Average));
        }));
        return row;
    }

    private void UpdateSensorsPage()
    {
        if (!SensorsPanel.IsVisible || _activeSensorEditor is not null) return;
        if (!_sensorsPageBuilt) EnsureSensorsPage();
        foreach (var (id, row, update) in _sensorValueUpdaters)
        {
            if (!row.IsEffectivelyVisible) continue;
            var position = row.TranslatePoint(new Point(0, 0), SensorsPanel);
            if (position is { } point && (point.Y + row.Bounds.Height < 0 || point.Y > SensorsPanel.Bounds.Height)) continue;
            update(_app.SensorStatistics.Get(id));
        }
    }
}
