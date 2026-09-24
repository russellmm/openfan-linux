using System.Globalization;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using OpenFan.Core.ControlLoop;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Server side of the privileged helper protocol (spec §3.4). Root daemon accepts
/// newline commands over a Unix socket whose file permissions restrict access to the
/// 'openfan' group; only NVML fan ids are accepted — hwmon goes through the udev ACL.
///
///   ping                    -> ok
///   set &lt;nvml-id&gt; &lt;pct&gt;  -> ok | err &lt;reason&gt;
///   default &lt;nvml-id&gt;     -> ok | err &lt;reason&gt;
///   power &lt;gpu-uuid&gt; &lt;W&gt;  -> ok | err &lt;reason&gt;
///   cpupower &lt;W&gt;|default   -> ok | err &lt;reason&gt;   (live PPT, 100-300 W, verified by readback)
///   cpuboot &lt;W&gt;|clear      -> ok | err &lt;reason&gt;   (keep-after-reboot override)
///   quit                    -> bye
///
/// Each connection owns what it set: on disconnect (clean or SIGKILL'd GUI) the
/// session restores those fans to NVML default so nothing stays pinned.
/// </summary>
/// <summary>Optional capability: apply GPU power limits (NvmlBackend implements this; the helper passes it through).</summary>
public interface IGpuPowerWriter
{
    bool SetPowerLimit(string uuid, int watts);
}

/// <summary>
/// Optional capability: CPU socket power limit (PPT). Implemented by
/// <see cref="CpuPowerControl"/>. Live limits are volatile SMU state; the boot limit is the file
/// hsmp-control-apply.service re-asserts, kept separate so "keep after reboot" is explicit.
/// </summary>
public interface ICpuPowerWriter
{
    bool SetLiveLimitWatts(int watts);
    bool RestoreBiosDefault();
    bool SetBootLimitWatts(int? watts);
    string? LastError { get; }
}

public sealed class HelperSession(IFanActuator actuator, IGpuPowerWriter? power = null, ICpuPowerWriter? cpu = null)
{
    private static readonly Regex NvmlFanId = new(
        @"^nvml:[A-Za-z0-9._-]+:fan:[0-9]+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex GpuUuid = new(
        @"^GPU-[A-Za-z0-9_-]{6,}$", RegexOptions.Compiled);

    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> Owned => _owned;

    public string HandleLine(string line)
    {
        var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return "err empty";

        switch (parts[0].ToLowerInvariant())
        {
            case "ping":
                return "ok";

            case "set" when parts.Length == 3:
                if (!NvmlFanId.IsMatch(parts[1]))
                    return "err only nvml fan ids are accepted";
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pct)
                    || pct is < 0 or > 100)
                    return "err percent must be 0-100";
                if (!actuator.SetPercent(parts[1], pct))
                    return "err set failed (see helper log / NVML state)";
                _owned.Add(parts[1]);
                return "ok";

            case "default" when parts.Length == 2:
                if (!NvmlFanId.IsMatch(parts[1]))
                    return "err only nvml fan ids are accepted";
                var ok = actuator.SetDefault(parts[1]);
                _owned.Remove(parts[1]);
                return ok ? "ok" : "err default failed";

            case "power" when parts.Length == 3:
                if (power is null)
                    return "err power control not available";
                if (!GpuUuid.IsMatch(parts[1]))
                    return "err expected a GPU uuid (GPU-...)";
                if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var watts)
                    || watts is < 1 or > 2000)
                    return "err watts must be 1-2000";
                return power.SetPowerLimit(parts[1], watts) ? "ok" : "err power limit rejected";

            // CPU socket power limit (PPT). Volatile SMU state: firmware re-programs it from
            // BIOS CBS at the next boot unless cpuboot keeps the override. TDP/TjMax are absent
            // on purpose — this firmware refuses OS writes to those while Secure Boot is enabled.
            case "cpupower" when parts.Length == 2:
                if (cpu is null)
                    return "err cpu power control not available";
                if (parts[1].Equals("default", StringComparison.OrdinalIgnoreCase))
                    return cpu.RestoreBiosDefault() ? "ok" : $"err {cpu.LastError ?? "restore failed"}";
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var cpuW)
                    || cpuW < CpuPowerControl.MinWatts || cpuW > CpuPowerControl.MaxWatts)
                    return $"err watts must be {CpuPowerControl.MinWatts}-{CpuPowerControl.MaxWatts}";
                return cpu.SetLiveLimitWatts(cpuW) ? "ok" : $"err {cpu.LastError ?? "power limit rejected"}";

            case "cpuboot" when parts.Length == 2:
                if (cpu is null)
                    return "err cpu power control not available";
                if (parts[1].Equals("clear", StringComparison.OrdinalIgnoreCase))
                    return cpu.SetBootLimitWatts(null) ? "ok" : $"err {cpu.LastError ?? "could not clear boot limit"}";
                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var bootW)
                    || bootW < CpuPowerControl.MinWatts || bootW > CpuPowerControl.MaxWatts)
                    return $"err watts must be {CpuPowerControl.MinWatts}-{CpuPowerControl.MaxWatts}";
                return cpu.SetBootLimitWatts(bootW) ? "ok" : $"err {cpu.LastError ?? "could not write boot limit"}";

            case "quit":
                return "bye";

            default:
                return "err unknown command";
        }
    }

    /// <summary>Client vanished mid-apply → give its fans back to the driver.</summary>
    public void RestoreOwned()
    {
        foreach (var id in _owned.ToArray())
            actuator.SetDefault(id);
        _owned.Clear();
    }
}

/// <summary>
/// Unix-socket accept loop hosting HelperSession instances. Lives in the library so
/// tests run it against a fake actuator; openfan-helper is a thin root wrapper.
/// </summary>
public sealed class HelperServer(IFanActuator actuator, string socketPath, IGpuPowerWriter? power = null,
    ICpuPowerWriter? cpu = null) : IAsyncDisposable
{
    private readonly Socket _listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _sessions = [];
    private readonly object _sessionsLock = new();

    public string SocketPath { get; } = socketPath;

    public void Start()
    {
        var dir = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        if (File.Exists(socketPath))
            File.Delete(socketPath); // stale socket from a crashed run

        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(8);

        _ = AcceptLoopAsync();
    }

    /// <summary>Owner/group bits for the socket: 0660 root:openfan (caller must be root).</summary>
    public void ApplyGroupAccess(string group)
    {
        File.SetUnixFileMode(socketPath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite |
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite);

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("chgrp")
            {
                ArgumentList = { group, socketPath },
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(2000);
        }
        catch
        {
            // chgrp failed (group missing?) — socket stays root-only, fail closed.
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                return; // listener closed
            }

            var task = ServeClientAsync(client);
            lock (_sessionsLock)
                _sessions.Add(task);
        }
    }

    private async Task ServeClientAsync(Socket client)
    {
        var session = new HelperSession(actuator, power, cpu);
        try
        {
            using (client)
            await using (var stream = new NetworkStream(client, ownsSocket: false))
            {
                using var reader = new StreamReader(stream);
                using var writer = new StreamWriter(stream) { AutoFlush = true };

                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_cts.Token);
                    if (line is null)
                        break; // EOF — GUI died or closed cleanly
                    var response = session.HandleLine(line);
                    await writer.WriteLineAsync(response);
                    if (response == "bye")
                        break;
                }
            }
        }
        catch (IOException)
        {
            // abrupt disconnect — restore below
        }
        catch (OperationCanceledException)
        {
            // Server shutting down while this client was still connected: the pending
            // ReadLineAsync cancels. That is normal shutdown (systemctl stop with the GUI open),
            // not a fault — leaving it to escape would throw out of DisposeAsync and skip socket
            // cleanup. Fans are still restored below.
        }
        finally
        {
            session.RestoreOwned();
        }
    }

    /// <summary>Shutdown: stop accepting, drain sessions (each restores its owned fans).</summary>
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener.Dispose();
        }
        catch
        {
            // already closed
        }

        try
        {
            await Task.WhenAll(_sessions.ToArray());
        }
        catch (OperationCanceledException)
        {
            // Sessions ending because we cancelled — expected during shutdown.
        }
        finally
        {
            TryDeleteSocket();   // teardown must complete even if a session faulted
        }
    }

    public void TryDeleteSocket()
    {
        try
        {
            if (File.Exists(socketPath))
                File.Delete(socketPath);
        }
        catch (IOException)
        {
            // best effort
        }
    }
}
