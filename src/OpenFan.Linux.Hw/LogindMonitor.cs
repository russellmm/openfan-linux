using Tmds.DBus.Protocol;

namespace OpenFan.Linux.Hw;

/// <summary>
/// Listens for logind PrepareForSleep on the system bus. The patched 0.21.3 protocol library
/// is verified with the current Avalonia desktop through an isolated GUI startup check.
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
                new MatchRule
                {
                    Type = MessageType.Signal,
                    Sender = "org.freedesktop.login1",
                    Path = "/org/freedesktop/login1",
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
