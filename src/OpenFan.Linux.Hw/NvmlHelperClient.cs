using System.Net.Sockets;
using OpenFan.Core.ControlLoop;

namespace OpenFan.Linux.Hw;

/// <summary>
/// IFanActuator talking to the root openfan-helper over its Unix socket (spec §3.4).
/// The user-session app stays unprivileged; the driver's root gate is satisfied by
/// the daemon, and the socket's group ownership restricts who may connect.
///
/// The connection is PERSISTENT on purpose: the helper restores a session's fans to
/// default when the connection closes (crash safety), so a per-call connection would
/// undo every set. Dispose() closes cleanly → helper restores what we owned.
/// </summary>
public sealed class NvmlHelperClient(string? socketPath = null) : IFanActuator, IDisposable
{
    public static string DefaultSocketPath =>
        Environment.GetEnvironmentVariable("OPENFAN_HELPER_SOCKET") ?? "/run/openfan/helper.sock";

    private readonly string _socketPath = socketPath ?? DefaultSocketPath;
    private readonly object _gate = new();
    private Socket? _socket;
    private StreamReader? _reader;
    private StreamWriter? _writer;

    /// <summary>True when the helper socket exists — cheap pre-check, real check is connect.</summary>
    public bool IsAvailable => File.Exists(_socketPath);

    public string? LastError { get; private set; }

    public bool SetPercent(string controlId, int percent)
        => Transact($"set {controlId} {percent}");

    public bool SetDefault(string controlId)
        => Transact($"default {controlId}");

    public bool Ping() => Transact("ping");

    /// <summary>GPU power limit (persisted by the driver; not restored on disconnect — that is intended).</summary>
    public bool TrySetPowerLimit(string uuid, int watts) => Transact($"power {uuid} {watts}");

    private bool Transact(string command)
    {
        lock (_gate)
        {
            if (!IsAvailable)
            {
                LastError = "GPU fan helper not running — install openfan-helper (see packaging/README)";
                Drop();
                return false;
            }

            // One reconnect-and-retry covers a helper restart between ticks.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (!EnsureConnected())
                    return false;
                try
                {
                    _writer!.WriteLine(command);
                    var response = _reader!.ReadLine();
                    if (response is null)
                        throw new IOException("helper closed the connection");

                    if (response == "ok")
                    {
                        LastError = null;
                        return true;
                    }
                    LastError = response;
                    return false; // protocol-level error — no point reconnecting
                }
                catch (Exception ex) when (ex is IOException or SocketException)
                {
                    Drop();
                    if (attempt == 1)
                    {
                        LastError = $"helper unreachable: {ex.Message}";
                        return false;
                    }
                }
            }
            return false;
        }
    }

    private bool EnsureConnected()
    {
        if (_socket is { Connected: true } && _reader is not null && _writer is not null)
            return true;

        Drop();
        try
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.SendTimeout = 1000;
            socket.ReceiveTimeout = 3000;
            socket.Connect(new UnixDomainSocketEndPoint(_socketPath));

            var stream = new NetworkStream(socket, ownsSocket: true);
            _socket = socket;
            _reader = new StreamReader(stream);
            _writer = new StreamWriter(stream) { AutoFlush = true };
            LastError = null;
            return true;
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            LastError = "helper socket denies access — is this session in the 'openfan' group?";
            return false;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            LastError = $"helper unreachable: {ex.Message}";
            return false;
        }
    }

    private void Drop()
    {
        try { _writer?.Dispose(); } catch (IOException) { /* already gone */ }
        try { _reader?.Dispose(); } catch (IOException) { /* already gone */ }
        try { _socket?.Dispose(); } catch (SocketException) { /* already gone */ }
        _writer = null;
        _reader = null;
        _socket = null;
    }

    /// <summary>Clean close — the helper's session EOF path restores anything we still owned.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            Drop();
        }
    }
}
