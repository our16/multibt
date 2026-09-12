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
        ["Delay.Down.Tip"] = "减少 5 ms",
        ["Master.Tip"] = "把所有已勾选设备的音量一起往前或往后推。任何一台到达 0% 或 100% 就推不动了，所以你调好的相对平衡不会被破坏。松开后推杆回到中间，音量留在设备上。",
        ["Delay.Up.Tip"] = "增加 5 ms",
        ["Master.Label"] = "总音量",
        ["Spatial.None"] = "未设方位",
        ["Spatial.Right"] = "右{0}m",
        ["Spatial.Left"] = "左{0}m",
        ["Spatial.Front"] = "前{0}m",
        ["Spatial.Back"] = "后{0}m",
        ["Spatial.Up"] = "上{0}m",
        ["Spatial.Down"] = "下{0}m",
        ["Spatial.Tip"] = "设备相对你的位置，单位米。三个框依次是：右（负数为左）、前（负数为后）、上（负数为下）。留空或 0 表示未设方位，该设备不受影响。",
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
        ["Latency.Estimated"] = "估算补偿 {0} ms",
        ["Latency.NoCompensation"] = "无补偿",
        ["Latency.Measured"] = "测量 {0} ms",
        ["Latency.LowConfidence"] = "低置信度",
        ["Latency.Compensation"] = "补偿 {0} ms",
        ["Latency.Trim"] = "微调 {0} ms",
        ["Latency.Effective"] = "生效 {0} ms",
        ["Latency.Stale"] = "⚠ 测量已过期",

        // system latency
        ["SystemLatency.Ok"] = "系统总延迟 {0} ms",
        ["SystemLatency.Warning"] = "⚠ 系统总延迟 {0} ms — 超过约 125 ms 的唇音同步阈值，看视频会不同步",
        ["SystemLatency.Hint"] = "对齐到最慢的设备会把系统延迟设为它的延迟；蓝牙通常 150–400 ms，看视频请改用 WiredOnly。",

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
        ["Transport.NotVerified"] = "音频通路尚未做真机听音验证。",

        // status line
        ["Status.Idle"] = "就绪",
        ["Status.Stopped"] = "已停止。",
        ["Status.Paused"] = "已暂停多设备输出。",
        ["Status.Resumed"] = "已恢复输出。",
        ["Status.SelectDevice"] = "请先勾选至少一个输出设备。",
        ["Status.NoSource"] = "没有可用的输出设备作为捕获源。",
        ["Status.NoDeviceOpened"] = "无法打开任何设备。",
        ["Status.SettingsSaved"] = "设置已保存。",
        ["Sink.Label"] = "音频输入",
        ["Sink.Tip"] = "虚拟声卡或真实设备都可以。选「自动」时跟随 Windows 默认输出。",
        ["Sink.Auto"] = "自动（跟随 Windows 默认输出）",
        ["Sink.Applied"] = "已设置音频输入。点「开始同步」后，Windows 默认输出会自动切到它，停止时恢复。",
        ["Sink.Cleared"] = "音频输入已设为自动。",
        ["Sink.SwitchFailed"] = "无法切换 Windows 默认输出设备：{0}",
        ["Sink.GetCable"] = "获取虚拟声卡 ▾",
        ["Sink.GetCable.Tip"] = "推荐几个虚拟声卡，点击会打开它们的官网。MultiBT 不提供下载。",
        // recommended virtual audio devices (links out; MultiBT ships none of them)
        ["Recommend.VbCable"] = "VB-CABLE",
        ["Recommend.VbCable.Note"] = "推荐。免费，单条虚拟线，Windows 直接可用。",
        ["Recommend.VoiceMeeter"] = "VoiceMeeter",
        ["Recommend.VoiceMeeter.Note"] = "免费，含多条虚拟线和调音台；比本程序需要的重。",
        ["Recommend.VirtualAudioCable"] = "Virtual Audio Cable",
        ["Recommend.VirtualAudioCable.Note"] = "Lite 版免费但只支持 1 条线；试用版每 30 分钟会有女声提示音。",
        ["Recommend.MttDriver"] = "Virtual Audio Driver（开源）",
        ["Recommend.MttDriver.Note"] = "MIT 开源、免费；但安装需要开启 Windows 测试签名模式。",
        ["Recommend.Repository"] = "源码仓库（GitHub）",
        ["Notice.Copy"] = "复制",
        ["Recommend.Note"] = "MultiBT 只做推荐，不提供下载；请在官网自行安装。",
        ["Input.RealDeviceNote"] = "「{0}」是输入设备：它自己原生播放，延迟和音量不受控，也不会收到副本。",
        ["Input.Type.VirtualCable"] = "虚拟声卡",
        ["Input.RealDeviceNotDefaultNote"] = "⚠ Windows 尚未把声音送进「{0}」，现在同步会是静音。把默认输出改成它，或改选「自动」。",
        ["Input.CableNotRoutedNote"] = "⚠ Windows 尚未把声音送进「{0}」，现在同步会是静音；开始同步时会自动切换。",
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
        ["Status.DeviceOpenFailed"] = "打不开",
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
        ["Capture.NotDefault"] = "⚠ 当前输入「{0}」不是 Windows 默认输出设备，系统声音不会送到它，其他设备会没声音。把它设为默认输出，或把输入改为「自动」。",

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

        ["Delay.Down.Tip"] = "Subtract 5 ms",
        ["Master.Tip"] = "Pushes every ticked device's volume forward or back together. It stops as soon as any one of them reaches 0 % or 100 %, so the balance you set is preserved. The fader returns to the centre when released; the levels stay on the devices.",
        ["Delay.Up.Tip"] = "Add 5 ms",
        ["Master.Label"] = "Master",
        ["Spatial.None"] = "no position",
        ["Spatial.Right"] = "right {0}m",
        ["Spatial.Left"] = "left {0}m",
        ["Spatial.Front"] = "front {0}m",
        ["Spatial.Back"] = "behind {0}m",
        ["Spatial.Up"] = "up {0}m",
        ["Spatial.Down"] = "down {0}m",
        ["Spatial.Tip"] = "Where this device sits relative to you, in metres. The three boxes are: right (negative is left), front (negative is behind), up (negative is below). Blank or 0 means no position, and the device is left untouched.",
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
        ["Latency.Estimated"] = "estimated compensation {0} ms",
        ["Latency.NoCompensation"] = "no compensation",
        ["Latency.Measured"] = "measured {0} ms",
        ["Latency.LowConfidence"] = "low confidence",
        ["Latency.Compensation"] = "compensation {0} ms",
        ["Latency.Trim"] = "trim {0} ms",
        ["Latency.Effective"] = "effective {0} ms",
        ["Latency.Stale"] = "⚠ measurement stale",

        ["SystemLatency.Ok"] = "System latency {0} ms",
        ["SystemLatency.Warning"] = "⚠ System latency {0} ms — beyond the ~125 ms lip-sync threshold, video will look out of sync",
        ["SystemLatency.Hint"] = "Aligning to the slowest device sets the system latency to its own; Bluetooth is usually 150-400 ms, so switch to WiredOnly for video.",

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
        ["Transport.NotVerified"] = "The audio path has not been verified by ear on real hardware.",

        ["Status.Idle"] = "Idle",
        ["Status.Stopped"] = "Stopped.",
        ["Status.Paused"] = "Paused multi-device output.",
        ["Status.Resumed"] = "Resumed output.",
        ["Status.SelectDevice"] = "Select at least one output device first.",
        ["Status.NoSource"] = "No render device available to mirror.",
        ["Status.NoDeviceOpened"] = "No device could be opened.",
        ["Status.SettingsSaved"] = "Settings saved.",
        ["Sink.Label"] = "Audio input",
        ["Sink.Tip"] = "A virtual cable or a real device. \"Auto\" follows the Windows default output.",
        ["Sink.Auto"] = "Auto (follow the Windows default output)",
        ["Sink.Applied"] = "Audio input set. Starting the mirror switches the Windows default output to it, and stopping puts it back.",
        ["Sink.Cleared"] = "Audio input set back to auto.",
        ["Sink.SwitchFailed"] = "Could not switch the Windows default output: {0}",
        ["Sink.GetCable"] = "Get a virtual cable ▾",
        ["Sink.GetCable.Tip"] = "Opens the vendor page for a few recommended virtual cables. MultiBT provides no downloads.",
        // recommended virtual audio devices (links out; MultiBT ships none of them)
        ["Recommend.VbCable"] = "VB-CABLE",
        ["Recommend.VbCable.Note"] = "Recommended. Free, one virtual cable, works on Windows as-is.",
        ["Recommend.VoiceMeeter"] = "VoiceMeeter",
        ["Recommend.VoiceMeeter.Note"] = "Free, several virtual cables plus a mixer; heavier than this app needs.",
        ["Recommend.VirtualAudioCable"] = "Virtual Audio Cable",
        ["Recommend.VirtualAudioCable.Note"] = "Lite is free but limited to 1 cable; the trial adds a spoken reminder every 30 minutes.",
        ["Recommend.MttDriver"] = "Virtual Audio Driver (open source)",
        ["Recommend.MttDriver.Note"] = "MIT licensed and free, but installing it requires Windows test signing.",
        ["Recommend.Repository"] = "Source repository (GitHub)",
        ["Notice.Copy"] = "Copy",
        ["Recommend.Note"] = "MultiBT only recommends these; it does not provide downloads. Install from the vendor.",
        ["Input.RealDeviceNote"] = "\"{0}\" is the input: it plays natively, its delay and volume are not controlled, and it gets no copy.",
        ["Input.Type.VirtualCable"] = "virtual cable",
        ["Input.RealDeviceNotDefaultNote"] = "⚠ Windows is not rendering into \"{0}\", so the mirror would be silent. Set it as the default output, or use \"Auto\".",
        ["Input.CableNotRoutedNote"] = "⚠ Windows is not rendering into \"{0}\" yet, so the mirror would be silent; starting it switches automatically.",
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
        ["Status.DeviceOpenFailed"] = "could not open",
        ["Status.RestartingForSource"] = "Switching to \"{0}\" and rebuilding the outputs...",
        ["Status.SourceCannotBeOutput"] = "\"{0}\" is the capture source and cannot also be an output (that would feed its audio back into itself).",
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

        ["Capture.NotDefault"] = "⚠ The input \"{0}\" is not the Windows default output, so system audio never reaches it and the other devices stay silent. Make it the default, or set the input to \"Auto\".",

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
