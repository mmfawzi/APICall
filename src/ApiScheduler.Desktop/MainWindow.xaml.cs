using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ApiScheduler.Desktop.ViewModels;

namespace ApiScheduler.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // A new editor means a different job: never carry a typed key over to it.
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.Editor) && ApiKeyBox.Password.Length > 0)
            {
                _clearingKeyBox = true;
                ApiKeyBox.Clear();
                _clearingKeyBox = false;
            }
        };
    }

    private bool _clearingKeyBox;
    private MainViewModel Vm => (MainViewModel)DataContext;

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_clearingKeyBox || Vm.Editor == null) return;
        Vm.Editor.SetNewApiKey(ApiKeyBox.Password);
    }

    private void Mappings_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e) => Vm.Editor?.MarkDirty();

    private void WriteBack_Changed(object? sender, System.Windows.Data.DataTransferEventArgs e) => Vm.Editor?.MarkDirty();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (Vm.Editor is { IsDirty: true })
        {
            var answer = MessageBox.Show($"Save changes to '{Vm.Editor.Name}' before closing?", "Unsaved changes",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Vm.SaveCurrent()))
                e.Cancel = true;
        }
        if (!e.Cancel)
        {
            _clearingKeyBox = true;
            ApiKeyBox.Clear();
        }
        base.OnClosing(e);
    }
}
