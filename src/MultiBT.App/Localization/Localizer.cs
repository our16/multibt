using System.ComponentModel;
using System.Globalization;
using MultiBT.Core.Config;

namespace MultiBT.App.Localization;

/// <summary>
/// Runtime-switchable UI strings.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT <c>.resx</c> + <c>ResourceManager</c>. Resource files are resolved at page-load
/// time, so switching language requires reloading every element or restarting the app, and the
/// generated designer members are compile-time constants that XAML binds by name. A dictionary behind
/// an <c>INotifyPropertyChanged</c> indexer instead lets every bound string update IN PLACE the moment
/// the language changes, which is what "支持切换" should mean.
/// </para>
/// <para>
/// The XAML binding is <c>{Binding Source={x:Static loc:Localizer.Instance}, Path=[Key]}</c>. Raising
/// <c>PropertyChanged("Item[]")</c> is what tells WPF that every indexer result is stale — without it
/// the switch would silently do nothing.
/// </para>
/// <para>
/// Missing keys return the key itself rather than an empty string, so a gap is immediately visible in
/// the UI instead of looking like a layout bug.
/// </para>
/// </remarks>
public sealed class Localizer : INotifyPropertyChanged
{
    private static readonly Lazy<Localizer> LazyInstance = new(() => new Localizer());

    private UiLanguage _language = UiLanguage.Chinese;

    private Localizer()
    {
    }

    /// <summary>Shared instance, for XAML <c>x:Static</c> binding.</summary>
    public static Localizer Instance => LazyInstance.Value;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Current language.</summary>
    public UiLanguage Language => _language;

    /// <summary>True when English is active, for UI that needs a plain bool.</summary>
    public bool IsEnglish => _language == UiLanguage.English;

    /// <summary>Looks up a string in the current language.</summary>
    /// <param name="key">Key from the table below.</param>
    public string this[string key] => Get(key, _language);

    /// <summary>Looks up a string, formatting it with <see cref="string.Format(string,object?)"/> when arguments are given.</summary>
    public string Format(string key, params object?[] arguments)
    {
        string template = Get(key, _language);
        return arguments.Length == 0 ? template : string.Format(CultureInfo.CurrentCulture, template, arguments);
    }

    /// <summary>Switches language and notifies every bound string.</summary>
    public void SetLanguage(UiLanguage language)
    {
        if (_language == language)
        {
            return;
        }

        _language = language;

        // "Item[]" is the WPF convention for "all indexer results changed".
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnglish)));

        // Dynamic strings composed in view models are not indexer-bound, so give them a chance to
        // rebuild themselves.
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after the language changes, for view models that build strings in code.</summary>
    public event EventHandler? LanguageChanged;

    private static string Get(string key, UiLanguage language)
    {
        Dictionary<string, string> table = language == UiLanguage.English ? English : Chinese;
        return table.TryGetValue(key, out string? value) ? value : key;
    }

    // ---------------------------------------------------------------------------------------
    // String tables. Keys are stable identifiers; every key here must exist in BOTH tables.
    // ---------------------------------------------------------------------------------------

    private static readonly Dictionary<string, string> Chinese = new(StringComparer.Ordinal)
    {
        // window / header
        ["App.Title"] = "MultiBT",
        ["App.Heading"] = "🔊 MultiBT",
        ["App.WindowTitle"] = "MultiBT — 多设备音频",
        ["App.Language"] = "语言：",

        // device list
        ["Devices.Group"] = "输出设备",
        ["Devices.Refresh"] = "刷新设备",
        ["Devices.Refresh.Tip"] = "重新枚举设备，并刷新各设备的 Windows 音量",
        ["Devices.AutoMatch"] = "自动对齐响度",
        ["Devices.AutoMatch.Tip"] = "把所有已启用设备的音量统一到其中最小的一个（只降不升）。音箱灵敏度差异仍需手动微调。",
        ["Devices.LatencyPreset"] = "延迟预设 (ms)：",
        ["Devices.SyncMode"] = "同步模式：",
        ["SyncMode.AlignAll"] = "全部对齐（音乐）",
        ["SyncMode.WiredOnly"] = "只对齐有线组（视频）",
        ["Language.Chinese"] = "中文",
        ["Language.English"] = "English",
        ["Devices.None"] = "（未发现设备）",

        // primary marker
        ["Primary.Marker"] = "主设备",
        ["Primary.Set"] = "设为主设备",
        ["Primary.Tip"] = "设为主设备（捕获源），并同步切换 Windows 默认输出设备",

        // per-device volume
        ["Volume.Label"] = "设备音量",
        ["Volume.Tip"] = "该设备在 Windows 中的实际输出音量。拖动即时生效。",
        ["Volume.Display"] = "设备音量 {0}%",
        ["Volume.Unknown"] = "设备音量不可读（该设备无法控制音量）",
        ["Volume.MutedWarning"] = "⚠ 该设备在 Windows 中被静音",
        ["Delay.Label"] = "延迟",
        ["Delay.Tip"] = "手动延迟该设备，0–2000 毫秒。没有测量值时可用来对齐：偏快的设备加延迟。范围较宽，用方向键可精确到 1 毫秒。",

        // transport
        ["Transport.Bluetooth"] = "蓝牙",
        ["Transport.Usb"] = "USB",
        ["Transport.Hdmi"] = "HDMI",
        ["Transport.Other"] = "其他",
        ["Transport.PairedNotConnected"] = "已配对未连接",

        // latency summary
        ["Latency.NotMeasured"] = "未测量",
        ["Latency.Measured"] = "测量 {0} ms",
        ["Latency.LowConfidence"] = "低置信度",
        ["Latency.Compensation"] = "补偿 {0} ms",
        ["Latency.Trim"] = "微调 {0} ms",
        ["Latency.Effective"] = "生效 {0} ms",
        ["Latency.Stale"] = "⚠ 测量已过期",

        // system latency
        ["SystemLatency.Ok"] = "系统总延迟 {0} ms",
        ["SystemLatency.Warning"] = "⚠ 系统总延迟 {0} ms — 超过约 125 ms 的唇音同步阈值，看视频会不同步",
        ["SystemLatency.Hint"] = "对齐到最慢的设备会把整个系统的端到端延迟设为该设备的延迟。蓝牙音箱通常在 150–400 ms，因此「全部已同步」与「能看视频」可能互斥——需要看视频时请切到 WiredOnly。",

        // diagnostics
        ["Diagnostics.Group"] = "诊断（漂移修正 / 缓冲填充 / 信号电平）",
        ["Diagnostics.None"] = "—",
        ["Diagnostics.SignalSilent"] = "无信号",
        ["Diagnostics.Signal"] = "信号 {0} dBFS",
        ["Diagnostics.EndpointMuted"] = "⚠ 端点已静音",
        ["Diagnostics.EndpointVolumeZero"] = "⚠ 端点音量为 0%",
        ["Diagnostics.EndpointVolume"] = "端点音量 {0}%",
        ["Diagnostics.Fill"] = "填充 {0}–{1} ms",
        ["Diagnostics.NoReads"] = "填充 —（无读取）",
        ["Diagnostics.SilenceFraction"] = "⚠ 单次读取中 {0}% 是静音",
        ["Diagnostics.Starved"] = "⚠ 饿死",
        ["Diagnostics.CorrectionAtLimit"] = "修正已达上限 ({0} ppm)",
        ["Diagnostics.Resync"] = "重同步 {0}",

        // transport controls
        ["Transport.Start"] = "▶ 开始同步",
        ["Transport.Stop"] = "■ 停止",
        ["Transport.Save"] = "保存设置",
        ["Transport.NotVerified"] = "音频通路尚未在真机做听音验证 —— 见 docs/SPEC.md §10 与 docs/PITFALLS.md §D。",

        // status line
        ["Status.Idle"] = "就绪",
        ["Status.Stopped"] = "已停止。",
        ["Status.Paused"] = "已暂停多设备输出。",
        ["Status.Resumed"] = "已恢复输出。",
        ["Status.SelectDevice"] = "请先勾选至少一个输出设备。",
        ["Status.NoSource"] = "没有可用的输出设备作为捕获源。",
        ["Status.NoDeviceOpened"] = "无法打开任何设备。",
        ["Status.SettingsSaved"] = "设置已保存。",
        ["Sink.Label"] = "捕获出口",
        ["Sink.Tip"] = "选择一个虚拟声卡：Windows 会把系统声音渲染到它，我们再捕获并送给所有音箱。这样每个音箱（包括主设备）都能调延迟和音量。",
        ["Sink.Auto"] = "（未使用虚拟声卡）",
        ["Sink.Applied"] = "已把捕获出口设为虚拟声卡，并切换 Windows 默认输出设备。",
        ["Sink.Cleared"] = "已取消捕获出口。",
        ["Sink.SwitchFailed"] = "无法切换 Windows 默认输出设备：{0}",
        ["Sink.GetCable"] = "获取虚拟声卡",
        ["Sink.GetCable.Tip"] = "打开 VB-CABLE 的官方下载页（免费）。装好后回到这里即可——MultiBT 会自动检测到它。",
        ["Sink.NoCableWarning"] = "⚠ 未检测到虚拟声卡。当前 Windows 直接渲染到默认设备，而那个设备始终会自己发声——它无法被延迟或调音量。要控制全部设备：装一个虚拟声卡（VB-CABLE、VoiceMeeter、Virtual Audio Cable 都可以），装好后回到这里，MultiBT 会自动检测并接管。",
        ["Input.Detected"] = "{0}（自动）",
        ["Input.None"] = "未检测到虚拟声卡 · 音频取自系统默认输出",
        ["Input.Tip"] = "输入来源自动检测，不需要手动选择：装了虚拟声卡时，开始同步会把 Windows 默认输出切到它，再从它取声音送给所有音箱。",
        ["Status.Undone"] = "已撤销上一步设置。",
        ["Status.Redone"] = "已恢复下一步设置。",
        ["Status.UndoFailed"] = "撤销失败：{0}",
        ["Status.Profile"] = "场景：{0}",
        ["Status.Found"] = "发现 {0} 个输出端点，其中 {1} 个蓝牙设备。",
        ["Status.Mirroring"] = "正在同步到 {0} 个设备（请求延迟 {1} ms）。",
        ["Status.PrimarySet"] = "主设备已设置为：{0}",
        ["Status.PrimarySetAndSwitched"] = "主设备已设为「{0}」，并已同步切换 Windows 默认输出设备。",
        ["Status.PrimarySwitchFailed"] = "主设备已设为「{0}」，但切换 Windows 默认输出设备失败：{1}",
        ["Status.Added"] = "已添加 {0}。正在同步到 {1} 个设备。",
        ["Status.Removed"] = "已移除设备。正在同步到 {0} 个设备。",
        ["Status.DeviceUnavailable"] = "不可用",
        ["Status.DeviceSkippedSource"] = "已跳过（捕获源）",
        ["Status.Running"] = "运行中",
        ["Status.Failed"] = "失败",
        ["Status.AutoMatchNone"] = "无法自动对齐：没有读到任何设备的端点音量。请手动调整滑杆。",
        ["Status.AutoMatchDone"] = "已把 {0} 个设备的音量统一到 {1}%（取其中最小的一个）。音箱灵敏度差异仍需手动微调滑杆。",
        ["Status.VolumeFailed"] = "无法调整「{0}」的设备音量。",
        ["Status.SourceCannotBeOutput"] = "「{0}」正在作为捕获源，不能同时作为输出（否则会形成音频回授）。",
        ["Status.RestartingForSource"] = "正在切换到「{0}」并重建输出...",
        ["Status.CouldNotStart"] = "无法开始：{0}",
        ["Status.CouldNotToggle"] = "无法切换 {0}：{1}",
        ["Status.AudioError"] = "音频错误：{0}",

        // capture source warning
        ["Capture.NotDefault"] = "⚠ 主设备「{0}」不是 Windows 默认输出设备。MultiBT 捕获的是「主设备正在播放的声音」，而系统声音只会送到 Windows 默认设备——所以其他设备会完全没有声音。请把「主设备」改为 Windows 默认输出设备，或在 Windows 设置里把这个设备设为默认输出。",

        // tray
        ["Tray.Tooltip"] = "MultiBT — 多设备音频",
        ["Tray.TooltipState"] = "MultiBT — {0}（{1} 个设备）",
        ["Tray.StatePaused"] = "已暂停",
        ["Tray.StateActive"] = "运行中",
        ["Tray.StateNoDevices"] = "无设备",
        ["Tray.Profiles"] = "场景",
        ["Tray.Devices"] = "设备",
        ["Tray.Pause"] = "暂停多设备输出",
        ["Tray.Resume"] = "恢复多设备输出",
        ["Tray.Open"] = "打开主窗口",
        ["Tray.Exit"] = "退出",
        ["Tray.NoProfiles"] = "（无场景）",
        ["Tray.NoDevices"] = "（无设备）",

        // dialogs
        ["Dialog.Error"] = "MultiBT — 错误",
        ["Dialog.UnexpectedError"] = "发生意外错误：",
        ["Dialog.FatalError"] = "发生致命错误：",
        ["Dialog.ProfileSwitchFailed"] = "无法切换场景：",
        ["Dialog.DeviceToggleFailed"] = "无法切换 {0}：",
    };

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["App.Title"] = "MultiBT",
        ["App.Heading"] = "🔊 MultiBT",
        ["App.WindowTitle"] = "MultiBT — Multi-Device Audio",
        ["App.Language"] = "Language:",

        ["Devices.Group"] = "Output devices",
        ["Devices.Refresh"] = "Refresh devices",
        ["Devices.Refresh.Tip"] = "Re-enumerate devices and refresh each device's Windows volume",
        ["Devices.AutoMatch"] = "Match loudness",
        ["Devices.AutoMatch.Tip"] = "Set every enabled device to the lowest volume among them (lowers only, never raises). Speaker sensitivity still needs manual trimming.",
        ["Devices.LatencyPreset"] = "Latency preset (ms):",
        ["Devices.SyncMode"] = "Sync mode:",
        ["SyncMode.AlignAll"] = "Align all (music)",
        ["SyncMode.WiredOnly"] = "Wired group only (video)",
        ["Language.Chinese"] = "中文",
        ["Language.English"] = "English",
        ["Devices.None"] = "(no devices found)",

        ["Primary.Marker"] = "Primary",
        ["Primary.Set"] = "Set as primary",
        ["Primary.Tip"] = "Make this the primary (capture source) and switch the Windows default output to match",

        ["Volume.Label"] = "Device volume",
        ["Volume.Tip"] = "This device's actual Windows output volume. Applies immediately while dragging.",
        ["Volume.Display"] = "Device volume {0}%",
        ["Volume.Unknown"] = "Device volume not readable (this device cannot be controlled)",
        ["Volume.MutedWarning"] = "⚠ This device is muted in Windows",
        ["Delay.Label"] = "Delay",
        ["Delay.Tip"] = "Manual delay for this device, 0-2000 ms. Use it when no measurement exists: add delay to the device that plays early. The range is wide, so use the arrow keys for 1 ms precision.",

        ["Transport.Bluetooth"] = "Bluetooth",
        ["Transport.Usb"] = "USB",
        ["Transport.Hdmi"] = "HDMI",
        ["Transport.Other"] = "Other",
        ["Transport.PairedNotConnected"] = "paired, not connected",

        ["Latency.NotMeasured"] = "not measured",
        ["Latency.Measured"] = "measured {0} ms",
        ["Latency.LowConfidence"] = "low confidence",
        ["Latency.Compensation"] = "compensation {0} ms",
        ["Latency.Trim"] = "trim {0} ms",
        ["Latency.Effective"] = "effective {0} ms",
        ["Latency.Stale"] = "⚠ measurement stale",

        ["SystemLatency.Ok"] = "System latency {0} ms",
        ["SystemLatency.Warning"] = "⚠ System latency {0} ms — beyond the ~125 ms lip-sync threshold, video will look out of sync",
        ["SystemLatency.Hint"] = "Aligning to the slowest device sets the whole system's end-to-end latency to that device. Bluetooth speakers are typically 150–400 ms, so \"everything in sync\" and \"usable for video\" can be mutually exclusive — switch to WiredOnly when watching video.",

        ["Diagnostics.Group"] = "Diagnostics (drift correction / buffer fill / signal level)",
        ["Diagnostics.None"] = "—",
        ["Diagnostics.SignalSilent"] = "signal SILENT",
        ["Diagnostics.Signal"] = "signal {0} dBFS",
        ["Diagnostics.EndpointMuted"] = "⚠ ENDPOINT MUTED",
        ["Diagnostics.EndpointVolumeZero"] = "⚠ endpoint volume 0%",
        ["Diagnostics.EndpointVolume"] = "endpoint volume {0}%",
        ["Diagnostics.Fill"] = "fill {0}–{1} ms",
        ["Diagnostics.NoReads"] = "fill — (no reads)",
        ["Diagnostics.SilenceFraction"] = "⚠ {0}% of a read was silence",
        ["Diagnostics.Starved"] = "⚠ starved",
        ["Diagnostics.CorrectionAtLimit"] = "correction at limit ({0} ppm)",
        ["Diagnostics.Resync"] = "resync {0}",

        ["Transport.Start"] = "▶ Start mirroring",
        ["Transport.Stop"] = "■ Stop",
        ["Transport.Save"] = "Save settings",
        ["Transport.NotVerified"] = "The audio path has not been verified by ear on real hardware — see docs/SPEC.md §10 and docs/PITFALLS.md §D.",

        ["Status.Idle"] = "Idle",
        ["Status.Stopped"] = "Stopped.",
        ["Status.Paused"] = "Paused multi-device output.",
        ["Status.Resumed"] = "Resumed output.",
        ["Status.SelectDevice"] = "Select at least one output device first.",
        ["Status.NoSource"] = "No render device available to mirror.",
        ["Status.NoDeviceOpened"] = "No device could be opened.",
        ["Status.SettingsSaved"] = "Settings saved.",
        ["Sink.Label"] = "Capture sink",
        ["Sink.Tip"] = "Pick a virtual audio cable: Windows renders system audio into it, and we capture that and feed every speaker. That makes delay and volume controllable on ALL of them, including the primary.",
        ["Sink.Auto"] = "(no virtual cable)",
        ["Sink.Applied"] = "Capture sink set to the virtual cable, and the Windows default output was switched to it.",
        ["Sink.Cleared"] = "Capture sink cleared.",
        ["Sink.SwitchFailed"] = "Could not switch the Windows default output: {0}",
        ["Sink.GetCable"] = "Get a virtual cable",
        ["Sink.GetCable.Tip"] = "Opens the official VB-CABLE download page (free). Once installed, just come back - MultiBT detects it automatically.",
        ["Sink.NoCableWarning"] = "⚠ No virtual audio cable detected. Windows currently renders straight to the default device, so that device always plays natively - it cannot be delayed or volume-controlled. To control every device, install a virtual cable (VB-CABLE, VoiceMeeter and Virtual Audio Cable all work); MultiBT detects it automatically once it is present.",
        ["Input.Detected"] = "{0} (auto)",
        ["Input.None"] = "no virtual cable detected - capturing the Windows default output",
        ["Input.Tip"] = "The input is detected automatically; there is nothing to choose. When a virtual cable is present, starting the mirror switches the Windows default output to it and captures that.",
        ["Status.Undone"] = "Undid the last settings change.",
        ["Status.Redone"] = "Redid the settings change.",
        ["Status.UndoFailed"] = "Undo failed: {0}",
        ["Status.Profile"] = "Profile: {0}",
        ["Status.Found"] = "Found {0} render endpoint(s), {1} Bluetooth.",
        ["Status.Mirroring"] = "Mirroring to {0} device(s) (requested latency {1} ms).",
        ["Status.PrimarySet"] = "Primary device set to: {0}",
        ["Status.PrimarySetAndSwitched"] = "Primary set to \"{0}\" and the Windows default output was switched to match.",
        ["Status.PrimarySwitchFailed"] = "Primary set to \"{0}\", but switching the Windows default output failed: {1}",
        ["Status.Added"] = "Added {0}. Mirroring to {1} device(s).",
        ["Status.Removed"] = "Removed device. Mirroring to {0} device(s).",
        ["Status.DeviceUnavailable"] = "unavailable",
        ["Status.DeviceSkippedSource"] = "skipped (capture source)",
        ["Status.Running"] = "running",
        ["Status.Failed"] = "failed",
        ["Status.AutoMatchNone"] = "Cannot match loudness: no device's endpoint volume could be read. Adjust the sliders manually.",
        ["Status.AutoMatchDone"] = "Set {0} device(s) to {1}% (the lowest among them). Speaker sensitivity still needs a manual trim.",
        ["Status.VolumeFailed"] = "Could not change the device volume of \"{0}\".",
        ["Status.CouldNotStart"] = "Could not start: {0}",
        ["Status.CouldNotToggle"] = "Could not change {0}: {1}",
        ["Status.AudioError"] = "Audio error: {0}",

        ["Capture.NotDefault"] = "⚠ Primary \"{0}\" is not the Windows default output device. MultiBT captures what the PRIMARY endpoint is rendering, and Windows only sends system audio to the default — so the other devices will be completely silent. Set the primary to the Windows default output, or make this device the default in Windows Settings.",

        ["Tray.Tooltip"] = "MultiBT — multi-device audio",
        ["Tray.TooltipState"] = "MultiBT — {0} ({1} device(s))",
        ["Tray.StatePaused"] = "paused",
        ["Tray.StateActive"] = "active",
        ["Tray.StateNoDevices"] = "no devices",
        ["Tray.Profiles"] = "Profiles",
        ["Tray.Devices"] = "Devices",
        ["Tray.Pause"] = "Pause multi-device output",
        ["Tray.Resume"] = "Resume multi-device output",
        ["Tray.Open"] = "Open MultiBT",
        ["Tray.Exit"] = "Exit",
        ["Tray.NoProfiles"] = "(no profiles)",
        ["Tray.NoDevices"] = "(no devices)",

        ["Dialog.Error"] = "MultiBT — Error",
        ["Dialog.UnexpectedError"] = "Unexpected error:",
        ["Dialog.FatalError"] = "A fatal error occurred:",
        ["Dialog.ProfileSwitchFailed"] = "Could not switch profile:",
        ["Dialog.DeviceToggleFailed"] = "Could not change {0}:",
    };
}
