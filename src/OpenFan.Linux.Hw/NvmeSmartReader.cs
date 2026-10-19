using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace OpenFan.Linux.Hw;

public interface INvmeSmartReader : IDisposable
{
    byte[] Read(string controller, string identity);
}

/// <summary>Root-only fixed SMART log read. No passthrough opcode, address, namespace or path supplied by clients.</summary>
public sealed class NativeNvmeSmartReader : INvmeSmartReader
{
    public byte[] Read(string controller, string identity)
    {
        if (!Regex.IsMatch(controller, @"^nvme[0-9]{1,8}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(identity, @"^[0-9a-f]{64}$", RegexOptions.CultureInvariant)) throw new IOException("Invalid NVMe identity");
        var device = NvmeDevices.Discover().FirstOrDefault(d => d.Controller == controller && d.Identity == identity)
            ?? throw new IOException("NVMe drive changed or disconnected");
        // O_RDONLY | O_CLOEXEC | O_NOFOLLOW: do not follow a substituted device-node symlink.
        var fd = Native.open("/dev/" + device.Controller, 0x80000 | 0x20000);
        if (fd < 0) throw new IOException("Cannot open NVMe controller (" + Marshal.GetLastPInvokeError() + ")");
        var buffer = Marshal.AllocHGlobal(512);
        try
        {
            Marshal.Copy(new byte[512], 0, buffer, 512);
            var cmd = new Command { Opcode = 2, Nsid = uint.MaxValue, Address = (ulong)buffer, DataLength = 512,
                Cdw10 = (127u << 16) | 2u, TimeoutMs = 1000 };
            // Matches Linux's nvme_get_log_smart: RAE=0. This Samsung firmware rejects RAE=1.
            var result = Native.ioctl(fd, 0xc0484e41, ref cmd);
            if (result != 0) throw new IOException(result < 0 ? "NVMe read failed (" + Marshal.GetLastPInvokeError() + ")" : "NVMe status " + result);
            var data = new byte[512]; Marshal.Copy(buffer, data, 0, data.Length);
            if (data.All(b => b == 0)) throw new IOException("NVMe returned an empty health log");
            if (!NvmeDevices.Discover().Any(d => d.Controller == controller && d.Identity == identity))
                throw new IOException("NVMe drive changed during health read");
            return data;
        }
        finally { Marshal.FreeHGlobal(buffer); Native.close(fd); }
    }
    public void Dispose() { }

    [StructLayout(LayoutKind.Sequential)]
    public struct Command
    {
        public byte Opcode, Flags; public ushort Reserved;
        public uint Nsid, Cdw2, Cdw3;
        public ulong Metadata, Address;
        public uint MetadataLength, DataLength, Cdw10, Cdw11, Cdw12, Cdw13, Cdw14, Cdw15, TimeoutMs, Result;
    }
    private static class Native
    {
        [DllImport("libc", SetLastError = true)] public static extern int open(string path, int flags);
        [DllImport("libc", SetLastError = true)] public static extern int ioctl(int fd, ulong request, ref Command command);
        [DllImport("libc")] public static extern int close(int fd);
    }
}
