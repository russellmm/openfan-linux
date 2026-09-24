using System.Runtime.InteropServices;
using OpenFan.Linux.Hw;

// openfan-helper — root daemon owning NVML GPU fan writes (spec §3.4).
// The GUI/CLI connect over /run/openfan/helper.sock (root:openfan 0660) and send
// line commands; every connection's fans are restored to default when it disconnects,
// so a crashed or killed client cannot leave GPU fans pinned.

if (Native.geteuid() != 0)
{
    Console.Error.WriteLine("openfan-helper must run as root (systemd unit: openfan-helper.service).");
    return 1;
}

var nvml = new NvmlBackend();
// CPU socket power control works without any NVIDIA driver, so NVML absence alone is not a
// reason to refuse to start — but with neither capability there is nothing to serve.
var cpu = new CpuPowerControl();
var cpuAvailable = HsmpPowerReader.FindHsmpChipDirectory("/sys/class/hwmon") is not null;
if (!nvml.Available && !cpuAvailable)
{
    Console.Error.WriteLine("NVML unavailable and no amd_hsmp_hwmon chip — helper has no job; exiting.");
    return 2;
}

var socketPath = Environment.GetEnvironmentVariable("OPENFAN_HELPER_SOCKET") ?? "/run/openfan/helper.sock";
var verbose = Environment.GetEnvironmentVariable("OPENFAN_HELPER_VERBOSE") == "1";

var server = new HelperServer(nvml, socketPath, nvml, cpu);
server.Start();
server.ApplyGroupAccess("openfan");

Console.WriteLine($"openfan-helper listening on {socketPath}" +
                  $" (NVML {(nvml.Available ? $"driver {nvml.DriverVersion}" : "unavailable")}," +
                  $" CPU PPT {(cpuAvailable ? "available" : "unavailable")})");

var stopping = new ManualResetEventSlim();
using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
{
    ctx.Cancel = true;
    Console.WriteLine("SIGTERM — restoring owned fans and shutting down.");
    stopping.Set();
});
using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
{
    ctx.Cancel = true;
    stopping.Set();
});

stopping.Wait();

// DisposeAsync drains every session, each restoring its owned controls (safety net).
await using (server)
{
}
nvml.Dispose();
return 0;

internal static class Native
{
    [DllImport("libc")]
    public static extern uint geteuid();
}
