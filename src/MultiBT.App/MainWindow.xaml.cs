using System.Windows;
using System.Windows.Controls;
using MultiBT.App.Localization;
using MultiBT.App.ViewModels;
using MultiBT.Core.Audio;
using MultiBT.Core.Config;
using MultiBT.Core.Devices;
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
    private readonly Localizer _localizer = Localizer.Instance;
    private bool _initialising;

    public MainWindow(MainViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _viewModel = viewModel;

        InitializeComponent();

        DataContext = _viewModel;

        _initialising = true;

        foreach (int preset in EngineTunables.LatencyPresetsMs)
        {
            _ = LatencyPresetBox.Items.Add(preset);
        }

        LatencyPresetBox.SelectedItem = _viewModel.EngineLatencyMs;

        RebuildSyncModeItems();
        RebuildLanguageItems();
        RebuildCaptureSinkItems();

        _initialising = false;

        // Enum-valued pickers display localised TEXT, which the Localizer's indexer binding cannot
        // reach (ComboBox items are not bindings), so they have to be rebuilt on a language change.
        _localizer.LanguageChanged += OnLanguageChanged;
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
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    /// <summary>Rebuilds the sync-mode picker with localised labels, keeping the selection.</summary>
    private void RebuildSyncModeItems()
    {
        SyncMode current = _viewModel.Mode;

        SyncModeBox.Items.Clear();
        _ = SyncModeBox.Items.Add(new ComboBoxItem
        {
            Content = _localizer["SyncMode.AlignAll"],
            Tag = SyncMode.AlignAll,
        });
        _ = SyncModeBox.Items.Add(new ComboBoxItem
        {
            Content = _localizer["SyncMode.WiredOnly"],
            Tag = SyncMode.WiredOnly,
        });

        SyncModeBox.SelectedIndex = current == SyncMode.WiredOnly ? 1 : 0;
    }

    /// <summary>Rebuilds the language picker, keeping the selection.</summary>
    private void RebuildLanguageItems()
    {
        UiLanguage current = _viewModel.Language;

        LanguageBox.Items.Clear();
        _ = LanguageBox.Items.Add(new ComboBoxItem { Content = _localizer["Language.Chinese"], Tag = UiLanguage.Chinese });
        _ = LanguageBox.Items.Add(new ComboBoxItem { Content = _localizer["Language.English"], Tag = UiLanguage.English });

        LanguageBox.SelectedIndex = current == UiLanguage.English ? 1 : 0;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        bool wasInitialising = _initialising;
        _initialising = true;

        try
        {
            RebuildSyncModeItems();
            RebuildLanguageItems();
            RebuildCaptureSinkItems();
        }
        finally
        {
            _initialising = wasInitialising;
        }
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising || LanguageBox.SelectedItem is not ComboBoxItem { Tag: UiLanguage language })
        {
            return;
        }

        _viewModel.SetLanguage(language);
    }

    /// <summary>
    /// Builds the capture-sink list: usable virtual cables first, then everything else.
    /// </summary>
    /// <remarks>
    /// The candidates come from a live endpoint enumeration rather than the device list, because the sink
    /// is not something the user listens to and is therefore not one of the output rows.
    /// </remarks>
    private void RebuildCaptureSinkItems()
    {
        SinkBox.Items.Clear();

        _ = SinkBox.Items.Add(new ComboBoxItem
        {
            Content = _localizer["Sink.Auto"],
            Tag = null,
        });

        foreach (AudioEndpointInfo endpoint in _viewModel.CaptureSinkCandidates)
        {
            // No marker for a virtual cable: it is one option among others, and flagging it would imply
            // it is the intended answer.
            _ = SinkBox.Items.Add(new ComboBoxItem { Content = endpoint.FriendlyName, Tag = endpoint.EndpointId });
        }

        string? current = _viewModel.CaptureSinkEndpointId;

        SinkBox.SelectedIndex = 0;

        for (int i = 0; i < SinkBox.Items.Count; i++)
        {
            if (SinkBox.Items[i] is ComboBoxItem { Tag: string id }
                && string.Equals(id, current, StringComparison.OrdinalIgnoreCase))
            {
                SinkBox.SelectedIndex = i;
                break;
            }
        }
    }

    /// <summary>
    /// Opens the download page for the recommended free virtual cable.
    /// </summary>
    /// <remarks>
    /// MultiBT deliberately does not ship its own audio driver (see docs/DECISIONS.md): signing one for
    /// user machines needs an EV certificate, and a cable is a one-time free install that solves the same
    /// problem. That makes "go get one" a step on the critical path, so it should be one click rather
    /// than a name the user has to search for.
    /// </remarks>
    private void OnGetVirtualCable(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "https://vb-audio.com/Cable/",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "MultiBT", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnCaptureSinkChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising || SinkBox.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        _viewModel.SetCaptureSink(item.Tag as string);
    }

    private void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshDevices();
        RebuildCaptureSinkItems();
    }

    private void OnAutoMatchLevels(object sender, RoutedEventArgs e) => _viewModel.AutoMatchLevels();

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
        if (_initialising || SyncModeBox.SelectedItem is not ComboBoxItem { Tag: SyncMode mode })
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
        if (sender is FrameworkElement { DataContext: DeviceViewModel device })
        {
            _viewModel.SetPrimaryDevice(device);
        }
    }
}
