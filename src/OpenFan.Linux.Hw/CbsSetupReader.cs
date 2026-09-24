using System.Buffers.Binary;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Read-only view of the BIOS "SMU Common Options" fields (TDP, PPT, TjMax) held in the AMD
/// CBS setup variable. This class never opens /dev/hsmp and never writes to efivarfs.
/// </summary>
/// <remarks>
/// Read-only is a firmware fact, not a policy choice: this board refuses OS-runtime
/// SetVariable with EFI_SECURITY_VIOLATION while Secure Boot is enabled (verified — even a
/// no-op rewrite of Timeout fails), so TDP/TjMax can only be edited in Setup and take effect
/// on the next boot. efivarfs also caches variable contents at boot, which matches exactly how
/// often these values can change.
///
/// PPT is different: it has a live control surface (HSMP / hwmon power1_cap) that does NOT
/// persist across boot, plus this persistent BIOS default. The SMU limit after boot always
/// comes from the value read here, so <see cref="Reading.PptBiosMw"/> is the correct restore
/// target across reboots — unlike a /run journal, which forgets at power loss.
///
/// Offsets are byte offsets into the variable DATA; an efivarfs file prefixes that data with a
/// 4-byte attribute word. They come from the BIOS Setting Mapping Table JSON embedded in BIOS
/// 0617 and were verified against the live variable; they are listed with provenance in
/// tools/hsmp-control/README.md.
/// A different board or BIOS needs re-verification, not just a new path.
/// </remarks>
public sealed class CbsSetupReader(string? varPath = null)
{
    public const string DefaultVarPath =
        "/sys/firmware/efi/efivars/AmdSetupSHP-3a997502-647a-4c82-998e-52ef9486a247";

    private const uint CbsMagic = 0xE5AF127C;
    private const int AttrWordLen = 4;

    // Data-relative offsets, width in bytes: control byte then its value.
    private const int TdpControlOff = 1043, TdpOff = 1044;
    private const int PptControlOff = 1048, PptOff = 1049;
    private const int TjMaxControlOff = 1053, TjMaxOff = 1054;
    private const int MinDataLen = TjMaxOff + 4;

    // Plausibility bounds. A wrong offset or a changed layout must produce nulls with a reason,
    // never a confident number that a fan curve would act on.
    private const uint MinTdpMw = 1, MaxTdpMw = 600_000;
    private const uint MinPptMw = 1, MaxPptMw = 600_000;
    private const uint MinTjMax = 20, MaxTjMax = 120;

    /// <param name="control">"manual", "auto", or null when nothing could be read.</param>
    public sealed record Reading(
        bool Ok,
        string? Reason,
        int? TdpMw,
        int? PptBiosMw,
        int? TjMaxCelsius,
        string? TdpControl,
        string? PptControl,
        string? TjMaxControl);

    private static readonly Reading Unreadable = new(false, "CBS setup variable not readable",
        null, null, null, null, null, null);

    public string VarPath { get; } =
        varPath
        ?? Environment.GetEnvironmentVariable("CBS_SETUP_VAR")
        ?? DefaultVarPath;

    /// <summary>
    /// Returns a Reading that is never null. When anything cannot be trusted — variable absent
    /// or unreadable, shorter than the field table, magic mismatch after a BIOS update, an
    /// unrecognised control byte, or a value outside the plausible range — every value is null
    /// and <see cref="Reading.Reason"/> explains why. Auto fields are also null: with Auto the
    /// effective limit comes from CPU fuses and is not exposed to the OS, so reporting a number
    /// would be wrong. Distinguish "auto" (control set, value absent) from unknown via
    /// <see cref="Reading.Ok"/> and the control strings.
    /// </summary>
    public Reading Read()
    {
        byte[] raw;
        try
        {
            raw = File.ReadAllBytes(VarPath);
        }
        catch (IOException) { return Unreadable; }
        catch (UnauthorizedAccessException) { return Unreadable; }

        if (raw.Length < AttrWordLen + MinDataLen)
            return Fail("CBS variable shorter than expected");

        var data = raw.AsSpan(AttrWordLen);
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != CbsMagic)
            return Fail("CBS magic mismatch (BIOS layout changed?)");

        var tdpCtl = ControlAt(data, TdpControlOff);
        var pptCtl = ControlAt(data, PptControlOff);
        var tjCtl = ControlAt(data, TjMaxControlOff);
        if (tdpCtl is null || pptCtl is null || tjCtl is null)
            return Fail("unrecognised CBS control byte (BIOS layout changed?)");

        // Only a Manual field's value is meaningful; validate those.
        if (tdpCtl == "manual" && !InRange(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(TdpOff)), MinTdpMw, MaxTdpMw))
            return Fail("TDP value outside plausible range");
        if (pptCtl == "manual" && !InRange(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(PptOff)), MinPptMw, MaxPptMw))
            return Fail("PPT value outside plausible range");
        if (tjCtl == "manual" && !InRange(BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(TjMaxOff)), MinTjMax, MaxTjMax))
            return Fail("TjMax value outside plausible range");

        return new Reading(
            true, null,
            tdpCtl == "manual" ? (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(TdpOff)) : null,
            pptCtl == "manual" ? (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(PptOff)) : null,
            tjCtl == "manual" ? (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(TjMaxOff)) : null,
            tdpCtl, pptCtl, tjCtl);
    }

    /// <summary>1 = Manual, 0 = Auto/fused. Anything else means the layout is not what this
    /// build was compiled against, so nothing here can be trusted.</summary>
    private static string? ControlAt(ReadOnlySpan<byte> data, int offset) =>
        data[offset] switch { 1 => "manual", 0 => "auto", _ => null };

    private static bool InRange(uint value, uint min, uint max) => value >= min && value <= max;

    private static Reading Fail(string reason) => new(false, reason, null, null, null, null, null, null);
}
