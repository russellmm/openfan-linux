using System.Globalization;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Privileged control of the CPU socket power limit (PPT) through the amd_hsmp_hwmon
/// <c>power1_cap</c> attribute, which the kernel maps to HSMP SET_SOCKET_POWER_LIMIT — the same
/// message hsmp-control sends. The user-session app stays unprivileged and reaches this only via
/// openfan-helper; expect the write methods to be called from the root daemon.
/// </summary>
/// <remarks>
/// Two different lifetimes, deliberately separate calls:
/// <list type="bullet">
///   <item><see cref="SetLiveLimitWatts"/> writes volatile SMU state. Firmware re-programs the
///   limit from BIOS CBS at every boot, so this alone does not survive a reboot (verified on this
///   board: 295 → 290 W came back to 295 W after restart).</item>
///   <item><see cref="SetBootLimitWatts"/> maintains /etc/hsmp-control/ppt_mw, which
///   hsmp-control-apply.service re-asserts early in boot. That is what "keep after reboot" means.</item>
/// </list>
/// Every live write is verified by reading the attribute back: HSMP may clamp a request, and a
/// silently different limit is worse than a reported failure. TDP and TjMax are not offered here at
/// all — they exist only in the BIOS variable, which this firmware refuses to let the OS write
/// while Secure Boot is enabled, so <see cref="CbsSetupReader"/> reports them read-only.
/// </remarks>
public sealed class CpuPowerControl(
    string hwmonRoot = "/sys/class/hwmon",
    string? bootConfigPath = null,
    CbsSetupReader? cbs = null,
    IReadOnlyList<string>? unitSearchDirs = null) : ICpuPowerWriter
{
    /// <summary>Service that re-asserts <see cref="BootConfigPath"/> early in boot. Until it is
    /// installed, writing that file changes nothing at next boot — so "keep after reboot" must not
    /// claim success without it.</summary>
    public const string BootUnitName = "hsmp-control-apply.service";

    private static readonly string[] DefaultUnitDirs =
    [
        "/etc/systemd/system", "/run/systemd/system",
        "/usr/local/lib/systemd/system", "/usr/lib/systemd/system",
    ];

    private readonly IReadOnlyList<string> _unitSearchDirs = unitSearchDirs ?? DefaultUnitDirs;
    /// <summary>Accepted window for user-requested limits; matches hsmp-control's 100000..300000 mW.
    /// Below roughly 200 W is untested on this board, so clamping by firmware is possible — the
    /// readback check turns that into a visible error rather than a wrong displayed value.</summary>
    public const int MinWatts = 100;
    public const int MaxWatts = 300;

    private readonly CbsSetupReader _cbs = cbs ?? new CbsSetupReader();

    public string BootConfigPath { get; } = bootConfigPath
        ?? Environment.GetEnvironmentVariable("HSMP_LIMIT_CONFIG")
        ?? "/etc/hsmp-control/ppt_mw";

    /// <summary>Why the last operation failed; null when healthy.</summary>
    public string? LastError { get; private set; }

    /// <summary>The live limit in whole watts, or null if the attribute is absent/unreadable.</summary>
    public int? LiveLimitWatts() => ReadMicrowatts(CapPath()) is { } uw ? (int)(uw / 1_000_000) : null;

    /// <summary>The limit BIOS holds in flash — what the SMU will be given at next boot. Null when
    /// the CBS variable is unreadable or PPT is Auto (fused default, not exposed to the OS).</summary>
    public int? BiosDefaultMilliwatts()
    {
        var r = _cbs.Read();
        return r.Ok && r.PptControl == "manual" ? r.PptBiosMw : null;
    }

    /// <summary>Set the live (volatile) socket power limit and verify it by readback.</summary>
    public bool SetLiveLimitWatts(int watts)
    {
        if (watts is < MinWatts or > MaxWatts)
            return Fail($"limit must be {MinWatts}-{MaxWatts} W");
        return SetLiveMicrowatts((long)watts * 1_000_000);
    }

    /// <summary>Put the limit back to what BIOS holds in flash. Deliberately not bounded by the UI
    /// window: a board whose stock default is above it must still be able to return to stock.</summary>
    public bool RestoreBiosDefault()
    {
        var mw = BiosDefaultMilliwatts();
        if (mw is null)
            return Fail("BIOS default PPT is not readable (CBS variable unavailable, or PPT set to Auto)");
        return SetLiveMicrowatts(mw.Value * 1000L);
    }

    /// <summary>Maintain the boot-time limit that hsmp-control-apply.service re-asserts.
    /// Null removes the override entirely, leaving firmware in charge.</summary>
    public bool SetBootLimitWatts(int? watts)
    {
        try
        {
            if (watts is null)
            {
                if (File.Exists(BootConfigPath)) File.Delete(BootConfigPath);
                LastError = null;
                return true;
            }
            if (watts is < MinWatts or > MaxWatts)
                return Fail($"boot limit must be {MinWatts}-{MaxWatts} W");

            var dir = Path.GetDirectoryName(BootConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // Temp + rename: a boot helper must never read a half-written file.
            var tmp = BootConfigPath + ".tmp";
            File.WriteAllText(tmp, (watts.Value * 1000).ToString(CultureInfo.InvariantCulture) + "\n");
            File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(tmp, BootConfigPath, overwrite: true);
            LastError = null;
            return true;
        }
        catch (IOException e) { return Fail($"could not update {BootConfigPath}: {e.Message}"); }
        catch (UnauthorizedAccessException) { return Fail($"not permitted to write {BootConfigPath} — needs root"); }
    }

    /// <summary>True when writing <see cref="BootConfigPath"/> will actually take effect at next
    /// boot, i.e. hsmp-control-apply.service is installed and not masked. A masked unit (a symlink
    /// to /dev/null) counts as absent — someone deliberately switched the re-assertion off.</summary>
    public bool BootPersistenceWired() => _unitSearchDirs.Any(d => UnitPresent(Path.Combine(d, BootUnitName)));

    private static bool UnitPresent(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget != "/dev/null";
        }
        catch (IOException) { return false; }
    }

    private bool SetLiveMicrowatts(long microwatts)
    {
        var chip = HsmpPowerReader.FindHsmpChipDirectory(hwmonRoot);
        if (chip is null)
            return Fail("amd_hsmp_hwmon not present — is the amd_hsmp kernel module loaded?");
        var cap = Path.Combine(chip, "power1_cap");

        var max = ReadMicrowatts(Path.Combine(chip, "power1_cap_max"));
        if (max is not null && microwatts > max)
            return Fail($"limit exceeds the firmware maximum of {max / 1_000_000} W");

        try
        {
            File.WriteAllText(cap, microwatts.ToString(CultureInfo.InvariantCulture));
        }
        catch (IOException e) { return Fail($"write failed: {e.Message}"); }
        catch (UnauthorizedAccessException) { return Fail("not permitted to write power1_cap — needs root (run via openfan-helper)"); }

        var back = ReadMicrowatts(cap);
        if (back is null) return Fail("limit written but could not be read back; outcome unknown");
        if (back != microwatts)
            return Fail($"firmware settled at {back / 1_000_000} W instead of {microwatts / 1_000_000} W (clamped)");

        LastError = null;
        return true;
    }

    private string? CapPath() =>
        HsmpPowerReader.FindHsmpChipDirectory(hwmonRoot) is { } chip ? Path.Combine(chip, "power1_cap") : null;

    // Note on units: power1_cap and friends are microwatts; the protocol and UI speak whole watts.

    private bool Fail(string why)
    {
        LastError = why;
        return false;
    }

    private static long? ReadMicrowatts(string? path)
    {
        if (path is null) return null;
        try
        {
            if (long.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out var v) && v >= 0)
                return v;
        }
        catch (IOException) { /* attribute absent or device removed */ }
        catch (UnauthorizedAccessException) { /* unreadable attribute */ }
        return null;
    }
}
