using System.Globalization;

namespace OpenFan.Core.Hardware;

/// <summary>Keep large lifetime counters inside the compact table's fixed-width numeric columns.</summary>
public static class SensorCountFormat
{
    public static string Format(double? value)
    {
        if (value is not { } n || !double.IsFinite(n)) return "—";
        var size = Math.Abs(n);
        if (size >= 1e15) return n.ToString("0.###E+0", CultureInfo.CurrentCulture);
        var (scale, suffix) = size >= 1e12 ? (1e12, "T") : size >= 1e9 ? (1e9, "B") : size >= 1e6 ? (1e6, "M") : (1d, "");
        return suffix.Length == 0 ? n.ToString("N0", CultureInfo.CurrentCulture) : (n / scale).ToString("0.###", CultureInfo.CurrentCulture) + suffix;
    }
}
