using System.Diagnostics;
using System.Globalization;

namespace OpenFan.Linux.Hw;

public sealed record BmcCommandResult(string[] Arguments, int? ExitCode, string Output, string Error, bool TimedOut = false)
{
    public bool Success => ExitCode == 0 && !TimedOut;
}

public sealed record BmcProbeReport(
    int SchemaVersion,
    DateTimeOffset CapturedAt,
    Dictionary<string, string?> Board,
    Dictionary<string, Dictionary<string, string?>> Hwmon,
    List<BmcCommandResult> Commands,
    string? Error);

/// <summary>
/// One-shot, read-only evidence collection for the WRX90 BMC backend. The raw response
/// layouts and physical channel map must be verified before implementing fan writes.
/// Never called from sensor discovery or polling; requires local /dev/ipmi0 access.
/// </summary>
public sealed class BmcProbe
{
    public const string SupportedBoard = "Pro WS WRX90E-SAGE SE";
    public const string ProtocolReference = "https://dlcdnets.asus.com/pub/ASUS/mb/Add-on_card/IPMI_EXPANSION_CARD/E26328_IPMI_Card_EM_V3_WEB.pdf";
    private readonly Func<string[], BmcCommandResult> _run;
    private readonly string _dmiRoot;
    private readonly string _hwmonRoot;

    public BmcProbe() : this(args => RunIpmiTool(args), "/sys/class/dmi/id", "/sys/class/hwmon") { }

    internal BmcProbe(Func<string[], BmcCommandResult> run, string dmiRoot, string hwmonRoot)
        => (_run, _dmiRoot, _hwmonRoot) = (run, dmiRoot, hwmonRoot);

    public BmcProbeReport Read()
    {
        var board = new[] { "board_vendor", "board_name", "board_version", "bios_vendor", "bios_version", "bios_date" }
            .ToDictionary(key => key, key => ReadText(Path.Combine(_dmiRoot, key)));
        var commands = new List<BmcCommandResult>();
        var hwmon = ReadHwmon();
        BmcProbeReport Report(string? error) => new(1, DateTimeOffset.UtcNow, board, hwmon, commands, error);
        BmcCommandResult Run(params string[] args)
        {
            // Explicit local transport: no network endpoint or credentials accepted.
            var result = _run(["-I", "open", .. args]);
            commands.Add(result);
            return result;
        }

        if (!Run("mc", "info").Success)
            return Report("Cannot read the local BMC. Run this diagnostic as root; check ipmitool and /dev/ipmi0. See command errors.");
        if (!Run("sdr", "type", "Fan").Success || !Run("sdr", "type", "Temperature").Success)
            return Report("BMC sensor inventory failed. See command errors; no OEM commands were issued.");

        // Standard IPMI inventory above is portable. These OEM getters are ASUS-specific.
        if (board["board_vendor"] != "ASUSTeK COMPUTER INC." || board["board_name"] != SupportedBoard)
            return Report("ASUS OEM reads skipped: this probe is restricted to " + SupportedBoard + ".");

        // ASUS E26328, appendix A-6: only command types 00, 01, 03 are getters.
        // In particular 02 writes PWM data and FF reloads the table: never issue either.
        var count = Run("raw", "0x30", "0x13", "0x01", "0x00");
        if (count.TimedOut) return Report("BMC PWM-count read timed out; stopped OEM reads.");
        var order = Run("raw", "0x30", "0x13", "0x03", "0x00");
        if (order.TimedOut || (!count.Success && !order.Success))
            return Report("BMC PWM inventory unavailable; stopped OEM reads. Firmware protocol needs investigation.");

        var countBytes = AsusBmcController.ParseHex(count.Output);
        var orderBytes = AsusBmcController.ParseHex(order.Output);
        if (!count.Success || countBytes.Length != 1 || countBytes[0] is < 1 or > 23 ||
            !order.Success || orderBytes.Length != countBytes[0] + 1 || orderBytes[0] != 0xff ||
            orderBytes.Skip(1).Any(i => i >= countBytes[0]) || orderBytes.Skip(1).Distinct().Count() != countBytes[0])
            return Report("Unrecognized BMC PWM count/order response; stopped OEM reads.");
        // Live firmware exposes eight slots, seven populated. Out-of-range 13/00 reads
        // return unrelated bytes rather than an error: only use advertised indices.
        foreach (var index in orderBytes.Skip(1))
        {
            var hex = "0x" + index.ToString("x2", CultureInfo.InvariantCulture);
            if (Run("raw", "0x30", "0x13", "0x00", hex).TimedOut ||
                Run("raw", "0x30", "0x0f", hex).TimedOut)
                return Report("BMC fan-configuration read timed out; stopped OEM reads.");
        }
        // Firmware 2.01/aux22 rejects the manual's no-payload 93 getter. Do not guess
        // a payload: that command also sets temperature sources.
        return Report(null);
    }

    private Dictionary<string, Dictionary<string, string?>> ReadHwmon()
    {
        var result = new Dictionary<string, Dictionary<string, string?>>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_hwmonRoot, "hwmon*").Order(StringComparer.Ordinal))
            {
                var values = new Dictionary<string, string?> { ["name"] = ReadText(Path.Combine(dir, "name")) };
                foreach (var file in Directory.EnumerateFiles(dir).Order(StringComparer.Ordinal))
                {
                    var name = Path.GetFileName(file);
                    // No serial numbers, BMC accounts, network configuration or credentials.
                    if (System.Text.RegularExpressions.Regex.IsMatch(name,
                        @"^(fan[0-9]+_(input|label)|pwm[0-9]+(_enable|_mode)?|temp[0-9]+_(input|label))$"))
                        values[name] = ReadText(file);
                }
                result[Path.GetFileName(dir)] = values;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static string? ReadText(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static BmcCommandResult RunIpmiTool(string[] arguments, string executable = "/usr/bin/ipmitool", int timeoutMs = 5000)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        start.Environment["LC_ALL"] = "C";
        try
        {
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                return new(arguments, null, stdout.GetAwaiter().GetResult().Trim(),
                    stderr.GetAwaiter().GetResult().Trim(), TimedOut: true);
            }
            return new(arguments, process.ExitCode, stdout.GetAwaiter().GetResult().Trim(), stderr.GetAwaiter().GetResult().Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return new(arguments, null, "", ex.Message);
        }
    }
}
