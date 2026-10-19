using System.Globalization;

namespace OpenFan.Core.Hardware;

/// <summary>Configured SPD hub thresholds, separate from sampled temperatures and their extrema.</summary>
public static class DramTemperatureLimits
{
    public static readonly string[] Suffixes = ["min", "max", "lcrit", "crit"];

    public static string Label(string suffix) => suffix switch
    {
        "min" => "Low", "max" => "High", "lcrit" => "Critical low", "crit" => "Critical high",
        _ => throw new ArgumentOutOfRangeException(nameof(suffix)),
    };

    public static string Id(string temperatureId, string suffix) => temperatureId + ":limit:" + suffix;

    public static string Format(string temperatureId, IEnumerable<string> available, Func<string, double?> reading) =>
        "Limits: " + string.Join(" · ", Suffixes.Where(available.Contains).Select(suffix =>
        {
            var value = reading(Id(temperatureId, suffix));
            var text = value is { } v && double.IsFinite(v) ? v.ToString("0.#", CultureInfo.CurrentCulture) + "°C" : "—";
            return Label(suffix) + " " + text;
        }));
}
