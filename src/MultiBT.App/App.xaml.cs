using System.Windows;
using System.Windows.Threading;
using MultiBT.App.Localization;
using MultiBT.App.Services;
using MultiBT.App.Tray;
using MultiBT.App.ViewModels;

namespace MultiBT.App;

/// <summary>
/// Application entry point: single-instance enforcement, the tray icon, and the main window.
/// </summary>
/// <remarks>
/// <para>
/// The tray is started here and is what keeps the process alive, so this class owns the lifetime of
/// the view model and the window. Previously <c>TrayHost</c> and <c>SingleInstance</c> existed but
/// were never constructed, which meant the tray icon never appeared and single-instance was not
/// enforced — the app looked finished but its primary UI surface did not exist.
/// </para>
/// </remarks>
public partial class App : Application
{
    private SingleInstance? _singleInstance;
    private TrayHost? _tray;
    private MainViewModel? _viewModel;
    private MainWindow? _window;
    private bool _exiting;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // An unhandled exception in the audio path must not silently kill the process with no
        // trace: a mirror that vanishes mid-playback with no message is the worst possible
        // failure mode for this app.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // Exit paths that are not the user pressing stop. Each of them has to undo the audio routing, or the
        // machine is left rendering into a cable nobody can hear -- silence with no cause the user could guess.
        // The one case none of this covers is a forced kill, which is why the routing is recorded on disk
        // before it is applied and put back by the next launch (see MainViewModel.RecoverLeftoverRouting).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RestoreRoutingQuietly();
        SessionEnding += (_, _) => RestoreRoutingQuietly();

        _singleInstance = new SingleInstance(out bool isFirstInstance);

        if (!isFirstInstance)
        {
            // Another instance is already running and has just been asked to show itself.
            // Exit WITHOUT creating a window or an audio engine.
            Shutdown();
            return;
        }

        _singleInstance.ActivateRequested += (_, _) => Dispatcher.Invoke(ShowMainWindow);

        _viewModel = new MainViewModel();
        _window = new MainWindow(_viewModel);
        _tray = new TrayHost();

        // A record left on disk means the previous run never got to undo its routing, which can only happen when
        // it did not exit cleanly. Put the machine's audio back before anything else touches it.
        _viewModel.RecoverLeftoverRouting();

        WireTray(_tray, _viewModel);

        // Keep the tray in step with the view model: device list, profile, pause state.
        _viewModel.PropertyChanged += (_, _) => RefreshTray();
        _viewModel.Devices.CollectionChanged += (_, _) => RefreshTray();

        // The tray carries its own labels and is not made of bindings, so a language change has to be
        // pushed into it. The view model re-localises the text it composes on the same event.
        Localizer.Instance.LanguageChanged += (_, _) =>
        {
            _tray?.ApplyLanguage();
            RefreshTray();
        };

        RefreshTray();
        ShowMainWindow();
    }

    /// <summary>Shows and focuses the main window, recreating it if it was closed.</summary>
    private void ShowMainWindow()
    {
        if (_viewModel is null)
        {
            return;
        }

        _window ??= new MainWindow(_viewModel);

        _window.Show();

        if (_window.WindowState == WindowState.Minimized)
        {
            _window.WindowState = WindowState.Normal;
        }

        _window.Activate();
    }

    private void WireTray(TrayHost tray, MainViewModel viewModel)
    {
        tray.OpenMainWindowRequested += (_, _) => ShowMainWindow();

        tray.ExitRequested += (_, _) => _ = ExitAsync();

        tray.PauseRequested += (_, paused) =>
        {
            viewModel.SetPaused(paused);
            RefreshTray();
        };

        tray.SwitchProfileRequested += (_, profileId) => _ = ActivateProfileSafelyAsync(viewModel, profileId);

        tray.ToggleDeviceRequested += (_, deviceKey) =>
        {
            DeviceViewModel? device = viewModel.Devices.FirstOrDefault(d => d.Key == deviceKey);
            if (device is null)
            {
                return;
            }

            // Update the bound flag first so the main window and the menu agree immediately, then
            // let the view model start/stop the channel.
            device.IsEnabled = !device.IsEnabled;
            _ = ToggleDeviceSafelyAsync(viewModel, device);
        };
    }

    private async Task ActivateProfileSafelyAsync(MainViewModel viewModel, string profileId)
    {
        try
        {
            await viewModel.ActivateProfileAsync(profileId).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{Localizer.Instance["Dialog.ProfileSwitchFailed"]}\n\n{ex.Message}",
                Localizer.Instance["App.Title"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        RefreshTray();
    }

    private async Task ToggleDeviceSafelyAsync(MainViewModel viewModel, DeviceViewModel device)
    {
        try
        {
            await viewModel.ToggleDeviceAsync(device).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"{Localizer.Instance.Format("Dialog.DeviceToggleFailed", device.DisplayName)}\n\n{ex.Message}",
                Localizer.Instance["App.Title"],
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        RefreshTray();
    }

    private void RefreshTray()
    {
        if (_tray is null || _viewModel is null)
        {
            return;
        }

        _tray.UpdateProfiles(
            _viewModel.Profiles.Select(p => (p.Id, p.Name, p.Icon)),
            _viewModel.ActiveProfileId);

        _tray.UpdateDevices(
            _viewModel.Devices.Select(d => (d.Key, d.DisplayName, d.IsEnabled, d.ConnectionHint ?? string.Empty)));

        _tray.UpdateState(_viewModel.IsPaused, _viewModel.Devices.Count(d => d.IsEnabled));
    }

    /// <summary>
    /// Shuts down cleanly: fade each channel, join the playback threads, then exit.
    /// </summary>
    private async Task ExitAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        if (_window is not null)
        {
            _window.AllowClose = true;
        }

        _tray?.Dispose();
        _tray = null;

        if (_viewModel is not null)
        {
            try
            {
                await _viewModel.DisposeAsync().ConfigureAwait(true);
            }
            catch (Exception)
            {
                // Never block exit on a teardown failure.
            }
        }

        _singleInstance?.Dispose();
        _singleInstance = null;

        Shutdown();
    }

    /// <summary>
    /// Undoes the audio routing while the process is on its way out.
    /// </summary>
    /// <remarks>
    /// Swallows everything: this runs during a shutdown, from an event that cannot report a failure usefully, and
    /// the alternative to a quiet failure is an exception thrown while the runtime is tearing down. The record on
    /// disk is what covers the cases this cannot.
    /// </remarks>
    private void RestoreRoutingQuietly()
    {
        try
        {
            _viewModel?.RestoreDefaultOutputIfRouted();
        }
        catch (Exception)
        {
            // Best effort. The next launch finds the record and finishes the job.
        }
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"{Localizer.Instance["Dialog.UnexpectedError"]}\n\n{e.Exception}",
            Localizer.Instance["Dialog.Error"],
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        // Keep running: a single failed UI interaction should not take down an active mirror -- and so the audio
        // routing is deliberately NOT undone here. The mirror may well still be running.
        e.Handled = true;
    }

    /// <summary>
    /// Reports a fatal exception, having first handed the machine's audio back.
    /// </summary>
    /// <remarks>
    /// This one is not survivable, so the routing has to be undone before the process ends: a fatal error that
    /// leaves Windows rendering into a silent cable is a second failure on top of the first.
    /// </remarks>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        RestoreRoutingQuietly();

        MessageBox.Show(
            $"{Localizer.Instance["Dialog.FatalError"]}\n\n{e.ExceptionObject}",
            Localizer.Instance["Dialog.Error"],
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Observe it so the finalizer does not escalate it on a later collection.
        e.SetObserved();
    }
}
