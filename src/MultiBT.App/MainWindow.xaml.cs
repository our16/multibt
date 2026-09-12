using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using MultiBT.App.Localization;
using MultiBT.App.Recommendations;
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
    /// <summary>
    /// Short type label for one entry in the input list.
    /// </summary>
    /// <remarks>
    /// A virtual cable is called out by name rather than by its transport, which would classify it as
    /// "Other" alongside the HDMI oddities. It is the one type difference that changes what the app can do,
    /// so it is the one worth naming.
    /// </remarks>
    private string DescribeInputType(AudioEndpointInfo endpoint)
    {
        if (VirtualCableDetector.IsVirtualCableRenderEndpoint(
                endpoint.FriendlyName, endpoint.DeviceFriendlyName))
        {
            return _localizer["Input.Type.VirtualCable"];
        }

        return _localizer[$"Transport.{endpoint.Transport}"];
    }

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
            // The input list carries the TYPE, because the type is what decides whether an input is worth
            // choosing: a virtual cable makes every speaker controllable, while a real device is captured
            // as-is and keeps playing natively. It is still only a label — nothing marks a cable as the
            // "correct" answer.
            _ = SinkBox.Items.Add(new ComboBoxItem
            {
                Content = $"{endpoint.FriendlyName}（{DescribeInputType(endpoint)}）",
                Tag = endpoint.EndpointId,
            });
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
    /// <summary>
    /// Opens the recommended-devices menu, anchored under the button that raised it.
    /// </summary>
    /// <remarks>
    /// A dropdown rather than a direct link, so the app can name a few products and state the real catch
    /// with each instead of silently promoting one vendor. Nothing here installs anything: every entry
    /// opens the vendor's own page, because MultiBT does not distribute drivers.
    /// </remarks>
    private void OnGetVirtualCable(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = PlacementMode.Bottom };

        foreach (VirtualAudioRecommendation recommendation in VirtualAudioRecommendations.All)
        {
            var item = new MenuItem
            {
                Header = _localizer[recommendation.NameKey],
                ToolTip = _localizer[recommendation.NoteKey],
            };

            string url = recommendation.Url;
            item.Click += (_, _) => OpenUrl(url);

            if (recommendation.RepositoryUrl is string repo && repo != url)
            {
                var repoItem = new MenuItem { Header = _localizer["Recommend.Repository"] };
                repoItem.Click += (_, _) => OpenUrl(repo);
                _ = item.Items.Add(repoItem);
            }

            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = _localizer["Recommend.Note"],
            IsEnabled = false,
        });

        menu.IsOpen = true;
    }

    /// <summary>Opens one URL in the user's browser, reporting a failure instead of throwing.</summary>
    private void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "MultiBT", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Copies the current notice to the clipboard.
    /// </summary>
    /// <remarks>
    /// Clipboard access can fail -- another process can hold the clipboard open -- and that must not take the
    /// application down over a copy button.
    /// </remarks>
    /// <summary>Group fader released: the device volumes hold the result, so the fader returns to rest.</summary>
    private void OnMasterNudgeCompleted(object sender, RoutedEventArgs e) => _viewModel.EndMasterNudge();

    /// <summary>Steps one device's delay down by one step.</summary>
    private void OnDelayDown(object sender, RoutedEventArgs e) => StepDelay(sender, up: false);

    /// <summary>Steps one device's delay up by one step.</summary>
    private void OnDelayUp(object sender, RoutedEventArgs e) => StepDelay(sender, up: true);

    /// <summary>
    /// Applies one delay step to the device whose row was clicked.
    /// </summary>
    /// <remarks>
    /// The button lives inside a DataTemplate, so the target device comes from the sender's DataContext rather
    /// than from a field: there is one handler for every row, and the row it was pressed in decides which
    /// device moves.
    /// </remarks>
    private static void StepDelay(object sender, bool up)
    {
        if (sender is not FrameworkElement { DataContext: DeviceViewModel device })
        {
            return;
        }

        if (up)
        {
            device.IncreaseDelay();
        }
        else
        {
            device.DecreaseDelay();
        }
    }

    private void OnCopyNotice(object sender, RoutedEventArgs e)
    {
        string? text = _viewModel.NoticeText;

        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception)
        {
            // The clipboard was busy; the text remains selectable in the field, so there is nothing to report.
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



    private void OnSyncModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising || SyncModeBox.SelectedItem is not ComboBoxItem { Tag: SyncMode mode })
        {
            return;
        }

        _viewModel.Mode = mode;
    }

    /// <summary>
    /// The single start/stop button: one control, and the state decides which way it goes.
    /// </summary>
    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsRunning)
        {
            await _viewModel.StopAsync();
        }
        else
        {
            _viewModel.Start();
        }
    }



    private void OnSave(object sender, RoutedEventArgs e) => _viewModel.SaveCurrentStateToActiveProfile();

    private void OnPrimaryDeviceClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceViewModel device })
        {
            _viewModel.SetPrimaryDevice(device);
        }
    }
}
