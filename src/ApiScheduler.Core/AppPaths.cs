namespace ApiScheduler.Core;

/// <summary>
/// Runtime files (config, state, history, logs) live in a "Data" folder next to the app folders:
///   D:\APICall\ApiService\   D:\APICall\ApiDesktop\   D:\APICall\Data\
/// so the service and the desktop app always share the same Data folder.
/// The installer locks the whole install folder down (SYSTEM, Administrators, service account).
/// APISCHEDULER_HOME overrides the location (used for testing).
/// </summary>
public static class AppPaths
{
    public const string ServiceName = "ApiScheduler";

    public static string Root { get; set; } = ResolveRoot();

    private static string ResolveRoot()
    {
        var env = Environment.GetEnvironmentVariable("APISCHEDULER_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return env;

        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var installDir = Path.GetDirectoryName(exeDir) ?? exeDir;
        return Path.Combine(installDir, "Data");
    }

    public static string ConfigFile => Path.Combine(Root, "config.json");
    public static string StateFile => Path.Combine(Root, "state.json");
    public static string HistoryFile => Path.Combine(Root, "history.jsonl");
    public static string RequestsDir => Path.Combine(Root, "requests");
    public static string LogsDir => Path.Combine(Root, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RequestsDir);
        Directory.CreateDirectory(LogsDir);
    }
}
