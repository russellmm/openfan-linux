using OpenFan.Linux.Hw;

namespace OpenFan.Core.Tests;

public sealed class HsmpPowerReaderTests
{
    [Fact]
    public void ReadsOnlyExactHsmpChipAndConvertsMicrowatts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openfan-hsmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var other = Directory.CreateDirectory(Path.Combine(root, "hwmon0")).FullName;
            File.WriteAllText(Path.Combine(other, "name"), "not_hsmp\n");
            File.WriteAllText(Path.Combine(other, "power1_input"), "999999999\n");
            var hsmp = Directory.CreateDirectory(Path.Combine(root, "hwmon1")).FullName;
            File.WriteAllText(Path.Combine(hsmp, "name"), "amd_hsmp_hwmon\n");
            File.WriteAllText(Path.Combine(hsmp, "power1_input"), "65088000\n");
            File.WriteAllText(Path.Combine(hsmp, "power1_cap"), "300000000\n");

            var result = new HsmpPowerReader(root).Read();
            Assert.NotNull(result);
            Assert.Equal("amd_hsmp_hwmon", result.Source);
            Assert.Equal(65.088, result.PowerW);
            Assert.Equal(300, result.PptCapW);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MissingOrMalformedFieldsNeverInventMeasurements()
    {
        var root = Path.Combine(Path.GetTempPath(), $"openfan-hsmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(new HsmpPowerReader(root).Read());
            var hsmp = Directory.CreateDirectory(Path.Combine(root, "hwmon7")).FullName;
            File.WriteAllText(Path.Combine(hsmp, "name"), "amd_hsmp_hwmon\n");
            File.WriteAllText(Path.Combine(hsmp, "power1_input"), "-42\n");
            var result = new HsmpPowerReader(root).Read();
            Assert.NotNull(result);
            Assert.Null(result.PowerW);
            Assert.Null(result.PptCapW);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
