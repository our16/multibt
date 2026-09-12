using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using H.NotifyIcon;

namespace MultiBT.App.Tray;

/// <summary>
/// The system tray icon and its context menu — the primary UI for MultiBT.
/// </summary>
/// <remarks>
/// <para>
/// Daily use must never require opening the main window (docs/GOALS.md G6). This menu is therefore
/// the whole product surface for normal use: profile switching, device selection, pause/resume,
/// open window, exit.
/// </para>
/// <para>
/// The submenus are rebuilt from data via <see cref="UpdateProfiles"/> and <see cref="UpdateDevices"/>
/// rather than being static, because a menu of hardcoded items cannot reflect the user's actual
/// devices or profiles — and an empty <c>MenuItem</c> with a label and no children is exactly how
/// "the tray does nothing when I click Profiles" happens.
/// </para>
/// </remarks>
public sealed class TrayHost : IDisposable
{
    private readonly TaskbarIcon _trayIcon;
    private readonly MenuItem _profilesMenu;
    private readonly MenuItem _devicesMenu;
    private readonly MenuItem _pauseMenu;
    private bool _disposed;
    private bool _isPaused;

    /// <summary>Raised when the user wants to open the main window.</summary>
    public event EventHandler? OpenMainWindowRequested;

    /// <summary>Raised when the user toggles pause. The argument is the DESIRED paused state.</summary>
    public event EventHandler<bool>? PauseRequested;

    /// <summary>Raised when the user selects a profile, carrying its id.</summary>
    public event EventHandler<string>? SwitchProfileRequested;

    /// <summary>Raised when the user toggles a device, carrying its device key.</summary>
    public event EventHandler<string>? ToggleDeviceRequested;

    /// <summary>Raised when the user wants to exit the application.</summary>
    public event EventHandler? ExitRequested;

    /// <summary>Initializes the tray icon and its menu.</summary>
    public TrayHost()
    {
        _profilesMenu = new MenuItem { Header = "_Profiles" };
        _devicesMenu = new MenuItem { Header = "_Devices" };

        _pauseMenu = new MenuItem
        {
            Header = "_Pause multi-device output",
            Command = new DelegateCommand(OnPauseClick),
        };

        var menu = new ContextMenu();
        menu.Items.Add(_profilesMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(_devicesMenu);
        menu.Items.Add(new Separator());
        menu.Items.Add(_pauseMenu);
        menu.Items.Add(new MenuItem
        {
            Header = "_Open MultiBT",
            Command = new DelegateCommand(() => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty)),
        });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem
        {
            Header = "E_xit",
            Command = new DelegateCommand(() => ExitRequested?.Invoke(this, EventArgs.Empty)),
        });

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "MultiBT — multi-device audio",

            // LEFT click opens the window directly; RIGHT click shows the menu.
            //
            // Without an explicit LeftClickCommand the notification area has nothing to do on a left
            // click, so the only way in was to discover the right-click menu — which reads as "the
            // tray icon does nothing".
            LeftClickCommand = new DelegateCommand(() => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty)),
            DoubleClickCommand = new DelegateCommand(() => OpenMainWindowRequested?.Invoke(this, EventArgs.Empty)),
            Icon = LoadTrayIcon(),
            ContextMenu = menu,
        };

        // Force the icon to materialise. Without this the icon can stay invisible until something
        // else touches it, which looks exactly like "the tray is not running".
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    /// <summary>
    /// Loads the tray icon from the embedded multi-size .ico.
    /// </summary>
    /// <remarks>
    /// Loaded from the .ico rather than via <c>ExtractAssociatedIcon</c> so Windows can choose the
    /// frame that matches the notification area's actual size. The Result is held in a field: the tray
    /// keeps a native handle to it, and a collected Icon would leave a blank tray slot.
    /// </remarks>
    private static System.Drawing.Icon? LoadTrayIcon()
    {
        try
        {
            Uri uri = new("pack://application:,,,/Assets/multibt.ico", UriKind.Absolute);

            using Stream stream = Application.GetResourceStream(uri).Stream;

            // 32 px is the largest frame the notification area ever needs; Windows downsamples from
            // there for 16/20/24 px and at any DPI scale.
            return new System.Drawing.Icon(stream, new System.Drawing.Size(32, 32));
        }
        catch (Exception)
        {
            // A missing or malformed icon must never stop the app from starting — it would just
            // launch without a tray presence, which is worse than a generic icon.
            return null;
        }
    }

    /// <summary>Gets or sets the tray tooltip text.</summary>
    public string ToolTipText
    {
        get => _trayIcon.ToolTipText ?? string.Empty;
        set => _trayIcon.ToolTipText = value;
    }

    /// <summary>
    /// Rebuilds the profile submenu.
    /// </summary>
    /// <param name="profiles">Profile id, display name and icon.</param>
    /// <param name="activeProfileId">The profile to mark as active.</param>
    public void UpdateProfiles(
        IEnumerable<(string Id, string Name, string? Icon)> profiles,
        string? activeProfileId)
    {
        _profilesMenu.Items.Clear();

        foreach ((string id, string name, string? icon) in profiles)
        {
            bool isActive = string.Equals(id, activeProfileId, StringComparison.Ordinal);
            string header = string.IsNullOrEmpty(icon) ? name : $"{icon} {name}";

            var item = new MenuItem
            {
                Header = (isActive ? "● " : "○ ") + header,
                IsCheckable = false,
                Command = new DelegateCommand(() => SwitchProfileRequested?.Invoke(this, id)),
            };

            _profilesMenu.Items.Add(item);
        }

        if (_profilesMenu.Items.Count == 0)
        {
            _profilesMenu.Items.Add(new MenuItem { Header = "(no profiles)", IsEnabled = false });
        }
    }

    /// <summary>Rebuilds the device submenu with checkable entries.</summary>
    /// <param name="devices">Device key, display name, enabled flag and transport label.</param>
    public void UpdateDevices(IEnumerable<(string Key, string Name, bool IsEnabled, string Hint)> devices)
    {
        _devicesMenu.Items.Clear();

        foreach ((string key, string name, bool isEnabled, string hint) in devices)
        {
            var item = new MenuItem
            {
                // The hint is optional and says why a device is silent, so it is appended only when it has
                // something to say. The transport TYPE no longer appears next to an output at all.
                Header = string.IsNullOrEmpty(hint) ? name : $"{name}  ({hint})",
                IsCheckable = true,
                IsChecked = isEnabled,
            };

            string capturedKey = key;
            item.Click += (_, _) => ToggleDeviceRequested?.Invoke(this, capturedKey);

            _devicesMenu.Items.Add(item);
        }

        if (_devicesMenu.Items.Count == 0)
        {
            _devicesMenu.Items.Add(new MenuItem { Header = "(no devices)", IsEnabled = false });
        }
    }

    /// <summary>
    /// Updates the tray state.
    /// </summary>
    /// <param name="isPaused">Whether output is currently paused.</param>
    /// <param name="activeDeviceCount">Number of devices currently mirrored to.</param>
    public void UpdateState(bool isPaused, int activeDeviceCount)
    {
        _isPaused = isPaused;
        _pauseMenu.Header = isPaused ? "_Resume multi-device output" : "_Pause multi-device output";

        string state = isPaused ? "paused" : activeDeviceCount > 0 ? "active" : "no devices";
        _trayIcon.ToolTipText = $"MultiBT — {state} ({activeDeviceCount} device(s))";
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon.Dispose();
    }

    /// <summary>
    /// Raises the pause request with the DESIRED state.
    /// </summary>
    /// <remarks>
    /// The previous implementation always passed <c>true</c>, which made pause a one-way trap: once
    /// paused from the tray there was no way to resume from the tray, and the menu label never
    /// changed to reflect it.
    /// </remarks>
    private void OnPauseClick() => PauseRequested?.Invoke(this, !_isPaused);

    /// <summary>
    /// Minimal command for menu items.
    /// </summary>
    /// <remarks>
    /// <see cref="CanExecuteChanged"/> is implemented with real (if empty) accessors rather than a
    /// bare field, because a field-like event on a command that never raises it produces a "never
    /// used" warning — and the warning is a genuine signal that the command never re-evaluates.
    /// These commands are always executable, so there is nothing to re-evaluate.
    /// </remarks>
    private sealed class DelegateCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
