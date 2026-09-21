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

    // v3 carries the full 32-char busId; struct includes 'function' (nvml.h layout).
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
        public uint function;
        public uint pciDeviceId;
        public uint pciSubSystemId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string busId;
    }

    /// <summary>nvmlFanSpeedInfo_t: speed is unsigned long long (the Windows port's uint was wrong).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct FanSpeedInfo
    {
        public uint version;
        public uint fan;
        public ulong speedRPM;
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
public sealed class NvmlBackend : ISensorBackend, IFanActuator
{
    public const string BackendName = "nvml";

    private readonly List<Gpu> _gpus = [];

    public string Name => BackendName;
    public bool Available { get; }
    public string DriverVersion { get; private set; } = "";

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

    public void ReadInto(IDictionary<string, double?> readings)
    {
        foreach (var g in _gpus)
        {
            if (NvmlNative.nvmlDeviceGetTemperature(g.Handle, NvmlNative.TempGpu, out var t) == NvmlNative.Success)
                readings[$"nvml:{g.Uuid}:temp:core"] = t;
            else
                readings[$"nvml:{g.Uuid}:temp:core"] = null;

            for (uint f = 0; f < g.FanCount; f++)
            {
                if (NvmlNative.nvmlDeviceGetFanSpeed_v2(g.Handle, f, out var speed) == NvmlNative.Success)
                    readings[$"nvml:{g.Uuid}:fan:{f}"] = speed;
                else
                    readings[$"nvml:{g.Uuid}:fan:{f}"] = null;

                double? rpm = null;
                var info = new NvmlNative.FanSpeedInfo { version = NvmlNative.FanSpeedInfoV1, fan = f };
                if (NvmlNative.nvmlDeviceGetFanSpeedRPM(g.Handle, ref info) == NvmlNative.Success)
                    rpm = info.speedRPM;
                readings[$"nvml:{g.Uuid}:tach:{f}"] = rpm;
            }
        }
    }

    public bool SetPercent(string controlId, int percent)
    {
        if (!TryParseFan(controlId, out var gpu, out var fan))
            return false;
        var clamped = Math.Clamp(percent, gpu.MinPercent, 100);
        return NvmlNative.nvmlDeviceSetFanSpeed_v2(gpu.Handle, fan, (uint)clamped) == NvmlNative.Success;
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
        return $"{pci.bus:X2}:{pci.device:X2}.{pci.function}";
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
