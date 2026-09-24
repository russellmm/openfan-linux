using System.Buffers.Binary;
using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public sealed class CbsSetupReaderTests
{
    // Mirrors the live AmdSetupSHP layout: 4-byte attribute word, then CBS data whose first
    // u32 is the magic, with control byte + u32 value pairs at the documented offsets.
    private const int TdpControlOff = 1043, TdpOff = 1044;
    private const int PptControlOff = 1048, PptOff = 1049;
    private const int TjMaxControlOff = 1053, TjMaxOff = 1054;

    private static byte[] Blob(byte tdpCtl = 1, uint tdp = 245_000, byte pptCtl = 1, uint ppt = 295_000,
        byte tjCtl = 1, uint tj = 80, uint magic = 0xE5AF127C, int dataLen = 2438)
    {
        var data = new byte[Math.Max(dataLen, TjMaxOff + 4)];   // fields must be writable
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), magic);
        data[TdpControlOff] = tdpCtl;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TdpOff), tdp);
        data[PptControlOff] = pptCtl;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(PptOff), ppt);
        data[TjMaxControlOff] = tjCtl;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(TjMaxOff), tj);

        var file = new byte[4 + dataLen];                        // then truncate to size
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0), 0x0000_0007); // NV|BS|RT
        data.AsSpan(0, Math.Min(data.Length, dataLen)).CopyTo(file.AsSpan(4));
        return file;
    }

    private static string TempVarPath() =>
        Path.Combine(Path.GetTempPath(), $"openfan-cbs-{Guid.NewGuid():N}");

    [Fact]
    public void ReadsManualFieldsFromDocumentedOffsets()
    {
        var path = TempVarPath();
        File.WriteAllBytes(path, Blob());
        try
        {
            var r = new CbsSetupReader(path).Read();
            Assert.True(r.Ok);
            Assert.Null(r.Reason);
            Assert.Equal(245_000, r.TdpMw);
            Assert.Equal(295_000, r.PptBiosMw);
            Assert.Equal(80, r.TjMaxCelsius);
            Assert.Equal("manual", r.TdpControl);
            Assert.Equal("manual", r.PptControl);
            Assert.Equal("manual", r.TjMaxControl);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AutoFieldsReportNullInsteadOfAConfidentZero()
    {
        // Factory defaults are control=0 with a zero value field: the effective limit comes from
        // fuses and is not exposed to the OS, so a number here would be a lie.
        var path = TempVarPath();
        File.WriteAllBytes(path, Blob(tdpCtl: 0, tdp: 0, tjCtl: 0, tj: 0));
        try
        {
            var r = new CbsSetupReader(path).Read();
            Assert.True(r.Ok);                      // the read itself succeeded
            Assert.Null(r.TdpMw);
            Assert.Null(r.TjMaxCelsius);
            Assert.Equal("auto", r.TdpControl);     // ...and is distinguishable from unknown
            Assert.Equal("auto", r.TjMaxControl);
            Assert.Equal(295_000, r.PptBiosMw);     // still-manual field unaffected
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void MissingVariableIsNotAnInvention()
    {
        var r = new CbsSetupReader(TempVarPath()).Read();   // never created
        Assert.False(r.Ok);
        Assert.All(new int?[] { r.TdpMw, r.PptBiosMw, r.TjMaxCelsius }, v => Assert.Null(v));
        Assert.False(string.IsNullOrEmpty(r.Reason));
    }

    [Fact]
    public void ShortOrBadMagicBlobReportsNothing()
    {
        var shortPath = TempVarPath();
        File.WriteAllBytes(shortPath, Blob(dataLen: 900));   // shorter than the field table
        var badMagic = TempVarPath();
        File.WriteAllBytes(badMagic, Blob(magic: 0xDEADBEEF));
        try
        {
            foreach (var path in new[] { shortPath, badMagic })
            {
                var r = new CbsSetupReader(path).Read();
                Assert.False(r.Ok);
                Assert.All(new int?[] { r.TdpMw, r.PptBiosMw, r.TjMaxCelsius }, v => Assert.Null(v));
                Assert.False(string.IsNullOrEmpty(r.Reason));
            }
        }
        finally { File.Delete(shortPath); File.Delete(badMagic); }
    }

    [Fact]
    public void ImplausibleValueOrUnknownControlByteIsTreatedAsLayoutChange()
    {
        // A shifted offset would produce garbage that looks numeric; it must not be reported.
        var paths = new[]
        {
            TempVarPath(), TempVarPath(), TempVarPath(),
        };
        File.WriteAllBytes(paths[0], Blob(tdp: 9_000_000));          // 9 MW: impossible
        File.WriteAllBytes(paths[1], Blob(tj: 4000));                // 4000 C
        File.WriteAllBytes(paths[2], Blob(pptCtl: 7));               // control byte not 0/1
        try
        {
            foreach (var path in paths)
            {
                var r = new CbsSetupReader(path).Read();
                Assert.False(r.Ok);
                Assert.All(new int?[] { r.TdpMw, r.PptBiosMw, r.TjMaxCelsius }, v => Assert.Null(v));
            }
        }
        finally { foreach (var p in paths) File.Delete(p); }
    }
}

public sealed class HsmpLimitConfigTests
{
    private static string TempCfg() =>
        Path.Combine(Path.GetTempPath(), $"openfan-ppt-{Guid.NewGuid():N}.conf");

    [Theory]
    [InlineData("290000\n", 290_000)]
    [InlineData("# desired PPT\n\n  285000  \n", 285_000)]
    [InlineData("", null)]                    // empty file: nothing configured
    [InlineData("# only a comment\n", null)]
    [InlineData("lots of watts", null)]       // malformed is never guessed
    [InlineData("-5", null)]
    public void ParsesConfigOrReportsNull(string content, int? expected)
    {
        var path = TempCfg();
        File.WriteAllText(path, content);
        try { Assert.Equal(expected, new HsmpLimitConfig(path).ReadDesiredMw()); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AbsentConfigMeansNoOverrideNotAnError() =>
        Assert.Null(new HsmpLimitConfig(TempCfg()).ReadDesiredMw());
}
