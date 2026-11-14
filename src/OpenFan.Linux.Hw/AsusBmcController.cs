using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

public sealed record BmcFanChannel(int Index, string Name, int MinPercent, double? TargetPercent, double? Rpm);
public sealed record BmcFanSnapshot(bool Available, string? Error, BmcFanChannel[] Channels);

/// <summary>
/// Root-side ASUS WRX90 BMC fan driver over the existing kernel IPMI interface.
/// Validated against board BIOS 1405, BMC 2.01 aux 22, product 1193.
/// Only per-zone manual curves are written; global modes and temperature sources
/// are never changed. Original curves are journaled before takeover and read back
/// after every write/restore. Session leases restore stale clients independently.
/// </summary>
public sealed class AsusBmcController : IDisposable
{
    public const string IdPrefix = "bmc:asus-wrx90:fan:";
    private static readonly string[] Labels = ["Zone1(CPU_FAN & CPU_OPT)", "Zone2(CHA_FAN1)", "Zone3(CHA_FAN2)",
        "Zone4(CHA_FAN3)", "Zone5(CHA_FAN4)", "Zone6(CHA_FAN5)", "Zone7(W_PUMP+)"];
    private static readonly string[] SensorNames = ["CPU_FAN", "CHA_FAN1", "CHA_FAN2", "CHA_FAN3", "CHA_FAN4", "CHA_FAN5", "W_PUMP+"];
    private readonly object _gate = new();
    private readonly Func<string[], BmcCommandResult> _run;
    private readonly string _journalPath;
    private readonly string _identity;
    private readonly Dictionary<int, byte[]> _original = [];
    private readonly Dictionary<int, (Guid Owner, DateTimeOffset Lease)> _owners = [];
    private readonly Dictionary<int, int> _targets = [];
    private readonly Dictionary<string, double?> _rpms = [];
    private DateTimeOffset _lastRpmRead;
    private readonly Timer? _watchdog;
    private FileStream? _lockFile;
    private bool _disposed;
    public bool Available { get; private set; }
    public string? LastError { get; private set; }

    public AsusBmcController() : this(new BmcProbe().Read(), args => BmcProbe.RunIpmiTool(args),
        "/var/lib/openfan/bmc-restore.json", ReadMachineIdentity(), startWatchdog: true) { }

    internal AsusBmcController(BmcProbeReport report, Func<string[], BmcCommandResult> run,
        string journalPath, string identity, bool startWatchdog = false)
    {
        _run = run;
        _journalPath = journalPath;
        _identity = identity;
        try
        {
            ValidateReport(report);
            Directory.CreateDirectory(Path.GetDirectoryName(_journalPath)!);
            File.SetUnixFileMode(Path.GetDirectoryName(_journalPath)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            _lockFile = new FileStream(_journalPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            Recover();
            Available = true;
            if (startWatchdog) _watchdog = new Timer(_ => ExpireLeases(DateTimeOffset.UtcNow), null, 1000, 1000);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            LastError = ex.Message;
            _lockFile?.Dispose();
            _lockFile = null;
        }
    }

    private static string ReadMachineIdentity()
    {
        try { return File.ReadAllText("/sys/class/dmi/id/product_uuid").Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return "unavailable"; }
    }

    private static void ValidateReport(BmcProbeReport report)
    {
        if (report.Error is not null) throw new InvalidDataException(report.Error);
        if (report.Board.GetValueOrDefault("board_name") != BmcProbe.SupportedBoard ||
            report.Board.GetValueOrDefault("board_vendor") != "ASUSTeK COMPUTER INC.")
            throw new InvalidDataException("BMC fan control requires the validated ASUS WRX90 board.");
        var info = Find(report, "mc", "info");
        string? Field(string name) => info.Output.Split('\n').Select(line => line.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim() == name).Select(p => p[1].Trim()).FirstOrDefault();
        var auxStart = info.Output.IndexOf("Aux Firmware Rev Info", StringComparison.Ordinal);
        var auxBytes = auxStart < 0 ? Array.Empty<byte>() : System.Text.RegularExpressions.Regex.Matches(info.Output[auxStart..], @"0x([0-9a-fA-F]{2})")
            .Select(m => byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
        if (!info.Success || Field("Manufacturer ID") != "2623" || Field("Product ID") != "4499 (0x1193)" ||
            Field("Firmware Revision") != "2.01" || !auxBytes.SequenceEqual(new byte[] { 0x22, 0, 0, 0 }))
            throw new InvalidDataException("Unvalidated BMC identity/firmware; fan writes disabled.");
        if (!Bytes(report, "raw", "0x30", "0x13", "0x01", "0x00").SequenceEqual(new byte[] { 8 }) ||
            !Bytes(report, "raw", "0x30", "0x13", "0x03", "0x00").SequenceEqual(new byte[] { 255, 0, 1, 2, 3, 4, 5, 6, 7 }))
            throw new InvalidDataException("Unrecognized BMC fan order.");
        for (var i = 0; i < Labels.Length; i++)
        {
            var data = Bytes(report, "raw", "0x30", "0x13", "0x00", Hex(i));
            var active = Bytes(report, "raw", "0x30", "0x0f", Hex(i));
            if (data.Length != 54 || Encoding.ASCII.GetString(data, 0, 32).TrimEnd('\0') != Labels[i] ||
                !ValidState(active) || !active.AsSpan(1).SequenceEqual(data.AsSpan(34, 10)))
                throw new InvalidDataException("Unrecognized BMC fan table at zone " + i + ".");
        }
    }

    private static BmcCommandResult Find(BmcProbeReport report, params string[] args) =>
        report.Commands.FirstOrDefault(c => c.Arguments.SequenceEqual(new[] { "-I", "open" }.Concat(args)))
        ?? new([], null, "", "missing command");
    private static byte[] Bytes(BmcProbeReport report, params string[] args)
    {
        var command = Find(report, args);
        return command.Success ? ParseHex(command.Output) : [];
    }
    internal static byte[] ParseHex(string value)
    {
        var tokens = value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new byte[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
            if (tokens[i].Length != 2 || !byte.TryParse(tokens[i], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out result[i])) return [];
        return result;
    }
    private static string Hex(int value) => "0x" + value.ToString("x2", CultureInfo.InvariantCulture);
    internal static bool ValidState(byte[] state) => state.Length == 11 && state[0] == 4 &&
        Enumerable.Range(0, 5).All(i => state[1 + i * 2] <= 100 && state[2 + i * 2] <= 100) &&
        Enumerable.Range(1, 4).All(i => state[1 + i * 2] > state[1 + (i - 1) * 2]) &&
        state[7] == 70 && state[9] == 100;
    private BmcCommandResult Run(params string[] args) => _run(["-I", "open", .. args]);
    private byte[] ReadState(int index)
    {
        var result = Run("raw", "0x30", "0x0f", Hex(index));
        var state = result.Success ? ParseHex(result.Output) : [];
        if (!ValidState(state)) throw new IOException("Cannot read validated manual curve for " + SensorNames[index] + ".");
        return state;
    }

    private void WriteState(int index, byte[] state)
    {
        if (!ValidState(state)) throw new InvalidDataException("Invalid fan restore curve.");
        var result = Run(["raw", "0x30", "0x0e", "0x04", Hex(index), .. state.Skip(1).Select(b => Hex(b))]);
        if (!result.Success || !ReadState(index).SequenceEqual(state))
            throw new IOException("BMC fan write/readback failed for " + SensorNames[index] + ". " + result.Error);
    }

    public static bool TryIndex(string id, out int index)
    {
        index = -1;
        return id.StartsWith(IdPrefix, StringComparison.Ordinal) && int.TryParse(id[IdPrefix.Length..],
            NumberStyles.None, CultureInfo.InvariantCulture, out index) && index is >= 0 and < 7 && id == IdPrefix + index;
    }

    public bool SetPercent(Guid owner, string id, int percent)
    {
        lock (_gate)
        {
            if (_disposed || !Available) return false;
            if (!TryIndex(id, out var index) || percent is < 0 or > 100)
            { LastError = "BMC fan target must be 0–100%."; return false; }
            if (_owners.TryGetValue(index, out var held) && held.Owner != owner)
            { LastError = "This BMC zone is owned by another OpenFan session."; return false; }
            try
            {
                var current = ReadState(index);
                if (!_original.ContainsKey(index))
                {
                    _original[index] = current;
                    try { SaveJournal(); } // must succeed before any fan write
                    catch { _original.Remove(index); throw; }
                }
                _owners[index] = (owner, DateTimeOffset.UtcNow);
                var target = (byte[])_original[index].Clone();
                // A manual duty is a flat curve. Thermal policy belongs to the user's
                // OpenFan curve; do not add undocumented floors or temperature overrides.
                for (var i = 0; i < 5; i++) target[2 + 2 * i] = (byte)percent;
                if (!target.SequenceEqual(current)) WriteState(index, target);
                _targets[index] = percent;
                LastError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var failure = ex.Message;
                Restore(index);
                LastError = failure + (LastError is null ? "" : " Restore: " + LastError);
                return false;
            }
        }
    }

    public bool SetDefault(Guid owner, string id)
    {
        lock (_gate)
        {
            if (!TryIndex(id, out var index) || _disposed) return false;
            if (_owners.TryGetValue(index, out var held) && held.Owner != owner) return false;
            return Restore(index);
        }
    }

    private bool Restore(int index)
    {
        if (!_original.TryGetValue(index, out var state)) return true;
        try
        {
            WriteState(index, state);
            _original.Remove(index);
            try { SaveJournal(); }
            catch { _original[index] = state; throw; }
            _owners.Remove(index);
            _targets.Remove(index);
            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { LastError = ex.Message; Console.Error.WriteLine("openfan BMC restore: " + LastError); return false; }
    }

    public void RestoreSession(Guid owner)
    {
        lock (_gate)
            foreach (var index in _owners.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray()) Restore(index);
    }

    internal void ExpireLeases(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var index in _owners.Where(p => now - p.Value.Lease > TimeSpan.FromSeconds(15)).Select(p => p.Key).ToArray()) Restore(index);
        }
    }

    public BmcFanSnapshot Snapshot(Guid owner)
    {
        lock (_gate)
        {
            if (!Available || _disposed) return new(false, LastError, []);
            // Heartbeat uses the same persistent connection as set/default. A stalled GUI
            // returns to the original temperature curve even if its socket remains open.
            foreach (var index in _owners.Where(p => p.Value.Owner == owner).Select(p => p.Key).ToArray())
                _owners[index] = (owner, DateTimeOffset.UtcNow);
            if (DateTimeOffset.UtcNow - _lastRpmRead >= TimeSpan.FromSeconds(2))
            {
                _rpms.Clear();
                var result = Run("sdr", "type", "Fan");
                if (result.Success)
                    foreach (var line in result.Output.Split('\n'))
                    {
                        var cells = line.Split('|', StringSplitOptions.TrimEntries);
                        if (cells.Length == 5 && cells[2] == "ok" && cells[4].EndsWith(" RPM", StringComparison.Ordinal) &&
                            double.TryParse(cells[4][..^4], NumberStyles.Float, CultureInfo.InvariantCulture, out var rpm) && rpm >= 0)
                            _rpms[cells[0]] = rpm;
                    }
                else LastError = "BMC fan RPM read failed: " + result.Error;
                _lastRpmRead = DateTimeOffset.UtcNow;
            }
            return new(true, LastError, Enumerable.Range(0, 7).Select(index => new BmcFanChannel(index, SensorNames[index],
                0, _targets.TryGetValue(index, out var target) ? target : null,
                _rpms.GetValueOrDefault(SensorNames[index]))).ToArray());
        }
    }

    private sealed record Recovery(string Identity, Dictionary<int, byte[]> Curves);
    private void SaveJournal()
    {
        if (_original.Count == 0) { File.Delete(_journalPath); return; }
        var temp = _journalPath + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            JsonSerializer.Serialize(stream, new Recovery(_identity, _original));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, _journalPath, overwrite: true);
    }

    private void Recover()
    {
        if (!File.Exists(_journalPath)) return;
        var recovery = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(_journalPath));
        if (recovery is null || recovery.Identity != _identity || _identity == "unavailable" || recovery.Curves is null ||
            recovery.Curves.Any(p => p.Key is < 0 or > 6 || p.Value is null || !ValidState(p.Value)))
            throw new InvalidDataException("BMC recovery journal identity/data mismatch; writes disabled.");
        foreach (var pair in recovery.Curves) _original[pair.Key] = pair.Value;
        foreach (var index in _original.Keys.ToArray())
            if (!Restore(index)) throw new IOException("Pending BMC curve could not be restored: " + LastError);
    }

    public void Dispose()
    {
        _watchdog?.Dispose();
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var index in _original.Keys.ToArray()) Restore(index);
            _disposed = true;
            Available = false;
            _lockFile?.Dispose();
        }
    }
}
