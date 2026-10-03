namespace SaitekMultiPanel;

/// <summary>OpenDeck redirects stdout/stderr to %APPDATA%\opendeck\logs\plugins\&lt;uuid&gt;.log.</summary>
public static class Log
{
    /// <summary>On with MULTIPANEL_DEBUG=1 or an empty "debug" file in the plugin folder (OpenDeck's working dir).</summary>
    public static bool Verbose { get; set; } =
        Environment.GetEnvironmentVariable("MULTIPANEL_DEBUG") == "1" || File.Exists("debug");

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message) => Write("ERROR", message);

    public static void Debug(string message)
    {
        if (Verbose)
            Write("DEBUG", message);
    }

    private static void Write(string level, string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}");
}
