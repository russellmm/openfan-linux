using System.Runtime.InteropServices;
using System.Text;
using OpenFan.Core.ControlLoop;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

/// <summary>
/// NVML bindings for Linux (spec §4.2): same per-fan model as Windows, but
/// libnvidia-ml.so.1 and the correct nvmlPciInfo_t / nvmlFanSpeedInfo_t layouts.
/// No driver-model APIs — WDDM/TCC/MCDM do not exist on the Linux driver.
/// </summary>
internal static class NvmlNative
{
    private const string Dll = "libnvidia-ml.so.1";

    public const int Success = 0;
    public const int ErrorNoPermission = 4;
    public const int TempGpu = 0;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlInit_v2();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlShutdown();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetCount_v2(out uint deviceCount);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out nint device);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetName(nint device, [Out] byte[] name, uint length);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetUUID(nint device, [Out] byte[] uuid, uint length);

    // v3 carries the full 32-char busId; nvmlPciInfo_t has no separate function field.
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl, EntryPoint = "nvmlDeviceGetPciInfo_v3")]
    public static extern int nvmlDeviceGetPciInfo(nint device, ref PciInfo pci);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetTemperature(nint device, int sensorType, out uint temp);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetNumFans(nint device, out uint numFans);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetFanSpeed_v2(nint device, uint fan, out uint speed);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetFanSpeedRPM(nint device, ref FanSpeedInfo fanSpeed);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceSetFanSpeed_v2(nint device, uint fan, uint speed);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceSetDefaultFanSpeed_v2(nint device, uint fan);

    // ---- GPU page telemetry (verified against driver 595 via ctypes probe) ----

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetPerformanceState(nint device, out uint state);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetUtilizationRates(nint device, ref Utilization rates);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetMemoryInfo(nint device, ref MemoryUsage memory);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetPowerUsage(nint device, out uint milliWatts);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetPowerManagementLimit(nint device, out uint milliWatts);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetPowerManagementLimitConstraints(nint device, out uint minMW, out uint maxMW);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceSetPowerManagementLimit(nint device, uint milliWatts);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetClockInfo(nint device, uint clockType, out uint MHz);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetCurrPcieLinkGeneration(nint device, out uint gen);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetCurrPcieLinkWidth(nint device, out uint width);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetPcieThroughput(nint device, uint counter, out uint kiloBytesPerSec);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetGraphicsRunningProcesses_v3(nint device, ref uint count, nint infos);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetComputeRunningProcesses_v3(nint device, ref uint count, nint infos);

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryUsage
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    // nvmlProcessInfo_v2/v3 layout (verified): pid, pad, usedGpuMemory, gpuInstanceId, computeInstanceId.
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInfo
    {
        public uint Pid;
        public uint Pad;
        public ulong UsedGpuMemory;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlDeviceGetMinMaxFanSpeed(nint device, out uint minSpeed, out uint maxSpeed);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    public static extern int nvmlSystemGetDriverVersion([Out] byte[] version, uint length);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct PciInfo
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string busIdLegacy;
        public uint domain;
        public uint bus;
        public uint device;
        public uint pciDeviceId;
        public uint pciSubSystemId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string busId;
    }

    /// <summary>nvmlFanSpeedInfo_v1_t: three unsigned ints; size is part of the version token.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FanSpeedInfo
    {
        public uint version;
        public uint fan;
        public uint speedRPM;
    }

    public static readonly uint FanSpeedInfoV1 =
        (uint)Marshal.SizeOf<FanSpeedInfo>() | (1u << 24);

    public static string ReadString(Func<byte[], uint, int> reader, int size = 96)
    {
        var buf = new byte[size];
        if (reader(buf, (uint)buf.Length) != Success)
            return "";
        var end = Array.IndexOf(buf, (byte)0);
        if (end < 0)
            end = buf.Length;
        return Encoding.UTF8.GetString(buf, 0, end);
    }
}

/// <summary>
/// NVIDIA GPU fans + core temp via NVML. Ids: nvml:{uuid}:fan|tach:{n}, nvml:{uuid}:temp:core —
/// identical to Windows so GPU curves could round-trip later (spec §5).
/// </summary>
public sealed class NvmlBackend : ISensorBackend, IFanActuator, IGpuPowerWriter
{
    public const string BackendName = "nvml";

    private readonly List<Gpu> _gpus = [];

    public string Name => BackendName;
    public bool Available { get; }
    public string DriverVersion { get; private set; } = "";

    /// <summary>Reason the last SetPercent failed, mapped from NVML codes — null when healthy.</summary>
    public string? LastWriteError { get; private set; }

    private sealed class Gpu
    {
        public required int Index;
        public required nint Handle;
        public required string Uuid;
        public required string Name;
        public required string PciBus;
        public required int FanCount;
        public required bool CanSet;
        public required int MinPercent;
    }

    public NvmlBackend()
    {
        try
        {
            if (NvmlNative.nvmlInit_v2() != NvmlNative.Success)
                return;
            Available = true;
            DriverVersion = NvmlNative.ReadString(NvmlNative.nvmlSystemGetDriverVersion, 80);

            if (NvmlNative.nvmlDeviceGetCount_v2(out var count) != NvmlNative.Success)
                return;
            for (uint i = 0; i < count; i++)
            {
                if (NvmlNative.nvmlDeviceGetHandleByIndex_v2(i, out var h) != NvmlNative.Success)
                    continue;
                var name = NvmlNative.ReadString((b, n) => NvmlNative.nvmlDeviceGetName(h, b, n));
                var uuid = NvmlNative.ReadString((b, n) => NvmlNative.nvmlDeviceGetUUID(h, b, n), 96);
                if (string.IsNullOrWhiteSpace(uuid))
                    uuid = $"index-{i}";
                var fans = 0;
                if (NvmlNative.nvmlDeviceGetNumFans(h, out var nFans) == NvmlNative.Success)
                    fans = (int)nFans;
                var canSet = fans > 0;
                var pci = ReadPciBus(h);
                // PRO 6000 keeps the Windows 30 % floor; NVML's own min agrees on driver 595.
                var min = name.Contains("PRO 6000", StringComparison.OrdinalIgnoreCase) ? 30 : 0;
                _gpus.Add(new Gpu
                {
                    Index = (int)i,
                    Handle = h,
                    Uuid = uuid,
                    Name = string.IsNullOrWhiteSpace(name) ? $"GPU {i}" : name,
                    PciBus = pci,
                    FanCount = Math.Max(fans, 0),
                    CanSet = canSet,
                    MinPercent = min,
                });
            }
        }
        catch (DllNotFoundException)
        {
            Available = false;
        }
        catch (EntryPointNotFoundException)
        {
            Available = false;
        }
    }

    private static readonly (string Metric, string Title, HardwareKind Kind, string Unit)[] TelemetryChannels =
    [
        ("load", "GPU load", HardwareKind.Load, "%"),
        ("memory-load", "Memory controller load", HardwareKind.Load, "%"),
        ("power", "Board power", HardwareKind.Power, "W"),
        ("power-limit", "Board power limit", HardwareKind.Power, "W"),
        ("graphics-clock", "Graphics clock", HardwareKind.Frequency, "MHz"),
        ("sm-clock", "SM clock", HardwareKind.Frequency, "MHz"),
        ("memory-clock", "Memory clock", HardwareKind.Frequency, "MHz"),
        ("memory-used", "VRAM used", HardwareKind.Data, "GiB"),
        ("memory-total", "VRAM total", HardwareKind.Data, "GiB"),
    ];
    private IReadOnlyDictionary<string, double?> _lastTelemetry = new Dictionary<string, double?>();

    public IReadOnlyList<HardwareItem> Discover()
    {
        var list = new List<HardwareItem>();
        foreach (var g in _gpus)
        {
            var label = GpuFormat.GpuLabel(g.Name, g.PciBus);
            list.Add(new HardwareItem
            {
                Id = $"nvml:{g.Uuid}:temp:core",
                Kind = HardwareKind.Temperature,
                Name = $"{label} Core",
                Backend = BackendName,
                Group = label,
                CanSet = false,
            });
            foreach (var (metric, title, kind, unit) in TelemetryChannels)
                list.Add(new HardwareItem
                {
                    Id = $"nvml:{g.Uuid}:metric:{metric}", Kind = kind, Name = $"{label} {title}",
                    Backend = BackendName, Group = label, Unit = unit, CanSet = false, Source = "NVML",
                    Details = "NVIDIA NVML · " + title,
                });
            for (var f = 0; f < g.FanCount; f++)
            {
                list.Add(new HardwareItem
                {
                    Id = $"nvml:{g.Uuid}:fan:{f}",
                    Kind = HardwareKind.Control,
                    Name = $"{label} Fan {f}",
                    Backend = BackendName,
                    Group = label,
                    MinPercent = g.MinPercent,
                    CanSet = g.CanSet,
                });
                list.Add(new HardwareItem
                {
                    Id = $"nvml:{g.Uuid}:tach:{f}",
                    Kind = HardwareKind.Tach,
                    Name = $"{label} Fan {f} RPM",
                    Backend = BackendName,
                    Group = label,
                    CanSet = false,
                });
            }
        }
        return list;
    }

    public void ReadInto(IDictionary<string, double?> readings) => ReadInto(readings, null);
    public void ReadInto(IDictionary<string, double?> readings, IReadOnlySet<string>? requested)
    {
        var telemetry = new Dictionary<string, double?>();
        foreach (var g in _gpus)
        {
            var prefix = $"nvml:{g.Uuid}:";
            bool Wants(string suffix) => requested is null || requested.Contains(prefix + suffix);
            void Metric(string key, double? value)
            {
                if (!Wants("metric:" + key)) return;
                readings[prefix + "metric:" + key] = value;
                telemetry[$"{g.Uuid}:{key}"] = value;
            }
            void Query(string key, Func<double?> read)
            {
                if (Wants("metric:" + key)) Metric(key, read());
            }
            if (Wants("temp:core"))
                readings[prefix + "temp:core"] = NvmlNative.nvmlDeviceGetTemperature(g.Handle, NvmlNative.TempGpu, out var t) == NvmlNative.Success ? t : null;
            if (Wants("metric:load") || Wants("metric:memory-load"))
            {
                var util = new NvmlNative.Utilization();
                var ok = NvmlNative.nvmlDeviceGetUtilizationRates(g.Handle, ref util) == NvmlNative.Success;
                Metric("load", ok ? util.Gpu : null); Metric("memory-load", ok ? util.Memory : null);
            }
            if (Wants("metric:memory-used") || Wants("metric:memory-total"))
            {
                var memory = new NvmlNative.MemoryUsage();
                var ok = NvmlNative.nvmlDeviceGetMemoryInfo(g.Handle, ref memory) == NvmlNative.Success;
                Metric("memory-used", ok ? memory.Used / 1073741824.0 : null);
                Metric("memory-total", ok ? memory.Total / 1073741824.0 : null);
            }
            Query("power", () => NvmlNative.nvmlDeviceGetPowerUsage(g.Handle, out var v) == NvmlNative.Success ? v / 1000.0 : null);
            Query("power-limit", () => NvmlNative.nvmlDeviceGetPowerManagementLimit(g.Handle, out var v) == NvmlNative.Success ? v / 1000.0 : null);
            Query("graphics-clock", () => NvmlNative.nvmlDeviceGetClockInfo(g.Handle, 0, out var v) == NvmlNative.Success ? v : null);
            Query("sm-clock", () => NvmlNative.nvmlDeviceGetClockInfo(g.Handle, 1, out var v) == NvmlNative.Success ? v : null);
            Query("memory-clock", () => NvmlNative.nvmlDeviceGetClockInfo(g.Handle, 2, out var v) == NvmlNative.Success ? v : null);
            for (uint f = 0; f < g.FanCount; f++)
            {
                if (Wants($"fan:{f}"))
                    readings[prefix + $"fan:{f}"] = NvmlNative.nvmlDeviceGetFanSpeed_v2(g.Handle, f, out var speed) == NvmlNative.Success ? speed : null;
                if (Wants($"tach:{f}"))
                {
                    var info = new NvmlNative.FanSpeedInfo { version = NvmlNative.FanSpeedInfoV1, fan = f };
                    readings[prefix + $"tach:{f}"] = NvmlNative.nvmlDeviceGetFanSpeedRPM(g.Handle, ref info) == NvmlNative.Success ? info.speedRPM : null;
                }
            }
        }
        Volatile.Write(ref _lastTelemetry, telemetry);
    }

    // ---- GPU page snapshots + power limit (helper-side write) ----------------

    public sealed record GpuProcessInfo(string Name, int Pid, bool Compute, ulong MemBytes);

    public sealed record GpuSnapshot(
        int Index, string Uuid, string Name, string PciBus,
        double? TempC, double? UtilGpuPct, double? MemUsed, double? MemTotal,
        double? PowerDrawW, double? PowerLimitW, double? PowerMinW, double? PowerMaxW, int? PState,
        double? FanPct, double? FanRpm, int? ClockG, int? ClockS, int? ClockM,
        int? PcieGen, int? PcieWidth, double? RxKBs, double? TxKBs,
        IReadOnlyList<GpuProcessInfo> Processes);

    public IReadOnlyList<GpuSnapshot> SnapshotAll()
    {
        var list = new List<GpuSnapshot>();
        if (!Available)
            return list;

        var telemetry = Volatile.Read(ref _lastTelemetry);
        foreach (var g in _gpus)
        {
            double? temp = NvmlNative.nvmlDeviceGetTemperature(g.Handle, NvmlNative.TempGpu, out var t) == NvmlNative.Success ? t : null;

            double? Cached(string metric) => telemetry.GetValueOrDefault($"{g.Uuid}:{metric}");
            double? utilGpu = Cached("load");
            ulong? memUsed = Cached("memory-used") is { } usedGiB ? (ulong)(usedGiB * 1073741824.0) : null;
            ulong? memTotal = Cached("memory-total") is { } totalGiB ? (ulong)(totalGiB * 1073741824.0) : null;
            double? drawW = Cached("power"); double? limitW = Cached("power-limit");
            double? minW = null, maxW = null;
            if (NvmlNative.nvmlDeviceGetPowerManagementLimitConstraints(g.Handle, out var cMin, out var cMax) == NvmlNative.Success)
            {
                minW = cMin / 1000.0; maxW = cMax > 0 ? cMax / 1000.0 : null;
            }

            int? pstate = NvmlNative.nvmlDeviceGetPerformanceState(g.Handle, out var ps) == NvmlNative.Success ? (int)ps : null;

            double? fanPct = g.FanCount > 0 && NvmlNative.nvmlDeviceGetFanSpeed_v2(g.Handle, 0, out var fs) == NvmlNative.Success ? fs : null;
            double? fanRpm = null;
            if (g.FanCount > 0)
            {
                var info = new NvmlNative.FanSpeedInfo { version = NvmlNative.FanSpeedInfoV1, fan = 0 };
                if (NvmlNative.nvmlDeviceGetFanSpeedRPM(g.Handle, ref info) == NvmlNative.Success)
                    fanRpm = info.speedRPM;
            }

            int? clk(int type) => Cached(type switch { 0 => "graphics-clock", 1 => "sm-clock", _ => "memory-clock" }) is { } clock ? (int)clock : null;

            int? gen = NvmlNative.nvmlDeviceGetCurrPcieLinkGeneration(g.Handle, out var gn) == NvmlNative.Success ? (int)gn : null;
            int? width = NvmlNative.nvmlDeviceGetCurrPcieLinkWidth(g.Handle, out var wd) == NvmlNative.Success ? (int)wd : null;
            double? rx = NvmlNative.nvmlDeviceGetPcieThroughput(g.Handle, 1, out var r) == NvmlNative.Success ? r : null;
            double? tx = NvmlNative.nvmlDeviceGetPcieThroughput(g.Handle, 0, out var x) == NvmlNative.Success ? x : null;

            list.Add(new GpuSnapshot(g.Index, g.Uuid, g.Name, g.PciBus,
                temp, utilGpu, memUsed, memTotal, drawW, limitW, minW, maxW, pstate,
                fanPct, fanRpm, clk(0), clk(1), clk(2), gen, width, rx, tx,
                ReadProcesses(g.Handle)));
        }
        return list;
    }

    private static IReadOnlyList<GpuProcessInfo> ReadProcesses(nint handle)
    {
        var procs = new List<GpuProcessInfo>();
        Collect(false);
        Collect(true);
        return procs;

        void Collect(bool compute)
        {
            uint n = 0;
            var rc = compute
                ? NvmlNative.nvmlDeviceGetComputeRunningProcesses_v3(handle, ref n, 0)
                : NvmlNative.nvmlDeviceGetGraphicsRunningProcesses_v3(handle, ref n, 0);
            if (rc is not (NvmlNative.Success or 7) || n == 0) // 7 = INSUFFICIENT_SIZE (count probe)
                return;

            // Pinned buffer, passed as a raw pointer: the default array marshaller hands NVML a copy.
            var arr = new NvmlNative.ProcessInfo[n];
            var gc = GCHandle.Alloc(arr, GCHandleType.Pinned);
            try
            {
                rc = compute
                    ? NvmlNative.nvmlDeviceGetComputeRunningProcesses_v3(handle, ref n, gc.AddrOfPinnedObject())
                    : NvmlNative.nvmlDeviceGetGraphicsRunningProcesses_v3(handle, ref n, gc.AddrOfPinnedObject());
            }
            finally
            {
                gc.Free();
            }

            if (rc != NvmlNative.Success)
                return;
            foreach (var pi in arr[..(int)n])
            {
                var name = TryReadComm((int)pi.Pid);
                procs.Add(new GpuProcessInfo(name, (int)pi.Pid, compute, pi.UsedGpuMemory));
            }
        }

        static string TryReadComm(int pid)
        {
            try
            {
                return File.ReadAllText($"/proc/{pid}/comm").Trim();
            }
            catch (IOException)
            {
                return "?";
            }
        }
    }

    /// <summary>Root/helper-side only in practice: NVML refuses power-limit writes from user sessions.</summary>
    public bool SetPowerLimit(string uuid, int watts)
    {
        var g = _gpus.FirstOrDefault(x => x.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));
        if (g is null || watts <= 0)
            return false;
        var rc = NvmlNative.nvmlDeviceSetPowerManagementLimit(g.Handle, (uint)(watts * 1000));
        if (rc != NvmlNative.Success)
        {
            LastWriteError = rc switch
            {
                NvmlNative.ErrorNoPermission => "power limit needs root — run through openfan-helper",
                _ => $"NVML rejected power limit ({rc}) — check constraints",
            };
            return false;
        }
        LastWriteError = null;
        return true;
    }

    public bool SetPercent(string controlId, int percent)
    {
        if (!TryParseFan(controlId, out var gpu, out var fan))
            return false;
        var clamped = Math.Clamp(percent, gpu.MinPercent, 100);
        var code = NvmlNative.nvmlDeviceSetFanSpeed_v2(gpu.Handle, fan, (uint)clamped);
        if (code == NvmlNative.Success)
            LastWriteError = null;
        else
            LastWriteError = code switch
            {
                NvmlNative.ErrorNoPermission => "GPU writes need root — privileged helper not installed",
                _ => $"NVML error {code}",
            };
        return code == NvmlNative.Success;
    }

    public bool SetDefault(string controlId)
    {
        if (!TryParseFan(controlId, out var gpu, out var fan))
            return false;
        return NvmlNative.nvmlDeviceSetDefaultFanSpeed_v2(gpu.Handle, fan) == NvmlNative.Success;
    }

    private bool TryParseFan(string id, out Gpu gpu, out uint fan)
    {
        gpu = null!;
        fan = 0;
        // nvml:{uuid}:fan:{index}
        const string prefix = "nvml:";
        const string mid = ":fan:";
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        var i = id.LastIndexOf(mid, StringComparison.OrdinalIgnoreCase);
        if (i < 0)
            return false;
        var uuid = id[prefix.Length..i];
        if (!uint.TryParse(id[(i + mid.Length)..], out fan))
            return false;
        var found = _gpus.FirstOrDefault(g => g.Uuid.Equals(uuid, StringComparison.OrdinalIgnoreCase));
        if (found is null)
            return false;
        gpu = found;
        return true;
    }

    private static string ReadPciBus(nint handle)
    {
        var pci = new NvmlNative.PciInfo();
        if (NvmlNative.nvmlDeviceGetPciInfo(handle, ref pci) != NvmlNative.Success)
            return "";
        if (!string.IsNullOrWhiteSpace(pci.busId))
            return pci.busId;
        if (!string.IsNullOrWhiteSpace(pci.busIdLegacy))
            return pci.busIdLegacy;
        return $"{pci.bus:X2}:{pci.device:X2}.0";
    }

    public void Dispose()
    {
        if (!Available)
            return;
        try
        {
            NvmlNative.nvmlShutdown();
        }
        catch
        {
            // ignore — shutdown failures are not actionable at exit
        }
    }
}
