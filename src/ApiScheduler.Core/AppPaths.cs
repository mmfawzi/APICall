namespace ApiScheduler.Core;

/// <summary>
/// All runtime files live under %ProgramData%\ApiScheduler. The installer locks this folder down
/// (SYSTEM, Administrators and the service account only).
/// </summary>
public static class AppPaths
{
    public const string ServiceName = "ApiScheduler";

    public static string Root { get; set; } =
        Environment.GetEnvironmentVariable("APISCHEDULER_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ApiScheduler");

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
