using Tmds.DBus.Protocol;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Listens for logind PrepareForSleep on the system bus (Tmds.DBus.Protocol 0.20 — the version
/// Avalonia.FreeDesktop pins; do NOT upgrade it independently or Avalonia's X11 init TypeLoads break).
/// Suspend → caller restores fans to board/firmware control (thermal protection while we sleep).
/// Resume  → caller re-arms so curve writes are forced again even if the EC reset pwm_enable.
/// Absent/broken D-Bus (containers, CI) is a silent no-op.
/// </summary>
public sealed class LogindMonitor : IDisposable
{
    private IDisposable? _subscription;

    public void Start(Action onSuspending, Action onResume) => _ = RunAsync(onSuspending, onResume);

    private static bool ReadSleeping(Message message, object? state)
        => message.GetBodyReader().ReadBool(); // true = about to suspend, false = woke up

    private async Task RunAsync(Action onSuspending, Action onResume)
    {
        try
        {
            var connection = Connection.System;
            await connection.ConnectAsync();

            _subscription = await connection.AddMatchAsync<bool>(
                // No Sender filter: interface+member is logind-exclusive, and it lets
                // `busctl emit` exercise this path in tests (synthetic sender).
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Interface = "org.freedesktop.login1.Manager",
                    Member = "PrepareForSleep",
                },
                ReadSleeping,
                (Exception? ex, bool sleeping, object readerState, object handlerState) =>
                {
                    if (ex != null) return;
                    if (sleeping) onSuspending();
                    else onResume();
                },
                readerState: null,
                handlerState: null,
                emitOnCapturedContext: false);
            Console.Error.WriteLine("openfan: logind sleep/resume watcher attached");
        }
        catch { /* no system bus here */ }
    }

    public void Dispose()
    {
        try { _subscription?.Dispose(); } catch { }
        _subscription = null;
    }
}
