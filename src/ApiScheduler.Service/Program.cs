using ApiScheduler.Core;
using ApiScheduler.Core.Logging;
using ApiScheduler.Service;

AppPaths.EnsureCreated();

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = AppPaths.ServiceName);
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(AppPaths.LogsDir));
builder.Logging.AddSimpleConsole();
if (OperatingSystem.IsWindows())
    builder.Logging.AddEventLog(s => { s.SourceName = AppPaths.ServiceName; s.Filter = (_, level) => level >= LogLevel.Warning; });

builder.Services.AddHostedService<SchedulerWorker>();

var host = builder.Build();
host.Run();
