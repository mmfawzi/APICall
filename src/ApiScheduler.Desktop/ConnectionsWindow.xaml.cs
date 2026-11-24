using System.Windows;
using System.Windows.Controls;
using ApiScheduler.Core;
using ApiScheduler.Core.Models;
using ApiScheduler.Core.Security;
using ApiScheduler.Core.Sql;
using ApiScheduler.Core.Storage;

namespace ApiScheduler.Desktop;

public partial class ConnectionsWindow : Window
{
    private List<DbConnectionProfile> _connections = new();
    private DbConnectionProfile? _current;

    public bool Changed { get; private set; }

    public ConnectionsWindow()
    {
        InitializeComponent();
        LoadList();
        Closed += (_, _) => PasswordBox.Clear();
    }

    private void LoadList(Guid? select = null)
    {
        _connections = ConfigStore.Load().Connections;
        List.ItemsSource = null;
        List.ItemsSource = _connections;
        List.SelectedItem = _connections.FirstOrDefault(c => c.Id == select) ?? _connections.FirstOrDefault();
        if (List.SelectedItem == null) ShowItem(null);
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowItem(List.SelectedItem as DbConnectionProfile);

    private void ShowItem(DbConnectionProfile? c)
    {
        _current = c == null ? null : ModelCloner.Clone(c);
        Form.IsEnabled = c != null;
        NameBox.Text = c?.Name ?? "";
        ServerBox.Text = c?.Server ?? "";
        DatabaseBox.Text = c?.Database ?? "";
        AuthBox.SelectedIndex = c?.AuthMode == DbAuthMode.Sql ? 1 : 0;
        UserBox.Text = c?.SqlUser ?? "";
        PasswordBox.Clear();
        EncryptBox.IsChecked = c?.Encrypt ?? true;
        TrustBox.IsChecked = c?.TrustServerCertificate ?? false;
        TestResult.Text = "";
        UpdateAuthUi();
    }

    private void AuthBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateAuthUi();

    private bool IsSqlAuth => AuthBox.SelectedIndex == 1;

    private void UpdateAuthUi()
    {
        if (UserBox == null) return;
        UserBox.IsEnabled = PasswordBox.IsEnabled = IsSqlAuth;
        PasswordHint.Text = !IsSqlAuth ? ""
            : string.IsNullOrEmpty(_current?.EncryptedSqlPassword) ? "No password stored yet."
            : "A password is stored (encrypted). Leave empty to keep it.";
    }

    /// <summary>Builds the profile from the form. The password is encrypted immediately and never kept in plain text.</summary>
    private DbConnectionProfile? ReadForm()
    {
        if (_current == null) return null;
        var c = ModelCloner.Clone(_current);
        c.Name = NameBox.Text.Trim();
        c.Server = ServerBox.Text.Trim();
        c.Database = DatabaseBox.Text.Trim();
        c.AuthMode = IsSqlAuth ? DbAuthMode.Sql : DbAuthMode.Windows;
        c.Encrypt = EncryptBox.IsChecked == true;
        c.TrustServerCertificate = TrustBox.IsChecked == true;
        if (IsSqlAuth)
        {
            c.SqlUser = UserBox.Text.Trim();
            if (PasswordBox.Password.Length > 0) c.EncryptedSqlPassword = SecretProtector.Protect(PasswordBox.Password);
        }
        else
        {
            c.SqlUser = null;
            c.EncryptedSqlPassword = null;
        }
        if (string.IsNullOrWhiteSpace(c.Name)) c.Name = $"{c.Server}/{c.Database}";
        return c;
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        var c = ReadForm();
        if (c == null) return;
        TestResult.Foreground = System.Windows.Media.Brushes.Gray;
        TestResult.Text = "Connecting…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await using var conn = await SqlData.OpenAsync(c, cts.Token);
            TestResult.Foreground = System.Windows.Media.Brushes.Green;
            TestResult.Text = $"✔ Connected (SQL Server {conn.ServerVersion}).";
        }
        catch (Exception ex)
        {
            TestResult.Foreground = System.Windows.Media.Brushes.Firebrick;
            TestResult.Text = "✖ " + ex.Message;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var c = ReadForm();
        if (c == null) return;
        var config = ConfigStore.Load();
        var idx = config.Connections.FindIndex(x => x.Id == c.Id);
        if (idx >= 0) config.Connections[idx] = c; else config.Connections.Add(c);
        try
        {
            ConfigStore.Save(config);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Cannot save", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Changed = true;
        LoadList(c.Id);
    }

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var c = new DbConnectionProfile { Name = "New connection" };
        _connections.Add(c);
        List.ItemsSource = null;
        List.ItemsSource = _connections;
        List.SelectedItem = c;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not DbConnectionProfile c) return;
        var config = ConfigStore.Load();
        var users = config.Jobs.Where(j => j.ConnectionId == c.Id).Select(j => j.Name).ToList();
        if (users.Count > 0)
        {
            MessageBox.Show("This connection is used by: " + string.Join(", ", users), "Cannot delete", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MessageBox.Show($"Delete connection '{c.Name}'?", "Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        config.Connections.RemoveAll(x => x.Id == c.Id);
        ConfigStore.Save(config);
        Changed = true;
        LoadList();
    }
}
