using System.Collections.Concurrent;
using System.Text;
using Microsoft.Extensions.Logging;

namespace ApiScheduler.Core.Logging;

/// <summary>Minimal daily rolling file logger (keeps 30 days). No third-party dependency.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _dir;
    private readonly BlockingCollection<string> _queue = new(10_000);
    private readonly Thread _writer;

    public FileLoggerProvider(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FileLogger" };
        _writer.Start();
        Cleanup();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Enqueue(string line) => _queue.TryAdd(line);

    private void WriteLoop()
    {
        foreach (var line in _queue.GetConsumingEnumerable())
        {
            try
            {
                File.AppendAllText(Path.Combine(_dir, $"apischeduler-{DateTime.Now:yyyyMMdd}.log"), line, Encoding.UTF8);
            }
            catch { /* never crash the service because of logging */ }
        }
    }

    private void Cleanup()
    {
        try
        {
            foreach (var f in Directory.GetFiles(_dir, "apischeduler-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-30)) File.Delete(f);
        }
        catch { }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(5));
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var cat = category[(category.LastIndexOf('.') + 1)..];
            var sb = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")).Append(" [").Append(logLevel.ToString()[..4].ToUpperInvariant()).Append("] ")
                .Append(cat).Append(": ").Append(formatter(state, exception)).AppendLine();
            if (exception != null) sb.AppendLine(exception.ToString());
            provider.Enqueue(sb.ToString());
        }
    }
}
