using OpenFan.Core.Hardware;
using OpenFan.Linux.Hw;
using System.Runtime.InteropServices;

namespace OpenFan.Core.Tests;

public sealed class SensorSamplingTests
{
    [Fact]
    public void StatisticsTrackValidSamplesAndRetainHistoryWhenCurrentIsMissing()
    {
        var stats = new SensorStatistics();
        stats.Record(new Dictionary<string, double?> { ["temp"] = 20 });
        stats.Record(new Dictionary<string, double?> { ["temp"] = 40 });
        stats.Record(new Dictionary<string, double?> { ["temp"] = null });
        var value = stats.Get("temp");
        Assert.Null(value.Current); Assert.Equal(20, value.Minimum); Assert.Equal(40, value.Maximum);
        Assert.Equal(30, value.Average); Assert.Equal(2, value.Samples);
        // Repainting/access does not add samples.
        Assert.Equal(value, stats.Get("temp"));
    }

    [Fact]
    public void StatisticsKeepZerosRejectNonFiniteAndPruneRemovedSensors()
    {
        var stats = new SensorStatistics();
        stats.Record(new Dictionary<string, double?> { ["fan"] = 0, ["bad"] = double.NaN, ["infinite"] = double.PositiveInfinity });
        Assert.Equal(0, stats.Get("fan").Current); Assert.Equal(1, stats.Get("fan").Samples);
        Assert.Null(stats.Get("bad").Current); Assert.Equal(0, stats.Get("bad").Samples);
        Assert.Null(stats.Get("infinite").Current);
        stats.Record(new Dictionary<string, double?> { ["fan"] = 1200 });
        Assert.Equal(600, stats.Get("fan").Average); Assert.Equal(default, stats.Get("bad"));
        stats.Reset(); Assert.Equal(default, stats.Get("fan"));
    }

    [Fact]
    public void InventoryCadenceKeepsSamplingIndependentAndSupportsImmediateRefresh()
    {
        var cadence = new InventoryRefreshCadence(TimeSpan.FromSeconds(5));
        Assert.True(cadence.IsDue(TimeSpan.Zero));
        Assert.False(cadence.IsDue(TimeSpan.FromSeconds(1)));
        Assert.False(cadence.IsDue(TimeSpan.FromMilliseconds(4999)));
        Assert.True(cadence.IsDue(TimeSpan.FromSeconds(5)));
        Assert.True(cadence.IsDue(TimeSpan.FromSeconds(6), force: true));
        Assert.False(cadence.IsDue(TimeSpan.FromSeconds(10)));
        Assert.True(cadence.IsDue(TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public void NvmlUtilizationMatchesNativeUnsignedIntLayout()
    {
        var native = typeof(NvmlBackend).Assembly.GetType("OpenFan.Linux.Hw.NvmlNative+Utilization")!;
        Assert.Equal(8, Marshal.SizeOf(native));
        Assert.Equal(typeof(uint), native.GetField("Gpu")!.FieldType);
        Assert.Equal(typeof(uint), native.GetField("Memory")!.FieldType);
        Assert.Equal((IntPtr)4, Marshal.OffsetOf(native, "Memory"));
    }

    [Fact]
    public void NvmlFanAndPciStructuresMatchNvidiaHeader()
    {
        var assembly = typeof(NvmlBackend).Assembly;
        var fan = assembly.GetType("OpenFan.Linux.Hw.NvmlNative+FanSpeedInfo")!;
        Assert.Equal(12, Marshal.SizeOf(fan));
        Assert.Equal(typeof(uint), fan.GetField("speedRPM")!.FieldType);
        Assert.Equal((IntPtr)8, Marshal.OffsetOf(fan, "speedRPM"));
        var pci = assembly.GetType("OpenFan.Linux.Hw.NvmlNative+PciInfo")!;
        Assert.Equal(68, Marshal.SizeOf(pci));
        Assert.Equal((IntPtr)36, Marshal.OffsetOf(pci, "busId"));
    }
}
