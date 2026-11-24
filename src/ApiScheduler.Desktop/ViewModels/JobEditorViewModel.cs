using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Data;
using ApiScheduler.Core;
using ApiScheduler.Core.Json;
using ApiScheduler.Core.Models;
using ApiScheduler.Core.Security;
using ApiScheduler.Core.Sql;
using ApiScheduler.Core.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ApiScheduler.Desktop.ViewModels;

/// <summary>Editable copy of one job. Nothing touches config.json until Save.</summary>
public partial class JobEditorViewModel : ObservableObject
{
    private readonly ApiJob _original;
    private string? _newApiKey;
    private bool _clearApiKey;

    public JobEditorViewModel(ApiJob job, IReadOnlyList<DbConnectionProfile> connections)
    {
        _original = ModelCloner.Clone(job);
        Connections = connections;
        Id = job.Id;
        _name = job.Name;
        _enabled = job.Enabled;
        _url = job.Url;
        _apiKeyHeaderName = job.ApiKeyHeaderName;
        _apiKeyFingerprint = job.ApiKeyFingerprint;
        _pinnedCertificateThumbprint = job.PinnedCertificateThumbprint;
        _timeoutSeconds = job.TimeoutSeconds;
        _delayBetweenCallsMs = job.DelayBetweenCallsMs;
        _intervalHours = job.IntervalMinutes / 60.0;
        _scheduleAnchor = job.ScheduleAnchor;
        _selectedConnection = connections.FirstOrDefault(c => c.Id == job.ConnectionId);
        _selectQuery = job.SelectQuery;
        _maxRowsPerRun = job.MaxRowsPerRun;
        WriteBack = ModelCloner.Clone(job.WriteBack);
        Mappings = new ObservableCollection<FieldMapping>(job.Mappings.Select(ModelCloner.Clone));
        Mappings.CollectionChanged += (_, _) => IsDirty = true;
        History = new ObservableCollection<RunRecord>();
        LoadHistory();
        IsDirty = false;
    }

    public Guid Id { get; }
    public IReadOnlyList<DbConnectionProfile> Connections { get; }
    public ObservableCollection<FieldMapping> Mappings { get; }
    public ObservableCollection<RunRecord> History { get; }
    public WriteBackSettings WriteBack { get; }
    public static MappingSource[] SourceValues { get; } = Enum.GetValues<MappingSource>();
    public static FieldType[] TypeValues { get; } = Enum.GetValues<FieldType>();

    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _name;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _apiKeyHeaderName;
    [ObservableProperty] private string? _apiKeyFingerprint;
    [ObservableProperty] private string? _pinnedCertificateThumbprint;
    [ObservableProperty] private int _timeoutSeconds;
    [ObservableProperty] private int _delayBetweenCallsMs;
    [ObservableProperty] private double _intervalHours;
    [ObservableProperty] private string? _scheduleAnchor;
    [ObservableProperty] private DbConnectionProfile? _selectedConnection;
    [ObservableProperty] private string _selectQuery;
    [ObservableProperty] private int _maxRowsPerRun;
    [ObservableProperty] private string? _queryMessage;
    [ObservableProperty] private bool _queryOk;
    [ObservableProperty] private DataView? _previewRows;
    [ObservableProperty] private string? _previewJson;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private FieldMapping? _selectedMapping;
    [ObservableProperty] private string? _nextRun;
    [ObservableProperty] private string? _lastResult;

    public string ApiKeyStatus => _clearApiKey ? "Key will be removed on save"
        : _newApiKey != null ? "New key entered — will be encrypted on save"
        : ApiKeyFingerprint != null ? $"Stored (encrypted). Fingerprint {ApiKeyFingerprint}"
        : "No key stored";

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(IsDirty) or nameof(QueryMessage) or nameof(QueryOk) or nameof(PreviewRows)
            or nameof(PreviewJson) or nameof(IsBusy) or nameof(SelectedMapping) or nameof(NextRun) or nameof(LastResult) or nameof(ApiKeyStatus)))
            IsDirty = true;
    }

    /// <summary>Called from the PasswordBox (never bound, so the key never sits in a binding/property store).</summary>
    public void SetNewApiKey(string? key)
    {
        _newApiKey = string.IsNullOrEmpty(key) ? null : key;
        if (_newApiKey != null) _clearApiKey = false;
        IsDirty = true;
        OnPropertyChanged(nameof(ApiKeyStatus));
    }

    [RelayCommand]
    private void ClearApiKey()
    {
        _newApiKey = null;
        _clearApiKey = true;
        IsDirty = true;
        OnPropertyChanged(nameof(ApiKeyStatus));
    }

    public void MarkDirty() => IsDirty = true;

    public ApiJob ToModel()
    {
        var job = ModelCloner.Clone(_original);
        job.Name = Name.Trim();
        job.Enabled = Enabled;
        job.Url = Url.Trim();
        job.ApiKeyHeaderName = ApiKeyHeaderName.Trim();
        job.PinnedCertificateThumbprint = string.IsNullOrWhiteSpace(PinnedCertificateThumbprint) ? null : PinnedCertificateThumbprint.Trim();
        job.TimeoutSeconds = TimeoutSeconds;
        job.DelayBetweenCallsMs = DelayBetweenCallsMs;
        job.IntervalMinutes = (int)Math.Round(IntervalHours * 60);
        job.ScheduleAnchor = string.IsNullOrWhiteSpace(ScheduleAnchor) ? null : ScheduleAnchor.Trim();
        job.ConnectionId = SelectedConnection?.Id ?? Guid.Empty;
        job.SelectQuery = SelectQuery;
        job.MaxRowsPerRun = MaxRowsPerRun;
        job.WriteBack = ModelCloner.Clone(WriteBack);
        job.Mappings = Mappings.Where(m => !string.IsNullOrWhiteSpace(m.JsonField)).Select(ModelCloner.Clone).ToList();

        if (_clearApiKey) { job.EncryptedApiKey = null; job.ApiKeyFingerprint = null; }
        if (_newApiKey != null)
        {
            job.EncryptedApiKey = SecretProtector.Protect(_newApiKey);
            job.ApiKeyFingerprint = SecretProtector.Fingerprint(_newApiKey);
        }
        return job;
    }

    /// <summary>After a successful save the editor becomes the new baseline.</summary>
    public void AcceptSaved(ApiJob saved)
    {
        _original.EncryptedApiKey = saved.EncryptedApiKey;
        _original.ApiKeyFingerprint = saved.ApiKeyFingerprint;
        ApiKeyFingerprint = saved.ApiKeyFingerprint;
        _newApiKey = null;
        _clearApiKey = false;
        OnPropertyChanged(nameof(ApiKeyStatus));
        IsDirty = false;
    }

    [RelayCommand]
    private void ValidateQuery()
    {
        var errors = SqlSafety.ValidateSelect(SelectQuery);
        QueryOk = errors.Count == 0;
        QueryMessage = QueryOk ? "✔ Valid single SELECT statement." : "✖ " + string.Join(Environment.NewLine, errors);
    }

    [RelayCommand]
    private async Task PreviewAsync()
    {
        ValidateQuery();
        if (!QueryOk) return;
        if (SelectedConnection == null) { QueryMessage = "✖ Select a database connection first."; QueryOk = false; return; }

        IsBusy = true;
        PreviewRows = null;
        PreviewJson = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await using var conn = await SqlData.OpenAsync(SelectedConnection, cts.Token);
            var result = await SqlData.QueryAsync(conn, SelectQuery, 20, cts.Token);

            var table = new DataTable();
            foreach (var c in result.Columns.Distinct(StringComparer.OrdinalIgnoreCase)) table.Columns.Add(c, typeof(string));
            foreach (var r in result.Rows)
                table.Rows.Add(table.Columns.Cast<DataColumn>().Select(c => r.TryGetValue(c.ColumnName, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null).ToArray());
            PreviewRows = table.DefaultView;
            LastColumns = result.Columns;

            if (result.Rows.Count == 0)
            {
                PreviewJson = "(query returned no rows — nothing would be sent right now)";
            }
            else
            {
                try
                {
                    var body = JsonBodyBuilder.Build(result.Rows[0], Mappings.Where(m => !string.IsNullOrWhiteSpace(m.JsonField)));
                    PreviewJson = $"// Body for the first row ({result.Rows.Count} row(s) shown, max {MaxRowsPerRun} per run)\n" + JsonBodyBuilder.Serialize(body, indented: true);
                }
                catch (Exception ex) { PreviewJson = "Mapping error: " + ex.Message; }
            }

            if (WriteBack.Enabled && !result.Columns.Contains(WriteBack.KeyColumn, StringComparer.OrdinalIgnoreCase))
                QueryMessage = $"⚠ Query OK, but it does not return the key column '{WriteBack.KeyColumn}' needed to mark rows as sent.";
            else
                QueryMessage = $"✔ Query OK — {result.Columns.Count} column(s), showing {result.Rows.Count} row(s).";
        }
        catch (Exception ex)
        {
            QueryOk = false;
            QueryMessage = "✖ " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    public IReadOnlyList<string>? LastColumns { get; private set; }

    /// <summary>Adds a mapping for each query column that is not mapped yet (POLICY_NO → policyNo).</summary>
    [RelayCommand]
    private async Task AutoMapAsync()
    {
        if (LastColumns == null) await PreviewAsync();
        if (LastColumns == null) return;
        var mapped = Mappings.Where(m => m.Source == MappingSource.Column).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { WriteBack.KeyColumn, WriteBack.SentCallColumn, WriteBack.SentDateColumn, "CREATION_DATE" };
        foreach (var col in LastColumns.Where(c => !mapped.Contains(c) && !skip.Contains(c)))
            Mappings.Add(new FieldMapping { JsonField = JobTemplates.ToCamel(col), Source = MappingSource.Column, Value = col });
    }

    [RelayCommand]
    private void AddMapping() =>
        Mappings.Add(new FieldMapping { JsonField = "newField", Source = MappingSource.Fixed, Value = "" });

    [RelayCommand]
    private void RemoveMapping()
    {
        if (SelectedMapping != null) Mappings.Remove(SelectedMapping);
    }

    [RelayCommand]
    private void MoveMappingUp() => Move(-1);

    [RelayCommand]
    private void MoveMappingDown() => Move(1);

    private void Move(int delta)
    {
        if (SelectedMapping == null) return;
        var i = Mappings.IndexOf(SelectedMapping);
        var j = i + delta;
        if (j < 0 || j >= Mappings.Count) return;
        var keep = SelectedMapping;
        Mappings.Move(i, j);
        SelectedMapping = keep;
    }

    [RelayCommand]
    public void LoadHistory()
    {
        History.Clear();
        try { foreach (var r in HistoryStore.ForJob(Id)) History.Add(r); } catch { /* history is optional */ }
    }

    public void ApplyState(JobState? state)
    {
        NextRun = !Enabled ? "Disabled"
            : state?.IsRunning == true ? "Running now…"
            : state?.NextRunUtc is { } n ? n.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            : "Waiting for service";
        LastResult = state?.LastRunStartUtc is { } s ? $"{s.ToLocalTime():yyyy-MM-dd HH:mm} — {state.LastResult}" : "Never run";
    }
}
