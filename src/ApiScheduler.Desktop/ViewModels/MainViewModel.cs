using System.Collections.ObjectModel;
using System.ServiceProcess;
using System.Windows;
using System.Windows.Threading;
using ApiScheduler.Core;
using ApiScheduler.Core.Models;
using ApiScheduler.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiScheduler.Desktop.ViewModels;

public sealed class JobListItem(ApiJob job) : ObservableObject
{
    public ApiJob Job { get; set; } = job;
    public Guid Id => Job.Id;
    public string Name => Job.Name;
    public string Subtitle => (Job.Enabled ? "Enabled" : "Disabled") + $" · every {Job.IntervalMinutes / 60.0:0.##} h";
    public void Refresh(ApiJob job) { Job = job; OnPropertyChanged(string.Empty); }
}

public partial class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer;
    private AppConfig _config = new();
    private bool _suppressSelection;

    public MainViewModel()
    {
        Reload();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();
        RefreshStatus();
    }

    public ObservableCollection<JobListItem> Jobs { get; } = new();
    public AppConfig Config => _config;
    public string DataFolder => AppPaths.Root;

    [ObservableProperty] private JobListItem? _selectedJob;
    [ObservableProperty] private JobEditorViewModel? _editor;
    [ObservableProperty] private string _serviceStatus = "Unknown";
    [ObservableProperty] private bool _serviceRunning;
    [ObservableProperty] private bool _serviceInstalled;
    [ObservableProperty] private string? _statusMessage;

    partial void OnSelectedJobChanged(JobListItem? oldValue, JobListItem? newValue)
    {
        if (_suppressSelection) return;
        if (Editor is { IsDirty: true } && oldValue != null && Editor.Id == oldValue.Id)
        {
            var answer = MessageBox.Show($"Save changes to '{Editor.Name}'?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !SaveCurrent()))
            {
                _suppressSelection = true;
                Application.Current.Dispatcher.BeginInvoke(() => { SelectedJob = oldValue; _suppressSelection = false; });
                return;
            }
        }
        Editor = newValue == null ? null : new JobEditorViewModel(newValue.Job, _config.Connections);
        RefreshStatus();
    }

    public void Reload()
    {
        try
        {
            _config = ConfigStore.Load();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Could not read configuration: " + ex.Message, "ApiScheduler", MessageBoxButton.OK, MessageBoxImage.Error);
            _config = new AppConfig();
        }
        var selectedId = SelectedJob?.Id;
        _suppressSelection = true;
        Jobs.Clear();
        foreach (var j in _config.Jobs) Jobs.Add(new JobListItem(j));
        _suppressSelection = false;
        SelectedJob = Jobs.FirstOrDefault(j => j.Id == selectedId) ?? Jobs.FirstOrDefault();
        if (SelectedJob == null) Editor = null;
        OnPropertyChanged(nameof(Config));
    }

    [RelayCommand]
    private void ReloadFromDisk()
    {
        if (Editor is { IsDirty: true } &&
            MessageBox.Show("Discard unsaved changes and reload?", "Reload", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        if (Editor != null) Editor.IsDirty = false;
        Reload();
    }

    [RelayCommand]
    private void NewJob() => AddJob(new ApiJob { ConnectionId = _config.Connections.FirstOrDefault()?.Id ?? Guid.Empty });

    [RelayCommand]
    private void NewSurveyJob() => AddJob(JobTemplates.SurveySms(_config.Connections.FirstOrDefault()?.Id ?? Guid.Empty));

    private void AddJob(ApiJob job)
    {
        if (Editor is { IsDirty: true } && !ConfirmLeave()) return;
        // Not persisted until the user clicks Save (validation needs a key, query, etc.).
        var copy = ModelCloner.Clone(_config);
        var baseName = job.Name;
        for (int i = 2; copy.Jobs.Any(j => j.Name == job.Name); i++) job.Name = $"{baseName} ({i})";
        _config.Jobs.Add(job);
        var item = new JobListItem(job);
        Jobs.Add(item);
        if (Editor != null) Editor.IsDirty = false;
        SelectedJob = item;
        Editor!.IsDirty = true;
        StatusMessage = "New job created — fill it in and click Save.";
    }

    private bool ConfirmLeave()
    {
        var answer = MessageBox.Show($"Save changes to '{Editor!.Name}'?", "Unsaved changes", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.Yes) return SaveCurrent();
        Editor.IsDirty = false;
        return true;
    }

    [RelayCommand]
    private void Save() => SaveCurrent();

    public bool SaveCurrent()
    {
        if (Editor == null) return true;
        ApiJob updated;
        try { updated = Editor.ToModel(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Save", MessageBoxButton.OK, MessageBoxImage.Error); return false; }

        // Re-read from disk so we never overwrite another admin's edits to *other* jobs/connections.
        AppConfig fresh;
        try { fresh = ConfigStore.Load(); } catch (Exception ex) { MessageBox.Show(ex.Message, "Save"); return false; }

        var errors = ConfigValidator.ValidateJob(updated, fresh);
        if (errors.Count > 0)
        {
            MessageBox.Show("Please fix the following:\n\n• " + string.Join("\n• ", errors), "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var idx = fresh.Jobs.FindIndex(j => j.Id == updated.Id);
        if (idx >= 0) fresh.Jobs[idx] = updated; else fresh.Jobs.Add(updated);
        try { ConfigStore.Save(fresh); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error); return false; }

        _config = fresh;
        Editor.AcceptSaved(updated);
        _suppressSelection = true;
        Jobs.Clear();
        foreach (var j in _config.Jobs) Jobs.Add(new JobListItem(j));
        SelectedJob = Jobs.First(j => j.Id == updated.Id);
        _suppressSelection = false;
        StatusMessage = $"Saved '{updated.Name}' at {DateTime.Now:HH:mm:ss}.";
        return true;
    }

    [RelayCommand]
    private void DeleteJob()
    {
        if (SelectedJob == null) return;
        if (MessageBox.Show($"Delete job '{SelectedJob.Name}'? This cannot be undone.", "Delete job", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var id = SelectedJob.Id;
        var fresh = ConfigStore.Load();
        fresh.Jobs.RemoveAll(j => j.Id == id);
        ConfigStore.Save(fresh);
        if (Editor != null) Editor.IsDirty = false;
        Editor = null;
        Reload();
        StatusMessage = "Job deleted.";
    }

    [RelayCommand]
    private void RunNow()
    {
        if (Editor == null) return;
        if (Editor.IsDirty) { MessageBox.Show("Save the job first — the service runs the saved version.", "Run now"); return; }
        if (!ServiceRunning) { MessageBox.Show("The ApiScheduler service is not running. Start it first.", "Run now"); return; }
        if (MessageBox.Show($"Send '{Editor.Name}' now?\n\nThis POSTs every pending row to {Editor.Url}.", "Run now",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        RunRequests.Request(Editor.Id);
        StatusMessage = "Run requested — the service will start it within ~15 seconds.";
    }

    [RelayCommand]
    private void ManageConnections()
    {
        var win = new ConnectionsWindow { Owner = Application.Current.MainWindow };
        win.ShowDialog();
        if (win.Changed)
        {
            var dirty = Editor?.IsDirty == true;
            var fresh = ConfigStore.Load();
            _config.Connections = fresh.Connections;
            if (Editor != null && !dirty) Editor = new JobEditorViewModel(SelectedJob!.Job, _config.Connections);
            else if (dirty) StatusMessage = "Connections changed — reopen the job to see them in the list.";
        }
    }

    [RelayCommand]
    private async Task StartServiceAsync() => await ControlServiceAsync(sc => sc.Start(), ServiceControllerStatus.Running);

    [RelayCommand]
    private async Task StopServiceAsync()
    {
        if (MessageBox.Show("Stop the scheduler service? No API calls will run until it is started again.", "Stop service",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await ControlServiceAsync(sc => sc.Stop(), ServiceControllerStatus.Stopped);
    }

    private async Task ControlServiceAsync(Action<ServiceController> action, ServiceControllerStatus target)
    {
        try
        {
            using var sc = new ServiceController(AppPaths.ServiceName);
            action(sc);
            await Task.Run(() => sc.WaitForStatus(target, TimeSpan.FromSeconds(30)));
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Service", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        try
        {
            using var sc = new ServiceController(AppPaths.ServiceName);
            var status = sc.Status;
            ServiceInstalled = true;
            ServiceRunning = status == ServiceControllerStatus.Running;
            ServiceStatus = status.ToString();
        }
        catch (InvalidOperationException)
        {
            ServiceInstalled = false;
            ServiceRunning = false;
            ServiceStatus = "Not installed";
        }

        ServiceState? state = null;
        try { state = StateStore.Load(); } catch { }
        if (ServiceRunning && state?.ServiceHeartbeatUtc is { } hb && DateTime.UtcNow - hb > TimeSpan.FromMinutes(2))
            ServiceStatus = "Running (not responding)";

        if (Editor != null)
        {
            JobState? js = null;
            state?.Jobs.TryGetValue(Editor.Id, out js);
            Editor.ApplyState(js);
        }
    }
}
