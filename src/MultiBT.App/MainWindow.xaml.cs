using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using MultiBT.App.ViewModels;
using MultiBT.Core.Audio;
using MultiBT.Core.Sync;

namespace MultiBT.App;

/// <summary>
/// Main window. Responsibilities are kept to pure UI concerns: wiring the pickers and forwarding
/// commands to the view model.
/// </summary>
/// <remarks>
/// The view model is injected rather than constructed here, because the tray also needs it and
/// there must be exactly one — two view models would mean two independent ideas of which devices
/// are enabled.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _initialising;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;

        InitializeComponent();

        DataContext = _viewModel;

        // Populate the pickers. Guarded so that assigning the initial selection does not fire
        // the change handlers and kick off a recompute before the window is even shown.
        _initialising = true;

        foreach (int preset in EngineTunables.LatencyPresetsMs)
        {
            _ = LatencyPresetBox.Items.Add(preset);
        }

        LatencyPresetBox.SelectedItem = _viewModel.EngineLatencyMs;

        _ = SyncModeBox.Items.Add(SyncMode.AlignAll);
        _ = SyncModeBox.Items.Add(SyncMode.WiredOnly);
        SyncModeBox.SelectedItem = _viewModel.Mode;

        _initialising = false;
    }

    /// <summary>The view model this window is bound to.</summary>
    public MainViewModel ViewModel => _viewModel;

    /// <summary>
    /// Set by the app during real shutdown, so closing the window can be permitted.
    /// </summary>
    /// <remarks>
    /// Closing the window normally HIDES it instead: the tray is the primary UI, and quitting the
    /// whole mirror because the user dismissed a settings window would be wrong.
    /// </remarks>
    public bool AllowClose { get; set; }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => _viewModel.RefreshDevices();

    private void OnAutoMatchLevels(object sender, RoutedEventArgs e) => _viewModel.AutoMatchLevels();

    private void OnMaximizeEndpointVolume(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceViewModel device })
        {
            _viewModel.MaximizeEndpointVolume(device);
        }
    }

    private void OnLatencyPresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising || LatencyPresetBox.SelectedItem is not int preset)
        {
            return;
        }

        _viewModel.EngineLatencyMs = preset;
    }

    private void OnSyncModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising || SyncModeBox.SelectedItem is not SyncMode mode)
        {
            return;
        }

        _viewModel.Mode = mode;
    }

    private void OnStart(object sender, RoutedEventArgs e) => _viewModel.Start();

    private async void OnStop(object sender, RoutedEventArgs e) => await _viewModel.StopAsync();

    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.SaveCurrentStateToActiveProfile();

    private void OnPrimaryDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton radioButton && radioButton.DataContext is DeviceViewModel device)
        {
            _viewModel.SetPrimaryDevice(device);
        }
    }
}
