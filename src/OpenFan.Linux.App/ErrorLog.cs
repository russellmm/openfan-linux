namespace OpenFan.Linux.App;

/// <summary>Rolling error journal at $XDG_STATE_HOME/openfan/errors.log — the "Open error log" menu item shows this.</summary>
public static class ErrorLog
{
    public static string Path
    {
        get
        {
            var state = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            var root = string.IsNullOrEmpty(state)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share")
                : state;
            return System.IO.Path.Combine(root, "openfan", "errors.log");
        }
    }

    public static void Write(string category, Exception? ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            System.IO.File.AppendAllText(Path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {category}: {ex}\n\n");
        }
        catch { }
    }
}
