namespace OpenFan.Core;

public static class AccentHex
{
    public const string Default = "#F0A03C";

    public static bool TryParse(string? hex, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        if (string.IsNullOrWhiteSpace(hex))
            return false;
        var s = hex.Trim();
        if (s.StartsWith('#'))
            s = s[1..];
        if (s.Length == 3)
        {
            try
            {
                r = (byte)Convert.ToInt32(string.Concat(s[0], s[0]), 16);
                g = (byte)Convert.ToInt32(string.Concat(s[1], s[1]), 16);
                b = (byte)Convert.ToInt32(string.Concat(s[2], s[2]), 16);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
        if (s.Length == 6 && int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var n))
        {
            r = (byte)((n >> 16) & 0xFF);
            g = (byte)((n >> 8) & 0xFF);
            b = (byte)(n & 0xFF);
            return true;
        }
        return false;
    }

    public static string Normalize(string? hex)
        => TryParse(hex, out var r, out var g, out var b) ? $"#{r:X2}{g:X2}{b:X2}" : Default;
}
