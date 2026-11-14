using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using OpenFan.Core.ControlLoop;
using OpenFan.Core.Hardware;

namespace OpenFan.Linux.Hw;

/// <summary>Persistent BMC session: snapshots renew leases; EOF restores its zones.</summary>
public sealed class BmcHelperClient(string? socketPath = null) : ISensorBackend, IFanActuator
{
    private readonly string _socketPath = socketPath ?? NvmlHelperClient.DefaultSocketPath;
    private readonly object _gate = new();
    private Socket? _socket;
    private NetworkStream? _stream;
    private BmcFanSnapshot _snapshot = new(false, null, []);
    private DateTimeOffset _lastProbe;
    private bool _disposed;
    private bool _reapplyRequired;
    private readonly Dictionary<int, int> _activeTargets = [];
    public string Name => "bmc";
    public bool Available => _snapshot.Available;
    public string? LastError { get; private set; }

    public static bool ReplacesNctControls
    {
        get
        {
            try { return File.Exists("/dev/ipmi0") && File.ReadAllText("/sys/class/dmi/id/board_name").Trim() == BmcProbe.SupportedBoard; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        }
    }

    public IReadOnlyList<HardwareItem> Discover()
    {
        lock (_gate)
        {
            // Old helpers return unknown command. Retry at most once every five seconds.
            if (!Available && DateTimeOffset.UtcNow - _lastProbe > TimeSpan.FromSeconds(5)) Refresh();
            return _snapshot.Channels.SelectMany(channel => new[]
            {
                new HardwareItem { Id = AsusBmcController.IdPrefix + channel.Index, Name = channel.Index == 0 ? "CPU_FAN / CPU_OPT (BMC)" : channel.Name + " (BMC)",
                    Kind = HardwareKind.Control, Backend = Name, Group = "ASUS BMC fans", MinPercent = channel.MinPercent,
                    CanSet = Available, Source = "Local IPMI", Details = "Requested BMC duty below 70°C; firmware curve forces 100% at 70°C and above. Original curve restored on release. CPU_FAN and CPU_OPT share a zone." },
                new HardwareItem { Id = "bmc:asus-wrx90:tach:" + channel.Index, Name = channel.Name,
                    Kind = HardwareKind.Tach, Backend = Name, Group = "ASUS BMC fans", CanSet = false, Unit = "RPM", Source = "BMC SDR" },
            }).ToArray();
        }
    }

    public void ReadInto(IDictionary<string, double?> readings)
    {
        lock (_gate)
        {
            if (Available || DateTimeOffset.UtcNow - _lastProbe > TimeSpan.FromSeconds(5)) Refresh();
            foreach (var channel in _snapshot.Channels)
            {
                readings[AsusBmcController.IdPrefix + channel.Index] = Available ? channel.TargetPercent : null;
                readings["bmc:asus-wrx90:tach:" + channel.Index] = Available ? channel.Rpm : null;
            }
        }
    }

    private void Refresh()
    {
        _lastProbe = DateTimeOffset.UtcNow;
        var response = Exchange("bmcsnapshot");
        try
        {
            if (response is null || !response.StartsWith("ok ", StringComparison.Ordinal))
            { _snapshot = _snapshot with { Available = false }; return; }
            var snapshot = JsonSerializer.Deserialize<BmcFanSnapshot>(Convert.FromBase64String(response[3..]));
            if (snapshot is null || snapshot.Channels is null || snapshot.Channels.Length > 7 ||
                snapshot.Channels.Select(c => c.Index).Distinct().Count() != snapshot.Channels.Length ||
                snapshot.Channels.Any(c => c.Index is < 0 or > 6 || c.MinPercent is < 20 or > 100))
                throw new JsonException("Invalid BMC snapshot.");
            _snapshot = snapshot;
            if (_activeTargets.Any(p => snapshot.Channels.FirstOrDefault(c => c.Index == p.Key)?.TargetPercent != p.Value))
                _reapplyRequired = true;
            LastError = snapshot.Error;
        }
        catch (Exception ex) when (ex is JsonException or FormatException)
        { LastError = ex.Message; _snapshot = _snapshot with { Available = false }; Drop(); }
    }

    public bool SetPercent(string id, int percent)
    {
        lock (_gate)
        {
            if (!AsusBmcController.TryIndex(id, out var index) || Exchange($"set {id} {percent}") != "ok") return false;
            _activeTargets[index] = percent;
            return true;
        }
    }
    public bool SetDefault(string id)
    {
        lock (_gate)
        {
            if (!AsusBmcController.TryIndex(id, out var index) || Exchange($"default {id}") != "ok") return false;
            _activeTargets.Remove(index);
            return true;
        }
    }
    public bool TakeReapplyRequest()
    {
        lock (_gate) { var required = _reapplyRequired; _reapplyRequired = false; return required; }
    }

    private string? Exchange(string command)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            try
            {
                if (_socket is null)
                {
                    _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
                        { SendTimeout = 2000, ReceiveTimeout = 10000 };
                    _socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
                    _stream = new NetworkStream(_socket, ownsSocket: false);
                }
                _stream!.Write(Encoding.ASCII.GetBytes(command + "\n"));
                var bytes = new List<byte>();
                // Bounded line; no buffered reader can consume bytes belonging to the next reply.
                for (var i = 0; i < 32768; i++)
                {
                    var b = _stream.ReadByte();
                    if (b < 0) throw new IOException("BMC helper closed the connection.");
                    if (b == '\n')
                    {
                        var response = Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
                        LastError = response.StartsWith("ok", StringComparison.Ordinal) ? null : response;
                        return response;
                    }
                    bytes.Add((byte)b);
                }
                throw new IOException("BMC helper response exceeds its size limit.");
            }
            catch (Exception ex) when (ex is IOException or SocketException)
            { LastError = "BMC helper: " + ex.Message; Drop(); return null; }
        }
    }

    private void Drop()
    {
        if (_socket is not null && _activeTargets.Count > 0) _reapplyRequired = true;
        _stream?.Dispose(); _stream = null; _socket?.Dispose(); _socket = null;
    }
    public void Dispose() { lock (_gate) { _disposed = true; Drop(); } }
}
