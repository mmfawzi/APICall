using System.Text.Json;
using System.Text.Json.Serialization;
using ApiScheduler.Core.Models;

namespace ApiScheduler.Core.Storage;

internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}

/// <summary>
/// Atomic, cross-process-safe JSON file access. Writes go to a temp file and are swapped in,
/// so a crash never leaves a half-written config. A global mutex serialises desktop and service.
/// </summary>
public static class JsonFileStore
{
    private static readonly Mutex FileMutex = CreateMutex();

    private static Mutex CreateMutex()
    {
        try { return new Mutex(false, @"Global\ApiScheduler.Files"); }
        catch (UnauthorizedAccessException) { return new Mutex(false, @"Local\ApiScheduler.Files"); }
    }

    public static T Read<T>(string path) where T : new()
    {
        return WithLock(() =>
        {
            if (!File.Exists(path)) return new T();
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return new T();
            return JsonSerializer.Deserialize<T>(text, JsonOpts.Options) ?? new T();
        });
    }

    public static void Write<T>(string path, T value)
    {
        WithLock(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, JsonOpts.Options));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
            return 0;
        });
    }

    public static void AppendLine<T>(string path, T value, long maxBytes = 5 * 1024 * 1024)
    {
        WithLock(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > maxBytes)
            {
                var old = path + ".1";
                File.Copy(path, old, overwrite: true);
                File.Delete(path);
            }
            File.AppendAllText(path, JsonSerializer.Serialize(value, JsonOpts.Compact) + Environment.NewLine);
            return 0;
        });
    }

    public static List<T> ReadLines<T>(string path)
    {
        return WithLock(() =>
        {
            var list = new List<T>();
            if (!File.Exists(path)) return list;
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var item = JsonSerializer.Deserialize<T>(line, JsonOpts.Compact);
                    if (item != null) list.Add(item);
                }
                catch (JsonException) { /* skip corrupt line */ }
            }
            return list;
        });
    }

    private static T WithLock<T>(Func<T> action)
    {
        bool owned = false;
        try
        {
            try { owned = FileMutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new IOException("Timed out waiting for the ApiScheduler file lock.");
            return action();
        }
        finally
        {
            if (owned) FileMutex.ReleaseMutex();
        }
    }
}

public static class ConfigStore
{
    public static AppConfig Load() => JsonFileStore.Read<AppConfig>(AppPaths.ConfigFile);

    public static void Save(AppConfig config)
    {
        var errors = ConfigValidator.Validate(config);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        JsonFileStore.Write(AppPaths.ConfigFile, config);
    }

    public static DateTime LastWriteUtc() =>
        File.Exists(AppPaths.ConfigFile) ? File.GetLastWriteTimeUtc(AppPaths.ConfigFile) : DateTime.MinValue;
}

public static class StateStore
{
    public static ServiceState Load() => JsonFileStore.Read<ServiceState>(AppPaths.StateFile);
    public static void Save(ServiceState state) => JsonFileStore.Write(AppPaths.StateFile, state);
}

public static class HistoryStore
{
    public static void Append(RunRecord record) => JsonFileStore.AppendLine(AppPaths.HistoryFile, record);

    public static List<RunRecord> ForJob(Guid jobId, int max = 200) =>
        JsonFileStore.ReadLines<RunRecord>(AppPaths.HistoryFile)
            .Where(r => r.JobId == jobId)
            .OrderByDescending(r => r.StartUtc)
            .Take(max)
            .ToList();
}

/// <summary>"Run now" requests from the desktop app are files in the requests folder, picked up by the service.</summary>
public static class RunRequests
{
    public static void Request(Guid jobId)
    {
        Directory.CreateDirectory(AppPaths.RequestsDir);
        File.WriteAllText(Path.Combine(AppPaths.RequestsDir, $"{jobId:N}.run"), DateTime.UtcNow.ToString("O"));
    }

    public static IEnumerable<Guid> TakeAll()
    {
        if (!Directory.Exists(AppPaths.RequestsDir)) yield break;
        foreach (var file in Directory.GetFiles(AppPaths.RequestsDir, "*.run"))
        {
            Guid id;
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "N", out id)) { TryDelete(file); continue; }
            TryDelete(file);
            yield return id;
        }
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
