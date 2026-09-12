# MultiBT v0.1 技术规格

> **定位一句话**：Windows 的「多输出设备 + 自动同步」按钮。
> 不做音频工作站，不做 Voicemeeter，不做驱动。
>
> 状态：**待实现（设计已冻结）** · 目标框架 `.NET 10` / WPF · 底层 `NAudio 3.1.0`
> 本文档是实现的唯一权威依据。实现前请先读 `docs/PITFALLS.md`（已核实的坑）和 `docs/TASKS.md`（任务拆分）。

---

## 0. 范围

### 0.1 用户要解决的唯一问题

> 「我想让客厅音箱 + 桌面音箱 + 投影同时出声，而且我经常忘记 Voicemeeter 怎么配。」

所以产品的核心不是「能同时输出多个设备」——那件事已经有 Max-Paire、SoundSync、double-headphones 做了——
核心是 **一次配置、永久自动恢复、不用再想它**。

### 0.2 v0.1 目标

| # | 目标 | 验收方式 |
|---|---|---|
| G1 | 一条系统音频同时输出到 N 个设备（N≥2，含蓝牙） | 3 个设备同时出声，无爆音、无断续 |
| G2 | 每个设备独立音量 | 拖动一个滑杆不影响其他设备 |
| G3 | 每个设备独立延迟补偿，使多设备**听感同步** | A/B 对比无 flam（<20ms） |
| G4 | 播放 1 小时期间**持续保持同步**（时钟漂移补偿） | 60 分钟后偏差仍 <5ms |
| G5 | Profile：保存/加载场景，设备重连后自动恢复 | 断开再连蓝牙，自动回到出声状态 |
| G6 | 主窗口可不开，托盘可完成全部日常操作 | 托盘菜单切换场景 |

### 0.3 明确非目标（v0.1 不做）

- ❌ 蓝牙**配对**（pairing）。Windows 不支持桌面应用配对（`PairAsync` 官方标注不支持 desktop app）。
  v0.1 只管**已配对**设备。
- ❌ 蓝牙底层连接管理以外的东西（不做 driver、不做 filter）。
- ❌ 每应用音频路由（per-app routing）——那是 SoundDeck 的领域。v0.1 只做整机镜像。
- ❌ EQ / 空间音频 / 音效。
- ❌ 网络音频（AirPlay / Snapcast）。
- ❌ 多房间「音乐同步」（需要输入距离做空气传播补偿）——v0.1 只做**同房间**同步。

### 0.4 分层范围（务实结论，含反直觉的地方）

这个分层是经过调研后**主动收紧**的结果。特别注意：**漂移补偿必须进 v0.1，而声学自动测量可以推后**——
这与直觉相反，理由见 §6.6：不补偿漂移的话，应用在 5 分钟内正确、之后就永远错误。

**Tier 1 — 必须实现**

| 功能 | 为什么是 Tier 1 |
|---|---|
| 每设备延迟缓冲 | 这就是产品本身。没有它两个设备就是错开的，应用是玩具。 |
| 手动延迟微调（slider + 数字输入 + 单设备试听音） | 唯一零风险、当天可用、不需要麦克风/安静环境的机制。SoundSync 和 double-headphones 的**全部**同步机制就是它。也是声学测量失败时的回退。 |
| **时钟漂移补偿** | 不可协商。±50ppm = 每小时 180ms；±100ppm = 每小时 360ms。5 分钟就到 30ms，已经明显可闻。约 120 行代码（MIT 参考实现见 §6.4）。 |
| `measured / compensation / manual` 三段模型 + 持久化 + 失效标记 | 便宜，且让 profile、重新测量、UI 自洽。防止「静默过期测量值」这一整类 bug。 |
| API 推导的粗粒度延迟（`GetBufferSizeLimits` / `GetDevicePeriod` / `BufferSize` / `StreamLatency`） | 免费，无依赖。用于确定缓冲目标与 UI 合理区间。**绝不用作补偿值。** |
| 每设备音量 | 类别标配，缺失会成为第一个抱怨。 |
| 诊断（fill min/max/mean、实际补偿 ppm、starve/overflow 计数、resync 事件） | 没有它无法调试这两个问题，而 NAudio 默认**主动隐藏**两种失败模式（见 §7.2）。 |
| 诚实的延迟 UI（各传输方式预期区间、测量离散度、**系统总延迟**警告） | 对齐到 200–400ms 的蓝牙设备会让**全部**输出变晚，超过 ITU-R BT.1359-1 的 125ms 可察觉阈值。「已同步」和「能看视频」可能互斥，必须告知用户。 |

**Tier 2 — 便宜就做**

- 声学自动对齐向导（差异点，且是**唯一**能自动看到蓝牙链路延迟的方法）→ 见 §5.4，允许推后
- Profile（Tier 1 的持久化模型一旦存在就几乎免费）
- 有线设备的数字回环自检（对开发者价值高于用户价值）

**Tier 3 — 推迟到 v0.2+**

- 距离输入 / 几何补偿 / 多位置三角定位
- 蓝牙编解码器内省（**未找到**任何 Windows API 暴露协商后的 A2DP codec/latency）
- 后台定期自动重测（v0.1 只在**事件**上重测：重连、端点 id 变化、preset 变化 + 手动按钮）
- 视频模式策略（最小化绝对延迟 / 只对齐有线组 / 视频延迟协同）
- 分数样本延迟线（整数样本量化 ≤20.8µs，比有意义的最细对齐还小 40 倍）

---

## 1. 已核实的环境与技术栈

> 以下全部在本机核实过（`dotnet build` 通过），不是推测。

| 项 | 值 | 备注 |
|---|---|---|
| OS | Windows 11 24H2 (build 26100) | |
| SDK | .NET `10.0.400`；runtime `10.0.11` | 也有 8.0.423 |
| TFM | **`net10.0-windows10.0.19041.0`** | ⚠️ 见下方警告 |
| NAudio | **3.1.0**（最新稳定版） | 拆包：Core / Wasapi / WinMM / Midi / Asio / Dmo / WinForms |
| 托盘 | `H.NotifyIcon.Wpf` 2.4.1 | WPF `net8.0-windows+` 可用 |
| MVVM | `CommunityToolkit.Mvvm` | |

### 1.1 ⚠️ TFM 必须带 `-windows` 后缀

NAudio 3.x 的 nuspec 依赖组是 `net9.0` / `net9.0-windows7.0` / `net9.0-windows10.0.19041`。
若 TFM 写成 `net10.0`，NuGet 会选**可移植的 `net9.0` 组**——于是 **WASAPI 整个消失，而且没有任何报错或警告**
（这正是 NAudio #1407 的 bug，3.0.1 才修）。

- 经实测：`net10.0-windows10.0.19041.0` → 解析到 `net9.0-windows10.0.19041`，8 个包全部到位 ✅
- NAudio **不支持** `net8.0` / `netstandard2.0` / `net472`，会 `NU1202` 硬失败。
- 最低 `net9.0`。

### 1.2 ⚠️ 3.1.0 带有两个只在上游未发布版本修掉的 bug

这两个直接影响本项目，必须在设计上规避（**不要**用「升级 NAudio」来解决）：

1. **#1412** — `WdlResamplingSampleProvider` 在源供给不足时会丢样本并最终永久返回 0。
   这会打断「从 `BufferedWaveProvider` 喂数据的捕获链」这一**常见**模式——正是本项目的模式。
   → **规避：漂移补偿不使用 `WdlResamplingSampleProvider`，自己包装 `NAudio.Dsp.WdlResampler`**（见 §6.3）。
2. **#1442** — 当源的 `Read` 抛异常、或源在首个缓冲填满前就结束，`WasapiPlayer` 会被**卡在 `Playing` 状态**
   永远无法再播放（3.1.0 的清理逻辑写在 `try` 末尾而不是 `finally`）。
   → **规避：恢复路径必须 `new` 一个全新的 player，绝不复用可能已被卡住的实例**（见 §7.3）。

### 1.3 网络环境注意

`api.nuget.org` 在本机直连会 TLS 失败（本机有 Clash 在 `127.0.0.1:7890`，但 `ProxyEnable=0` 且无 `HTTP(S)_PROXY`）。
`dotnet restore` 需要能通 HTTPS。若 restore 报 `NU1301 ... The SSL connection could not be established`：
开启本地代理，或设置 `HTTPS_PROXY=http://127.0.0.1:7890`。

---

## 2. 架构

### 2.1 组件与数据流

```text
                    Windows 默认输出设备 (Render endpoint)
                                 │
                                 │  WASAPI Loopback  (WasapiRecorder)
                                 ▼
                      ┌───────────────────────┐
                      │   AudioSource          │  一次捕获，单写者
                      │   (capture thread)     │  DataAvailable: ReadOnlySpan<byte>
                      └───────────┬───────────┘
                                  │  同一份 PCM 字节，fan-out 到 N 条链
        ┌──────────────┬──────────┼──────────┬──────────────┐
        ▼              ▼          ▼          ▼              ▼
   ┌─────────┐   ┌─────────┐ ┌─────────┐ ┌─────────┐  ┌─────────┐
   │OutputCh0│   │OutputCh1│ │OutputCh2│ │   ...   │  │OutputChN│
   └────┬────┘   └────┬────┘ └────┬────┘ └─────────┘  └────┬────┘
        │             │           │                        │
        │  每条链内部（每条链由且仅由一个 WasapiPlayer 线程驱动）：
        │
        │   ring buffer ──► adaptive resampler ──► delay line ──► volume ──► meter
        │   (漂移缓冲)      (唯一被控制器动的量)   (同步补偿)     (增益)
        │       ▲                  ▲                  ▲
        │       │                  │                  │
        │   fill 观测          ratio 微调          compensationMs
        │       │                  │                  │
        │       └──────────┬───────┘                  │
        │                  │                          │
        ▼                  ▼                          ▼
   ┌──────────────────────────────────────────────────────────┐
   │                     SyncController                        │
   │  每 200ms (5Hz)：trough → EMA → P 控制 → clamp/rate-limit │
   │  常数见 §6.3；只控制「漂移缓冲」，不碰「补偿缓冲」        │
   └──────────────────────────────────────────────────────────┘
                              │
                              ▼
   ┌──────────────────────────────────────────────────────────┐
   │  DeviceManager          │  ProfileManager                  │
   │  端点枚举 / 身份 / 状态   │  profiles.json 读写 / 失效判定    │
   │  蓝牙连接 (KSPROPERTY)   │                                  │
   │  通知订阅 (事件驱动)      │                                  │
   └──────────────────────────────────────────────────────────┘
```

### 2.2 线程模型（硬性约束）

| 线程 | 归属 | 允许做什么 | 禁止做什么 |
|---|---|---|---|
| 捕获线程 | NAudio `WasapiRecorder` | 在 `DataAvailable` 里把字节 push 进 N 个 ring buffer | ❌ 阻塞、❌ 分配、❌ 加锁争用、❌ 碰 COM |
| N× 播放线程 | 每条链一个 `WasapiPlayer` | 从自己那条链 `Read` 并写入设备 | ❌ 被两个 player 共享同一个 provider 实例 |
| 控制线程 | 自己的 `Timer` 5Hz | 读 fill、算 ratio、`SetRates` | ❌ 每个 render callback 里重算 ratio |
| UI 线程 | WPF | 绑定、用户操作 | ❌ 任何音频栈调用 |
| 通知回调 | NAudio / COM | **只** post 到队列后立刻返回 | ❌ 在里面 dispose/重建 player（**会死锁**） |

**三条必须遵守的铁律：**

1. **fan-out = N 个独立 buffer，不是 N 个 reader 共用一个 buffer。**
   `BufferedWaveProvider` 底层是「单写者 + 单读者」安全的 `CircularBuffer`，**不支持多读者**。
   每条链必须有自己独立的 `BufferedWaveProvider`。
2. **`WasapiPlayer.Init` 非线程安全**，每个实例只能调用一次，且在 `Play` 之前；
   不得与 `Play`/`Stop`/`Dispose`/另一个 `Init` 并发。
3. **通知回调里绝不做音频栈操作。** NAudio 明确警告：`useSynchronizationContext: false` 时
   处理器「必须非阻塞，且不得回调进音频栈（例如 dispose 一个 player/recorder），否则有死锁风险」。

### 2.3 项目结构

```text
MultiBT/
├── MultiBT.sln
├── Directory.Build.props            # 统一 TFM / Nullable / LangVersion / x64
├── src/
│   ├── MultiBT.Core/                # 无 UI 依赖，可单元测试
│   │   ├── Audio/
│   │   │   ├── AudioSource.cs              # WasapiRecorder 封装 + 字节分发
│   │   │   ├── AudioEngine.cs              # 生命周期编排
│   │   │   ├── OutputChannel.cs            # 一条链的全部状态与所有权
│   │   │   ├── AdaptiveResampler.cs        # 包装 NAudio.Dsp.WdlResampler（可运行时调速）
│   │   │   ├── DelaySampleProvider.cs      # 整数样本补偿延迟线（独立于漂移缓冲）
│   │   │   ├── LevelMeter.cs
│   │   │   └── EngineTunables.cs           # 所有魔法常数集中于此（§6.3）
│   │   ├── Devices/
│   │   │   ├── DeviceManager.cs            # 枚举 + 身份 + 通知订阅
│   │   │   ├── AudioEndpointInfo.cs        # 端点 DTO（含 Transport 分类）
│   │   │   ├── TransportClassifier.cs      # BTHENUM/BTHHFENUM → Bluetooth；USB；HDMI
│   │   │   ├── BluetoothConnector.cs       # KSPROPERTY_ONESHOT_RECONNECT/DISCONNECT
│   │   │   └── RecoveryPolicy.cs           # 设备状态机（§7）
│   │   ├── Sync/
│   │   │   ├── SyncController.cs           # trough P 控制器
│   │   │   ├── ClockDriftEstimator.cs      # 诊断/上报用，不进控制回路
│   │   │   └── LatencyModel.cs             # measured/compensation/manual 的唯一计算入口
│   │   └── Config/
│   │       ├── ProfileStore.cs             # profiles.json 原子读写
│   │       └── Models.cs
│   └── MultiBT.App/                 # WPF
│       ├── App.xaml(.cs)
│       ├── Views/MainWindow.xaml
│       ├── Views/CalibrationWizard.xaml
│       ├── ViewModels/{MainViewModel,DeviceViewModel,ProfileViewModel}.cs
│       ├── Tray/TrayHost.cs                # H.NotifyIcon.Wpf
│       └── Services/SingleInstance.cs      # 单实例 + 第二实例唤起主窗口
└── tests/
    └── MultiBT.Core.Tests/          # LatencyModel / TransportClassifier / 控制器数学
```

---

## 3. NAudio / WASAPI 数据流（逐层）

> 所有类型名与签名都已通过编译核实（`tools/ApiProbe`，见 §10.1）。

### 3.1 捕获：`WasapiRecorder`（不是 `WasapiLoopbackCapture`）

NAudio 3.x 用 `WasapiRecorder` + `WasapiRecorderBuilder` 取代了旧的 `WasapiCapture` / `WasapiLoopbackCapture`。

```csharp
using var recorder = new WasapiRecorderBuilder()
    .WithLoopbackCapture()            // 捕获某个 render 端点在播的东西
    .WithDevice(renderDevice)         // 省略则用默认 render 设备
    .WithSharedMode()                 // 默认
    .WithEventSync()                  // 默认（低延迟捕获的前置条件）
    .WithBufferLength(50)             // ms
    .WithMmcssThreadPriority("Pro Audio")
    .Build();

WaveFormat captureFormat = recorder.WaveFormat;   // Build() 后可用

recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) =>
{
    // buffer 是 ReadOnlySpan<byte>，零拷贝，只在回调期间有效。
    // 必须立刻分发；不能存起来异步处理。
};
recorder.RecordingStopped += (_, e) => { /* e.Exception 可能非 null，见 §7 */ };
recorder.StartRecording();
```

**必须处理的四个捕获侧事实：**

1. **静音时 `DataAvailable` 不触发。** WASAPI loopback 只有在设备真的在播声音时才投递数据。
   → 每条链的 ring buffer 必须能**独立产出静音**；`BufferedWaveProvider.ReadFully = true` 是必须的，
   否则 `Read` 返回 0，而 `FillBuffer` 把 0 字节读**当作流结束**，会让该 player 永久停摆。
2. **别用 `WasapiLoopbackCapture` 的旧心智模型**去推断延迟。loopback 的投递时机与播放端点无关。
3. **`devicePosition` / `qpcPosition` 不能盲信。** NAudio 自己的文档指出：在部分共享模式驱动上
   这两个值「第一个包是真的，之后为 0」。→ 引擎必须**校验**（连续包 position 单调、非零增量），
   校验通过才用于诊断；**任何情况下都不进控制回路**（见 §6.1）。
4. **捕获格式 = 默认 render 设备的 mix format。** 不要强行改成「统一格式」——共享模式下让
   引擎自己做转换，我们只在每条链上处理**设备之间**的差异。

### 3.2 每条输出链（顺序不能改）

```csharp
// 1) 漂移缓冲：控制器唯一控制的缓冲
var ring = new BufferedWaveProvider(deviceMixFormat, TimeSpan.FromSeconds(2.0))
{
    ReadFully = true,                 // 必须：捕获间隙时零填充，且仍返回 count
    DiscardOnBufferOverflow = false,  // 必须 false：true 会静默丢弃「最新」样本
};

ISampleProvider samples = ring.ToSampleProvider();

// 2) 固定比率格式转换（仅当捕获格式 != 设备 mix format）。
//    注意：这是「固定」转换；控制器不能动它 —— 它无法在运行时改比率。
if (samples.WaveFormat.SampleRate != deviceMixFormat.SampleRate)
    samples = new WdlResamplingSampleProvider(samples, deviceMixFormat.SampleRate);

//    通道数对齐
if (samples.WaveFormat.Channels == 1 && deviceMixFormat.Channels == 2)
    samples = new MonoToStereoSampleProvider(samples);

// 3) 自适应重采样：唯一被控制器实时微调的量（±200ppm）
samples = new AdaptiveResampler(samples, deviceMixFormat.SampleRate);   // 自研，§6.3

// 4) 补偿延迟线：独立的第二个缓冲，只在补偿值变化时改（§6.5）
samples = new DelaySampleProvider(samples, deviceMixFormat.SampleRate, maxDelayMs: 2000);

// 5) 每设备音量
samples = new VolumeSampleProvider(samples) { Volume = gain };

// 6) 电平表（纯 UI）
samples = new MeteringSampleProvider(samples);

// 7) 交给设备
player.Init(new SampleToWaveProvider(samples));
```

**顺序的理由：** 重采样在延迟之前，于是延迟线运行在**设备 mix 采样率**上，
`delaySamples = round(ms/1000 * Fs_device)` 是精确的，且漂移控制器无法扰动它。

### 3.3 播放：`WasapiPlayer`（不是 `WasapiOut`）

```csharp
var player = new WasapiPlayerBuilder()
    .WithDevice(device)                              // MMDevice
    .WithSharedMode()                                // 默认
    .WithEventSync()                                 // 默认
    .WithLatency(latencyMs)                          // 默认 200ms，我们要显式控制
    .WithRawMode()                                   // 绕过 APO「音效增强」
    .WithCategory(AudioStreamCategory.Media)
    .WithMmcssThreadPriority("Pro Audio")            // 唯一支持的 MMCSS 入口
    .Build();

player.Init(chain);
player.Play();
```

**关键事实：**

| 事实 | 影响 |
|---|---|
| `WithRawMode()` 绕过 Windows「音频增强」APO | 避免 loudness equalization / 虚拟环绕把左右声道混掉。WASAPI 镜像场景**应该开**。需要 `IAudioClient2`，不支持时 `Init` 抛 `InvalidOperationException`。 |
| `WithMmcssThreadPriority("Pro Audio")` | 抗爆音。每个 player 在**自己的**线程上注册一次。NAudio 的 `NativeMethods` 是 `internal`，**无法**绕过 builder 自己调 `AvSetMmThreadCharacteristics`。 |
| `player.Volume` / `IsMuted` | **是 session 音量**（这个应用在音量合成器里的滑杆），不是端点音量。 |
| `player.DeviceVolume` | **端点全局音量**，影响所有应用。**禁止用于每设备音量**——用链上的 `VolumeSampleProvider`。 |
| `player.Init` | 非线程安全，一次性。 |
| `player.LowLatencyActive` / `LowLatencyUnavailableReason` | 低延迟是「尽力而为」，**会静默降级**。要检查它。 |

> **不要用 `WithLowLatency()` 做 v0.1 的默认。**
> 低延迟要求「无格式转换 + 事件同步 + 共享模式 + 格式等于设备 mix format」，且会显著提高唤醒频率；
> 对本项目真正重要的是**吞吐稳定性**而非低延迟（因为最终要对齐到 150–400ms 的蓝牙设备）。
> v0.1 统一用 `engineLatencyMs` preset（15/30/60/100），蓝牙设备默认 **100ms**。

---

## 4. 设备管理

### 4.1 枚举

```csharp
using var enumerator = new MMDeviceEnumerator();

// ⚠️ 必须用 DeviceState.All：已配对但未连接的蓝牙端点是 Unplugged，不是 Active
MMDeviceCollection render = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
```

- `MMDeviceCollection` 是**快照**，且索引器每次访问都**新建一个 `MMDevice` 包装**。
  → 设备变化后必须**重新枚举**；每个 `MMDevice` 都要 `Dispose()`。
- 别缓存 `MMDevice`。**每次激活都取新的**——休眠/唤醒后缓存的 COM 对象会失效，
  即使端点仍然枚举得到。

### 4.2 端点元数据与身份

| 需要 | 取法 |
|---|---|
| 显示名（"JBL Charge 5"） | `device.FriendlyName` 或 `PropertyKeys.PKEY_Device_FriendlyName` |
| 传输方式分类 | `device.InstanceId`（= `PKEY_Device_InstanceId`）字符串前缀 |
| 形态（音箱/耳机） | `PropertyKeys.PKEY_AudioEndpoint_FormFactor`（**NAudio 未定义对应 enum，需自己声明**） |
| 传给 `GetDevice()` 的 id | `device.ID` |

**传输方式分类规则：**

```
InstanceId 以 "BTHENUM\" 或 "BTHHFENUM\" 开头   → Bluetooth
InstanceId 含 "USB\"                              → USB
InstanceId 含 "HDAUDIO\" 且端点名为显示设备类      → HDMI/DP
其他                                              → Other
```

> ⚠️ `BTHENUM` 是**约定**而非有权威文档保证的常量。首次在目标机器上运行时
> **必须打日志核对真实值**，并让分类器在无法判定时安全回退到 `Other`（不崩、不误判延迟区间）。

**端点身份的稳定性（重要）：**

- `MMDevice.ID` 是 WASAPI endpoint ID 字符串。微软**明确声明其格式未定义（opaque）**，
  **不得解析**；生命周期绑定「设备安装」，**驱动升级/重装会变**，重启不变，USB 拔插不变。
- 因此 profile **不能只靠 `device.ID`** 认设备。三级回退策略：

```text
1. device.ID            （精确命中，最快）
2. InstanceId           （跨 ID 变化仍可用）
3. FriendlyName + 序号  （最后手段；Windows 重建端点时会换 ID，靠名字重新认领）
```

Windows 11 24H2+ 另有 `PKEY_AudioEndpoint_StableId`（NAudio 未定义，需自行声明 GUID/PID）
——**列为可选优化**，因为它本身也标为 UNCONFIRMED。

### 4.3 设备变化通知（NAudio 3.x 是**事件 API**）

> ⚠️ `IMMNotificationClient` 与 `RegisterEndpointNotificationCallback` 在 NAudio 3.x 已变为 **`internal`**。
> 不要再按 2.x 的写法实现。改用：

```csharp
using MMDeviceNotificationClient notifications = enumerator.CreateNotificationClient(useSynchronizationContext: false);

notifications.DeviceStateChanged    += (_, e) => Post(ChangeKind.State, e.DeviceId, e.NewState);
notifications.DeviceAdded           += (_, e) => Post(ChangeKind.Added, e.DeviceId);
notifications.DeviceRemoved         += (_, e) => Post(ChangeKind.Removed, e.DeviceId);
notifications.DefaultDeviceChanged  += (_, e) => Post(ChangeKind.Default, e.DeviceId);
notifications.PropertyValueChanged  += (_, e) => { /* 高频！只记内容，不做任何事 */ };
```

- `DeviceStateChanged` 的 `DEVICE_STATE_ACTIVE ⇄ DEVICE_STATE_UNPLUGGED` **正是**蓝牙连接/断开事件。
- `e.DeviceId` 也是 **opaque**：只用于比较或传给 `GetDevice()`，**不得解析**。
- `e.PropertyKey` 事件**每次音量变化都触发**，处理器必须极轻。
- `useSynchronizationContext: false` 时回调在音频/COM 线程上 → **只允许 post 到队列**。
  在回调里 dispose/重建 player **会死锁**（NAudio 明确记载）。

### 4.4 蓝牙连接管理（v0.1 **可以**做，无需管理员权限）

调研结论改变了原计划：原方案写「第一版不要碰蓝牙底层」，但**在不写驱动、不提权的前提下，
连接/断开已配对蓝牙音频设备是可行的**，且这正是产品差异点。

**推荐路径：Core Audio + `IKsControl` + `KSPROPSETID_BtAudio` oneshot**

```text
1. IMMDeviceEnumerator::EnumAudioEndpoints(eRender, DEVICE_STATEMASK_ALL, &coll)
   ← 必须含 DEVICE_STATE_UNPLUGGED，因为「已配对但未连接」就是这个状态
2. 对每个端点：Activate(IID_IDeviceTopology) → 遍历 connector →
   IConnector::GetConnectedTo → IPart::GetTopologyObject → IDeviceTopology::GetDeviceId
   → 得到 KS filter 的设备接口路径（\\?\bthenum… / \\?\bthhfenum…）
3. IMMDeviceEnumerator::GetDevice(filterPath) → Activate(IID_IKsControl)
4. KsProperty(Set = KSPROPSETID_BtAudio, Id = KSPROPERTY_ONESHOT_RECONNECT,
              Flags = KSPROPERTY_TYPE_GET)
   断开 = 同样的调用，Id 换成 KSPROPERTY_ONESHOT_DISCONNECT
```

**为什么这是「受支持的做法」而不是 hack：** WHQL 认证要求
`Device.Audio.Bluetooth.AtleastOneProfileSupport`——蓝牙音频设备**必须**暴露
`KSPROPSETID_BtAudio` 的 RECONNECT **和** DISCONNECT 两个 oneshot 属性，
以便「声音控制面板能够控制其连接与断开」。**无需提权、无需驱动、无需 MSIX。**

**必须处理的四点：**

1. **成功 ≠ 连上。** 文档明确：「成功只表示驱动**尝试**连接，不保证成功。」
   → 必须轮询端点状态到 `DEVICE_STATE_ACTIVE`（有超时上限），失败就重试（有上限）。
2. **必须按 container 分组。** 一个物理设备暴露多个端点（A2DP render、HFP render、HFP capture），
   Windows 只有在**全部**断开时才真正断开。按 `PKEY_Device_ContainerId` 分组，对同组每个 KS filter 都发一次。
3. **连接后要等 3–5 秒** Windows 才会把所有音频设备初始化完。
   → 恢复要走**延迟队列**，不是收到通知立刻开流。
4. **重连会让 `measuredLatencyMs` 失效。** 重配会强制重新协商 codec 并可能静默回落到 baseline SBC，
   而 SBC 与 AAC 在实测里差 ~60ms。→ 见 §5.3 的失效触发条件。**这样用户不会看到「突然不同步」。**

**明确砍掉的（做不了或不值得做）：**

| 方案 | 判决 |
|---|---|
| `BluetoothDevice.FromIdAsync` 连接 | ❌ 它只是构造对象，**没有** connect 动词。WinRT 蓝牙 API 根本没有连接/断开方法。 |
| `AudioPlaybackConnection` | ❌ **方向反了**。它让 PC 当蓝牙**音箱**接收手机音频。 |
| 裸 L2CAP / AVDTP PSM 25 | ❌ 会与系统自带 `BthA2dp.sys` 的 AVDTP 信令通道冲突。 |
| `GetRfcommServicesAsync` / `StreamSocket` 当作**主**机制 | ❌ A2DP-only 音箱（多数蓝牙音箱）根本没有 RFCOMM 服务。仅耳机有 HFP 时可用。**只作为可选补刀。** |
| SetupAPI `DIF_PROPERTYCHANGE` disable/enable | ❌ 需要提权且具侵入性。 |
| `PairAsync` 配对新设备 | ❌ 官方标注 desktop app **不支持**。v0.1 承诺范围仅限**已配对**设备。 |
| `System.Devices.Aep.DeviceCategory` | ❌ **该属性不存在**。正确的是 `System.Devices.Aep.Category`。 |

**（可选，v0.2）HFP 抑制。** Windows 会自由地把耳机在 A2DP↔HFP 之间切换，
这会破坏镜像稳定性。唯一可靠的抑制手段是**提权**禁用 HFP 端点。
→ v0.1 **接受抖动**，不做提权。**不要同时承诺「无需提权」和「完全稳定」。**

**硬件验证清单（冻结前必须在真机跑）：**
`KSPROPERTY_ONESHOT_RECONNECT` 是否对至少一个耳机和一个 A2DP-only 音箱生效；
`PKEY_Device_ContainerId` 在目标机器的蓝牙 render 端点是否被填充。

---

## 5. 延迟模型与测量

### 5.1 三字段模型（保留，但修正定义）

原设计 `measuredLatencyMs` / `compensationMs` / `manualOffsetMs` 的三分是**对的**——
它正确区分了「我们测到的事实」「我们决定的事实」「用户决定的事实」。保留，但必须做五处修正：

```jsonc
{
  "measuredDelayRelRefMs": 185.0,   // ⚠️ 相对参考设备，不是绝对值（见 5.1.1）
  "measurementSpreadMs": 4.2,       // P90 − P10，UI 用它说「±4ms」
  "measurementQuality": "high",     // high | low | failed（由 §5.4 门限决定）
  "measurementUtc": "2026-09-12T14:03:11Z",
  "measurementIsStale": false,      // 见 §5.3
  "compensationMs": 125.0,          // 自动算出的软件补偿
  "manualOffsetMs": 0.0             // 用户微调，可正可负
}
```

#### 5.1.1 ⚠️ `measuredLatencyMs` **作为绝对值是不可测的**

麦克风自身的通路（ADC + USB/驱动缓冲）给**每一次**测量都加了一个**未知常量**。
它在**差值**中抵消，在绝对值中不抵消。

→ 因此字段定义为 **相对于参考设备的延迟**（`measuredDelayRelRefMs`）。
→ **绝不允许** UI 把它的最大值当作「你的系统延迟 240ms」展示——那会错几十毫秒。

#### 5.1.2 有效补偿的唯一计算入口

```csharp
// LatencyModel.cs —— 引擎和 UI 都必须调用这一个函数，否则两者会不一致
public static int ComputeEffectiveDelaySamples(DeviceLatencySettings s, int deviceSampleRate, int maxDelayMs)
{
    double totalMs = s.CompensationMs + s.ManualOffsetMs;
    totalMs = Math.Clamp(totalMs, 0, maxDelayMs);
    return (int)Math.Round(totalMs / 1000.0 * deviceSampleRate);
}
```

- `manualOffsetMs` **有符号**，必须夹紧。符号语义要写死并显示在 UI：**正值 = 让这个设备更晚出声**。
- UI 显示**生效值**而不是裸的 offset——因为当 `compensationMs == 0` 时负 offset 没有移动空间。
- 额外暴露 `compensationAppliedSamples`（实际下发的整数样本数），便于对账。

### 5.2 空气传播距离：**多数情况下不是误差**

`d / c`，`c ≈ 343 m/s` → **2.915 ms/米**。

| 到麦克风的距离差 | 多出来的表观延迟 |
|---|---|
| 1 m | 2.9 ms |
| 2 m | 5.8 ms |
| 4 m | 11.7 ms |

**关键判断（与原设计的直觉相反）：**

- 若所有音箱在**同一房间**、麦克风放在**听音位**，空气项**就是正确答案的一部分**，
  不是误差。目标是「声波同时到达听众」，在听众位置测量并对齐测得的延迟恰好达到这个目标。
  **减掉空气项反而会让它们「电学同时」而「声学错开」。**
- 只有在**跨房间**（派对模式）时空气项才无意义，那才需要减 `d/343`，且需要用户输入距离。
  → v0.1 明确只支持**同房间**，保留空气项，并在 UI 提示「请把麦克风放在你坐的位置」。

### 5.3 失效（staleness）触发条件 —— 必须实现

> 最容易被忽略、也最容易导致「昨天还好今天怎么不同步了」的一类 bug。

`measurementIsStale = true` 当且仅当以下任一发生：

- 端点 `device.ID` 变化或端点被重新注册
- 设备断开/重连（**蓝牙重连尤其**：可能静默换 codec，实测 SBC vs AAC 差 ~60ms）
- mix format 或采样率变化
- 全局 `engineLatencyMs` 变化
- 该设备的 latency override 变化

失效后：**保留旧值可用，但在 UI 上明确标记「测量已过期，建议重新对齐」**，并且不自动应用。

### 5.4 声学自动对齐（Tier 2，可推后）

**这是唯一能自动「看到」蓝牙链路延迟的方法**（链路 + 远端 DAC 的延迟全在端点下游，
任何 Windows API 都看不到）。但它是**辅助**而非主机制——理由见 §5.4.4。

#### 5.4.1 测试信号：指数（对数）扫频

```text
类型       exponential / logarithmic sine sweep (Farina ESS)
f1         200 Hz
f2         8000 Hz          ← 不是 20Hz–20kHz：没有蓝牙音箱能重放两端，且低频被房间模态主导
时长 T     250 ms
采样率     48000 Hz (mono, float32)
电平       -12 dBFS RMS     ← 远离 AGC / limiter
淡入淡出   10 ms raised-cosine（两端）
前置静音   ≥ 200 ms
后置静音   ≥ 750 ms         ← 给质量检测一个干净的本底噪声区

s(t) = sin( 2π f1 T/ln(f2/f1) * ( exp((t/T) ln(f2/f1)) − 1 ) )
f(t) = f1 * (f2/f1)^(t/T)
逆滤波 s_inv(t) = reverse(s(t)) * (f1 / f(t))
反卷积 h = IFFT( FFT(x) · conj(FFT(s)) / (|FFT(s)|² + ε) )
```

| 信号 | 判决 |
|---|---|
| **指数扫频** | ✅ **采用。** 每倍频程等能量，匹配音箱/房间的实际频谱划分；且逆滤波后谐波失真产物被推到**负时间**，可被窗掉——对蓝牙音箱（最非线性、带 limiter）尤其重要。 |
| 线性 chirp | 🟡 可接受的简化。每 Hz 等时间 → 能量偏向 8–20kHz（蓝牙音箱正在滚降且指向性强）。 |
| MLS | ❌ 要求精确整周期（播放/录音时钟不同时很脆弱）；对时变和非线性极敏感，杂散峰会铺满所有延迟；峰均比高。 |
| 短 click / 脉冲 | ❌ 能量太低（真实房间 SNR 差）；没有音箱能重放 Dirac；可能触发保护 limiter。**但它非常适合「人耳拍手测试」，见 §5.4.4。** |

#### 5.4.2 检测算法：匹配滤波 + 前沿阈值（**不是** GCC-PHAT，也不是 argmax）

**不要用 GCC-PHAT。** PHAT 是为「源信号未知」准备的；我们有**已知的参考信号**，
此时匹配滤波在加性噪声下已是最优。PHAT 给所有频点单位权重，会在音箱/麦克风根本没有信号的频点
把纯噪声提升上来，**主动变差**。

**也不要用相关峰的 argmax。** 在混响房间里，相关峰会**偏向能量重心（偏晚）**，
因为直达声和早期反射都与参考相关。取**包络上第一次越过阈值的位置**才是**直达声到达**。

```text
1. x[n] = 录音缓冲（mono, 48kHz），直流阻断（~80Hz 一阶高通）
2. r[n] = IFFT( FFT(x) · conj(FFT(ref)) · W )       W = 200–8000Hz 矩形掩模
                                                     ref = 真正发给该设备的样本
3. e[n] = |r[n] + j·Hilbert(r[n])|                   包络
4. N    = median( e[n] ) 在触发电平之前的静音区
5. P    = max( e[n] )
6. PSR  = 20·log10(P / N)                            要求 ≥ 12 dB
7. θ    = min( max(4N, 0.15P), 0.5P )                随 SNR 自适应，绝不落在尾部
8. n0   = 最后一个 e[n] < θ 的位置；n1 = 第一个 e[n] ≥ θ 的位置
   确认：之后 0.5ms 内 e[n] 必须保持 ≥ 0.5θ        （杀掉孤立噪声尖峰）
9. 亚样本：对 log e[n0−1 .. n1+1] 做抛物线拟合，取顶点
10. τ = n_vertex / 48000
```

**预期精度：** 理论 CRLB 精度荒谬地好（~ns 级）因而无关紧要。真正的限制是房间声学、
音箱瞬态响应和 codec 分帧。文献里 GCC-PHAT 在恶劣条件（未知源、T60 0.2–1.0s、SNR 0–30dB）
的 MAE 是 6.84cm ≈ **0.20ms**；有已知参考信号 + 前沿检测应显著更好。
**但蓝牙的实际下限是 ±1–5ms**（SBC 一帧 128 样本 = 2.67ms，接收端至少抖动一帧）。

#### 5.4.3 重复测量、统计与垃圾剔除

```
剔除  : |x_k − median(x)| > 3 · 1.4826 · MAD(x)
估计  : median( 幸存者 )          ← 中位数，不是 P95
上报  : P90(x) − P10(x) 作为「离散度」
门限  : ≤10 次尝试中至少 3 次被接受，否则报告失败
```

**为什么是中位数而不是 P95：** 反射和 codec 抖动只会**增加**延迟，分布是**右偏**的，
P95 有意去采样混响偏置。

**门限（按顺序）：**

1. **一次只测一个设备，其余输出在端点级静音**，源端点也静音。——这一条消灭了「录到了错的设备」
   这一整类失败，是性价比最高的鲁棒性措施。
2. `PSR ≥ 12 dB` —— 抓「什么都没播」和「只有房间噪声」。
3. **峰值优势 ≥ 4×（12dB）** 相对 ±5ms 之外的第二大局部极大 —— 抓「锁到了反射簇或另一个声源」。
4. **按传输方式的合理性窗口：** 有线/USB `0–80ms`，HDMI `0–120ms`，蓝牙 `80–450ms`。
   超出即拒绝。**特别注意：本该晚的设备测出 0ms，几乎一定意味着听到了别的东西。**
5. 电平门限：峰值 ≥ −50 dBFS，且 ADC 未削波（削波会畸变包络、让前沿偏移）。
6. 离散度门限：`P90 − P10 > 8ms` → 标记低置信度，**拒绝自动应用**。
7. 失败就重测，**绝不静默回退到 0**。「测量失败」是合法结果。

**两个容易忘记的要求：**

- **测量路径必须等于播放路径。** 要在**真实管线运行中**测量：把扫频注入第 *i* 个设备的
  正常链路，其他设备静音，且漂移控制器**已经锁定并预热 ≥3s**。
  用一个单独的校准 `WasapiOut` 会有不同的缓冲占用和拉取粒度，
  **仅这一项就能引入最多 ~25ms 的误差**（对 60ms 粒度的无线端点）。
  → 这是架构要求：扫频必须能注入每条链的正常管线。
- **HDMI/AVR 会重新锁定。** 接收机锁定新采样率可能要 1–3s。任何启动或格式变化后的第一次
  重复必须丢弃。

#### 5.4.4 为什么声学测量是**辅助**而不是主要机制

1. 它需要麦克风、安静时刻，并要中断播放。
2. 在蓝牙上它的精度下限由 codec 分帧抖动（±1–5ms）**和**校准路径 vs 播放路径的粒度不匹配
   （最多 ~25ms）决定 —— **比用户自己听出 40ms 的 flam 然后十秒钟拧掉它要差。**
3. 用户只能感知**差值**，而差值他闭眼都能听出来对着调。

→ **手动微调优先，声学自动对齐第二。** UI 上「拍手/试听 + ±1/±5ms 微调」必须是主交互。

**蓝牙延迟现实数值（用于 UI 合理性校验）：**

| 传输 / codec | 实测端到端延迟 |
|---|---|
| aptX Low Latency | ~40 ms（规范值，少见） |
| aptX Adaptive | ~80 ms |
| SBC（4 台安卓旗舰均值） | **308 ms**（方差 41.9） |
| aptX（均值） | 316 ms |
| LDAC（均值） | 324 ms |
| AAC（均值） | 369 ms（方差 45，部分机型可达 90） |
| **Windows 蓝牙（某已发布 Windows 应用自述）** | **150–250 ms** |
| 有线 / USB / HDMI | 5–40 ms（AVR/电视差得多） |

**UI 含义：** 蓝牙 `measuredDelay` 是 150–400ms 且每次重测抖动 ±几十 ms，
**保留 1ms 滑杆步进，但置信度指示必须诚实**（蓝牙标 ±5–25ms）。
在一个自身抖动 20ms 的设备上给 1ms 滑杆而不显示离散度，是 UX 撒谎。

---

## 6. 同步与漂移补偿

### 6.1 为什么必须做（定量）

`Δt = ppm × 1e-6 × T_seconds`：

| 时钟误差 | 5 分钟 | 30 分钟 | 60 分钟 |
|---|---|---|---|
| 20 ppm | 6 ms | 36 ms | 72 ms |
| **50 ppm**（BT Core Spec active clock 上限） | **15 ms** | **90 ms** | **180 ms** |
| **100 ppm** | **30 ms** | **180 ms** | **360 ms** |
| 200 ppm | 60 ms | 360 ms | 720 ms |

**两个**设备看的是**误差之差**：各自 ±50ppm 时，一对设备最大可漂移到 100ppm → **一小时 360ms**。

**可闻性（两个相关声源）：**

| 偏差 | 听感 |
|---|---|
| <1 ms | 不可闻 |
| 1–10 ms | **梳状滤波**：单声道/相关素材音色变空、发飘 |
| 10–20 ms | 临界；打击乐起音有「flam」粗糙感 |
| 20–40 ms | **明显可闻**：鼓/拨弦的起音被加倍，发「echoey」 |
| >40 ms | 感知为独立的后到声：「那个音箱在后面」 |
| >125 ms | 唇音同步失败（ITU-R BT.1359-1） |

**结论：** 50–100ppm 下，两个蓝牙音箱在 **5–10 分钟**就跨过 20–40ms 的「明显可闻」线。
**漂移补偿比延迟测量更重要**，因为测量只修正**初始**偏差，而漂移会**持续**破坏它。
「手动微调但不补偿漂移」在 5 分钟内正确，之后永远错误。

### 6.2 ⚠️ 最大的设计陷阱：两个缓冲不能合一

> **原设计里最危险的一点。** 如果把补偿延迟实现成「ring buffer 的读指针偏移」，
> 那么 `fill` 和 `delay` 就是**同一个变量**——控制器会忠实地把它稳定在 5ms，
> 而你那 170ms 的补偿**悄悄消失了**。

```text
  捕获 ──► ┌──────────────────┐   ┌───────────────────────┐   ┌──────────┐   ┌────────┐
           │ per-output ring  │──►│ adaptive resampler    │──►│ delay    │──►│ volume │──► WASAPI
           │ (漂移缓冲)        │   │ ratio = f*_dev *      │   │ line     │   │        │
           │ 控制器唯一控制它  │   │        (1+correction) │   │(补偿+微调)│   │        │
           └──────────────────┘   └───────────────────────┘   └──────────┘   └────────┘
```

**四条规则：**

1. **两个独立缓冲。** ring 是**漂移**缓冲（被控量）；delay line 是**补偿**缓冲（只在补偿值变化时改）。
2. **先重采样，后延迟。** 延迟线因此运行在设备 mix 速率上，算术精确，且控制器无法扰动它。
3. **永不无界增长。** `fill` 天然有界（`maxBacklog = latency + 25ms` → 显式 `ClearBuffer` 重同步；容量 2s）；
   `delaySamples` 由补偿范围界定。两者都不是积分量。
4. **`f*`（trough 目标）对所有设备必须相同。** 由 `c_i(t) = R_i(t) − b_i(t)`，
   对齐要求 `f_i + b_i` 对所有设备为常数。`b_i` 是设备相关但恒定的，会被**测得的补偿**吸收；
   但如果每条链用**不同**的 `f*_i`，就等于注入了等于该差值的静态错位。
   → **一个全局 `engineLatencyMs`**，允许每设备覆盖但 UI 标注「需要重新测量」。

### 6.3 控制律：对 trough 做 P 控制（**不要加 I**）

**被控对象是积分器**：`d(fill)/dt = c − d`，`d` 是未知漂移。取 `c = K·e` 得
`dE/dt = K·E − d` → `E(t) → d/K`（一阶，时间常数 `1/K`）。

→ **对积分对象做 P 控制本身已经具有零稳态速率误差**（这是「P-on-integrator 等价于对速率积分」的经典结果）。
→ **加显式 I 项会让回路变成双积分器，这正是制造极限环的标准配方。不要加 I。**

```csharp
// 每条链，每 Tc = 200ms (5 Hz)：
double f    = (writerFrames − readerFrames) / (double)Fs;      // ring fill，秒
double m    = Math.Min(m, f);                                   // 窗口内最小值 = TROUGH
mHat       += Alpha * (m − mHat);                               // EMA，Alpha = 0.3 → ~1s
double e    = mHat − fStar;                                     // fStar = (latencyMs + 5ms)/1000
double c    = Math.Clamp(K * e, −CMax, +CMax);                  // K = 0.5 /s, CMax = 200e-6
c           = Math.Clamp(c, cPrev − DCmax, cPrev + DCmax);      // 速率限幅 50e-6 / tick
resampler.SetRates(nominalInRate * (1 + c), deviceRate);
m           = double.PositiveInfinity;                          // 重置窗口最小值
```

**为什么是 trough（窗口最小值）而不是均值：** 无线端点的拉取粒度可以粗到 **~60ms**
（AudioHQ 在 PlayStation Link 源上实测：backlog 从 ~10ms 锯齿到 ~70ms）。
针对均值控制会把谷底拖到一次 render 拉取之下 → 饿死缓冲 → 可闻爆音。
**针对谷底控制，无论突发多大，低点都安全。**

**常数表（集中在 `EngineTunables.cs`）：**

| 常数 | 值 | 依据 |
|---|---|---|
| `BufferSeconds` | 2.0 s | ring 容量 |
| `TargetBacklogMarginMs` | **+5 ms** | `f* = engineLatencyMs + 5ms`（AudioHQ 0.2.4 验证） |
| `ResyncMarginMs` | **+25 ms** | `maxBacklog = engineLatencyMs + 25ms` → 记日志 + `ClearBuffer()` |
| `MaxCorrection` | **±200 ppm** | v0.1 上限（见下方 FM 论证） |
| `MaxCorrectionHardCeiling` | ±500 ppm | 硬天花板（AudioHQ 用 −0.5% = 8.6 cents） |
| `K`（P 增益） | **0.5 /s** | 稳态偏差 `E_ss = d/K`；100ppm → 0.2ms |
| `Alpha`（EMA） | **0.3** | 每 tick，5Hz → ~1s 建立时间 |
| `ControlTickHz` | **5 Hz** | 固定频率，**绝不**在 render callback 里重算 |
| `MaxCorrectionRate` | **50 ppm / tick** | = 250 ppm/s |
| `WarmUpSeconds` | 3 s | 之前不信任 fill，不做补偿决策 |
| `LatencyPresets` | 15 / 30 / 60 / 100 ms | 蓝牙默认 **100ms** |

**数值验证：** `K = 0.5 /s`，`d = 100 ppm = 1e-4`：
```
稳态 fill 偏差 E_ss = d/K = 1e-4 / 0.5 = 2e-4 s = 0.2 ms
闭环极点 1/K = 2 s
调节权限 0.5% × 48000 = 240 样本/s = 5 ms/s
```
**0.2ms 的稳态错位比两个相关声源的可闻阈值低 15 倍，而且是恒定的、不会累积。**
这是本问题最重要的结论：**用 P 控制器可以把 100ppm 的设备永久稳定在 0.2ms 误差，且永远不需要知道漂移率。**

**离散稳定性（纯延迟）：** 从施加 ratio 到在 fill 中看到它之间存在真实传输延迟
（重采样滤波群延迟 + 一个引擎周期 + 拉取粒度），记 `L ≈ 30–100ms`，突发无线端点可达 ~60ms。
P-on-integrator 的纯延迟界是 `K·L < π/2`：
```
L = 100 ms → K < 15.7 /s
选 K = 0.5 /s → 31 倍裕量
```
所以 `K = 0.5` 非常保守。若刚重连的设备收敛太慢，可以安全地提到 1.0–2.0 /s；
**超过 ~5 /s 之前必须先实测 `L`。**

**ppm 的可闻性（为什么 ±200ppm 是安全的）：**
```
cents = 1200 · log2(1 + c)
  50 ppm → 0.087 cents
 100 ppm → 0.17  cents
 200 ppm → 0.35  cents
 500 ppm → 0.87  cents
5000 ppm (0.5%) → 8.63 cents
```
纯音的音高 JND 是 ~5–10 cents，所以即使 ±0.5%（8.6 cents）作为**静态**失谐也在门槛下。

**但静态不是全部：** **移动**的修正就是调频（FM），而 FM 的可检测阈值远低于静态音高 JND。
→ 三重防护：**上限 ±200ppm** + **速率限幅 ≤50ppm/tick** + **EMA 平滑**。
残差抖动在 1–10Hz（FM 检测最灵敏的频段）应基本没有频谱内容。

### 6.4 执行机构：自己包装 `WdlResampler`

> **两个现成的重采样器都**不能**在运行时改比率。** `WdlResamplingSampleProvider` 的内部
> `WdlResampler` 是 `private readonly`；`MediaFoundationResampler` 的格式在 `SetOutputType` 时固定。
> 重建它们会重置 `m_fracpos` 和滤波状态 → **不连续/爆音**。

`NAudio.Dsp.WdlResampler` 是**公开**的，且它的 `SetRates` **可以安全地在流中途调用**：
源码里它只在值变化时重算 `m_ratio`，**不碰** `m_fracpos`、输入缓冲和 IIR 滤波历史
→ 相位保持，比率变化本身不引入样本不连续。

```csharp
var rs = new WdlResampler();
rs.SetMode(interp: true, filtercnt: 2, sinc: false);   // 线性插值 + 2 级 IIR
rs.SetFilterParms();
rs.SetFeedMode(false);                                  // output-driven
rs.SetRates(nominalInRate, deviceRate);
// ... 每 200ms：
rs.SetRates(nominalInRate * (1.0 + correction), deviceRate);
```

**三个实现要点：**

1. **`ResampleOut` 可能返回少于请求的帧数。** `ResamplePrepare`/`ResampleOut` 必须**循环**
   直到输出缓冲填满，且**绝不能从活管线返回 0**——`FillBuffer` 把 0 字节读当作流结束，
   player 会永久停摆（这正是 NAudio 3.1.0 的 #1412 类 bug）。最后兜底可以零填充。
2. **保持 interp 模式。** `sinc` 模式的 `BuildLowPass` 在 `filtpos` 变化时会 `Array.Resize`
   并重建 2048 抽头表——在 5Hz 控制回路里就是每 200ms 在音频线程上重建一次。另一个必须保持 interp 的理由。
3. **固定**的格式转换用别的方式做（`WdlResamplingSampleProvider` 或让引擎自己转），
   控制用的这个只在 ratio ≈ 1 附近微调。

### 6.5 延迟变化的处理策略

| 变化幅度 | 策略 |
|---|---|
| 小（≤20 ms）——控制器微调或滑杆慢拖 | **滑动**分数读指针，速率限幅使隐含音高偏移 <0.5%（48kHz 下 `v ≤ 240 样本/s`，20ms 需 4s）。不可闻。 |
| **大（>20 ms）**——新测量加了 170ms、蓝牙断开导致补偿塌陷 | **绝不滑动。** 170ms 在 0.5% 限制下要滑 **34 秒**的明显失谐音频。→ **静音 → 重新应用 → 10ms 淡入**，UI 显示「正在重新同步…」。短暂静默优于半分钟的滑音。 |
| 播放开始前 | 预填 `D_i` 个静音样本。无需淡入。 |

### 6.6 漂移检测（诊断用，**不进控制回路**）

有三种可观测量，**推荐用 fill 趋势做控制**（§6.3），时钟斜率只做诊断：

| 方法 | 它到底告诉你什么 |
|---|---|
| 捕获侧 `devicePosition`/`qpcPosition` | 只有**捕获**端点的时钟速率。**对你的输出设备一无所知。** 且 NAudio 默认**不暴露**（见 §10.2）。 |
| 渲染侧 `IWavePosition.GetPosition()` | ⚠️ **不能用！** 它返回 `AdjustedPosition`，用**标称**频率外推 → **假设零漂移**，会**掩盖**你要测的量。对 200ppm 的设备会读出 ~0ppm。 |
| 渲染侧裸 `AudioClockClient.GetPosition(out pos, out qpos)` | ✅ **正确。** 丢弃 `S_FALSE` 读；注意连续读可能**相等**（驱动每个引擎周期才更新一次）。 |

**如果要做 ppm 上报：**

```
b0    = Frequency / 1e7                                   [position units per 100ns tick]
b_hat = Σ((t_k − t̄)(p_k − p̄)) / Σ((t_k − t̄)²)           最小二乘斜率
d_hat = b_hat / b0 − 1                                   [×1e6 → ppm]
σ_b   = σ_p · √12 / (T_ticks · √N)                       （均匀采样）
```
`Frequency = 48000`、`σ_p = 0.29`（1 单位计数器均匀量化）时：
`30–60s` 滑动窗口理论上可分辨 **~0.1–0.5 ppm**。
但**从不需要 1ppm**，因为 P 控制器自动吸收任何漂移。速率估计只是 UI/诊断的锦上添花。

**归因诊断（(ii) 的唯一真实用途）：** 结合两者可区分「设备快」和「写者慢」：
`fill` 下降 **且** `d_render − d_capture > 0` → 设备真的快；
`fill` 下降 **且** `d_render − d_capture ≈ 0` → **你的写者慢了**（捕获饿死、GC 暂停、调度），
不是时钟问题。这个归因是值得实现时钟斜率的唯一理由。

### 6.7 ⚠️ 只能对齐到最慢的设备——必须告知用户

对齐到最慢设备会把**整个系统**的端到端延迟设为 `max(L_i)`。
有蓝牙音箱在 200–400ms 时，**每一个**输出都会变晚，**超过 ITU-R BT.1359-1 的 125ms 可察觉阈值**。
→ 在一台同时有蓝牙音箱和 DAC 的机器上，**「已同步」和「能看视频」是互斥的**。
UI 必须**显示系统总延迟**并提供选择：

```text
○ 音乐模式：全部对齐（系统延迟 ≈ 310 ms）
● 视频模式：只对齐有线组，蓝牙单独延后（系统延迟 ≈ 40 ms）
```

**不要把这个trade-off藏起来。**

---

## 7. 异常与恢复

### 7.1 每设备状态机

```text
        ┌──────────┐  用户启用   ┌───────────┐  设备 ACTIVE   ┌──────────┐
        │ Disabled │───────────►│  Waiting  │───────────────►│ Starting │
        └──────────┘            │ (设备缺失) │                └────┬─────┘
             ▲                  └───────────┘                     │ Init+Play
             │ 用户禁用               ▲                           ▼
             │                        │ 重连/插入            ┌──────────┐
             │                        │                      │ Running  │
             │                  ┌─────┴──────┐  出错/拔出     └────┬─────┘
             └──────────────────│ Recovering │◄──────────────────┘
                                └─────┬──────┘
                                      │ 指数退避，最多 3 次
                                      ▼
                                ┌──────────┐
                                │  Failed  │  ← 只有用户操作能离开此状态
                                └──────────┘
```

**关键行为：**

- 设备 `UNPLUGGED` → 拆掉该链的 client，但**把它留在期望集合里**，下次 `ACTIVE` 自动重新武装。
- **恢复必须 `new` 一个全新的 `WasapiPlayer`**，绝不复用（NAudio 3.1.0 #1442 卡死 bug，§1.2）。
- 重连后**等 3–5 秒**再开流（Windows 需要时间初始化所有音频设备）。
- 退避重试**最多 3 次**。**绝不做无界看门狗。**
- **一个设备坏掉不能拖垮整个会话**：捕获回调里对每条链用独立 `try/catch`。

### 7.2 ⚠️ NAudio 默认隐藏的两种失败模式

| 隐藏的失败 | 机制 | 后果 |
|---|---|---|
| **饿死不可见** | `ReadFully = true`（默认）时，`Read` 会**零填充并仍返回 `count`** | **饿死静音与真实静音无法区分。** 必须自己在每次 read **前后**采样 `BufferedDuration` 并计数。 |
| **溢出静默丢内容** | `DiscardOnBufferOverflow = true` 时，`AddSamples` 写入能放下的部分并**静默丢弃最新的样本** | 无异常、无计数器。**本项目保持 false**，改为在有界 backlog 上显式 `ClearBuffer` 重同步。 |

→ **诊断是 Tier 1 而非可选项。** 必须记录：每设备 fill 的 min/max/mean、
实际施加的 correction（ppm）、starved-read 与 overflow 计数、resync 事件。

### 7.3 异常映射

| 异常 / 情况 | 处理 |
|---|---|
| `AUDCLNT_E_DEVICE_INVALIDATED` | 该链进入 `Recovering`；重新 `GetDevice(id)` 并重建 player |
| `PlaybackStopped` 带非 null `Exception` | 同上。**3.1.0 上必须 new 新 player。** |
| `Init` 抛 `InvalidOperationException`（raw mode 不支持） | 去掉 `.WithRawMode()` 重试一次；再失败进 `Failed`（不要无限重试） |
| 捕获停止（`RecordingStopped`） | 整个引擎重同步：重置所有链的 fill 基线，重新预热 3s |
| 蓝牙重连 | 标记该设备 `measurementIsStale = true`（§5.3） |

### 7.4 关闭顺序（防死锁）

```text
1. 停止捕获（recorder.StopRecording()）——先断绝输入
2. 取消通知订阅（dispose notification client）
3. 停止同步控制器线程
4. 逐条链 await player.DisposeAsync()      ← 用 DisposeAsync，别阻塞 UI 线程
5. dispose 所有 ring / provider
6. 退出前若正在播放：先把各链淡出（10ms），避免末端爆音
```

---

## 8. 配置 JSON

`%APPDATA%\MultiBT\profiles.json`（原子写：临时文件 + `File.Replace`）

```jsonc
{
  "schemaVersion": 1,
  "activeProfileId": "movie",
  "engine": {
    "engineLatencyMs": 100,          // 全局 f* 基础；所有设备必须相同（§6.2 规则 4）
    "sourceDeviceId": null,          // null = 跟随 Windows 默认 render 设备
    "startWithWindows": true,
    "autoResumeLastProfile": true
  },
  "devices": [                       // 设备全局档案（跨 profile 复用）
    {
      "key": "jbl-charge5",
      "identities": {
        "endpointId": "{0.0.0.00000000}.{a1b2...}",   // 首选；opaque，只比较不解析
        "instanceId": "BTHENUM\\{0000110b-...}\\7&2f9a1c3&0&001122334455_C00000000",
        "friendlyName": "JBL Charge 5"                // 最后回退：Windows 重建端点后靠名字认领
      },
      "displayName": "客厅 JBL",
      "transport": "Bluetooth",       // Bluetooth | Usb | Hdmi | Other（由 InstanceId 前缀分类）
      "latency": {
        "measuredDelayRelRefMs": 185.0,   // ⚠️ 相对参考设备，不是绝对值（§5.1.1）
        "measurementSpreadMs": 4.2,       // P90 − P10
        "measurementQuality": "high",     // high | low | failed
        "measurementUtc": "2026-09-12T14:03:11Z",
        "measurementIsStale": false,
        "compensationMs": 125.0,
        "manualOffsetMs": 0.0
      },
      "audio": { "gain": 0.8, "muted": false, "maxDelayMs": 2000 }
    }
  ],
  "profiles": [
    {
      "id": "movie",
      "name": "电影",
      "icon": "🎬",
      "mode": "AlignAll",             // AlignAll（音乐）| WiredOnly（视频），见 §6.7
      "outputs": [
        { "deviceKey": "jbl-charge5",  "enabled": true,  "gain": 0.80, "manualOffsetMs":   0 },
        { "deviceKey": "desk-sony",    "enabled": true,  "gain": 0.70, "manualOffsetMs":  -5 },
        { "deviceKey": "projector",    "enabled": true,  "gain": 0.60, "manualOffsetMs":   0 },
        { "deviceKey": "bt-headphone", "enabled": false, "gain": 0.80, "manualOffsetMs":   0 }
      ]
    },
    { "id": "music", "name": "音乐", "icon": "🎵", "mode": "AlignAll", "outputs": [] },
    { "id": "work",  "name": "工作", "icon": "💻", "mode": "WiredOnly", "outputs": [] }
  ],
  "ui": { "startMinimizedToTray": true, "showLevelMeters": true }
}
```

**`mode` 的语义（§6.7）：**
- `AlignAll` — 全部对齐到最慢设备。系统延迟 = `max(L_i)`。适合音乐。
- `WiredOnly` — 只对有线组内部对齐，蓝牙设备**不加补偿**（保持其天然延迟）。系统延迟 ≈ 有线组最大值。适合看视频。

**原子写与容错：** 解析失败时**不要崩溃也不要静默重置**——重命名为 `profiles.corrupt-<ts>.json`，
以默认配置启动，并在 UI 明确提示。

---

## 9. UI

### 9.1 主窗口

```text
┌──────────────────────────────────────────────┐
│ 🔊 MultiBT                            — □ ×  │
├──────────────────────────────────────────────┤
│  当前场景：🎬 电影          ⚠ 系统延迟 310ms │
│                             [切换到视频模式]  │
├──────────────────────────────────────────────┤
│  ☑ 客厅 JBL        ████████░░ 80%            │
│     延迟 185ms · 补偿 +125ms · ±4ms   [微调] │
│  ☑ 桌面 Sony       ███████░░░ 70%            │
│     延迟 240ms · 补偿  +70ms · ±3ms   [微调] │
│  ☑ 投影            ██████░░░░ 60%            │
│     延迟 310ms · 补偿   +0ms · ⚠测量过期     │
│  ☐ 蓝牙耳机        ████████░░ 80%            │
│     —— 未连接（已配对）        [连接]        │
├──────────────────────────────────────────────┤
│  🎬电影   🎵音乐   💻工作            [⚙ 设置] │
├──────────────────────────────────────────────┤
│  [🔍 重新检测延迟]  [⚡ 自动同步]  ▶ 开始同步 │
│  同步状态：● 已锁定 · 漂移修正 +42 ppm       │
└──────────────────────────────────────────────┘
```

**每设备行必须显示三件事**（这是诚实性的核心）：
1. `measuredDelay`（测量值，**不可编辑**）+ 离散度 `±N ms`
2. `compensationMs`（软件控制的，**不可编辑**）
3. 「微调」按钮 → 打开有符号 `manualOffsetMs` 调整

### 9.2 托盘（日常入口）

```text
🔊 MultiBT · 电影
────────────
☑ 🎬 电影
☐ 🎵 音乐
☐ 💻 工作
────────────
☑ 客厅 JBL
☑ 桌面 Sony
☐ 投影
────────────
暂停多设备输出
打开主窗口
退出
```

- 托盘必须能完成**全部**日常操作（G6）。
- 单实例：第二次启动唤起已有实例的主窗口（`SingleInstance.cs`）。
- 退出时**先淡出再停流**（§7.4 步骤 6）。

### 9.3 校准向导（Tier 2）

```text
步骤 1/4  准备
  · 把麦克风放在你坐的位置
  · 保持环境安静（空调/风扇可接受）
  · 播放会中断约 20 秒
             [开始] [跳过，手动微调]

步骤 2/4  逐设备测量
  客厅 JBL    ████████░░  完成  185ms ±4ms  ✅
  桌面 Sony   ██████░░░░  测量中…
  投影        待测量

步骤 3/4  结果（可选择 [接受] / [重测] / [手动微调]）
  ⚠ 若有设备未通过质量门限，则拒绝自动应用任何值

步骤 4/4  试听确认
  [在全部设备上播放测试音]   ±1ms ±5ms 微调
```

**硬性要求：** 任何设备未通过 §5.4.3 的门限 → **拒绝自动应用**，只显示「测量失败，请手动微调」。

---

## 10. 验证与诊断

### 10.1 编译期契约检查（已存在）

`tools/ApiProbe/` 是一个**只做编译检查**的项目：它把本文档要求的所有 NAudio / Core Audio API
都写进一个文件，`dotnet build` 就是测试。未来 NAudio 改名或删成员时，它会**编译失败**而不是在流中途运行时失败。

```powershell
dotnet build tools/ApiProbe/ApiProbe.csproj
```

**当前状态：已在 `NAudio 3.1.0` + `net10.0-windows10.0.19041.0` 上验证通过（0 警告 0 错误），
并做过负向对照（故意引用不存在的成员确认构建会失败）。**

### 10.2 需要在真机上验证的 UNCONFIRMED 项

实现前/中必须实测，**不要按假设写死**：

1. `devicePosition`/`qpcPosition` 在目标机器的 loopback 上是否真的非零且单调（NAudio 文档说部分驱动不可靠）。
2. `KSPROPERTY_ONESHOT_RECONNECT` 对至少一个耳机 + 一个 A2DP-only 音箱是否生效。
3. `PKEY_Device_ContainerId` 在目标机器的蓝牙 render 端点上是否被填充。
4. `BTHENUM\` 前缀的真实形态（打日志核对，分类器要能安全回退）。
5. `System.Devices.Aep.DeviceAddress` 的字符串格式（12 位 hex 还是带冒号）。
6. `IAudioClock` 在蓝牙 A2DP 端点上的位置是否反映**远端 DAC** 的时钟，还是 Windows 合成的。
7. 实测各设备的 `GetBufferSizeLimits` / `GetDevicePeriod`，据此校准 `engineLatencyMs`（蓝牙预计需要 100ms）。
8. ±0.05% 的流中途 `SetRates` 听感是否真的无爆音（**Codex：源码显示无不连续机制，但 NAudio 没有任何文档保证**）。
    → **必须在实现里对比率做斜坡过渡（~100ms 内 0.05%），并用持续钢琴/人声音调验耳。**

### 10.3 运行期诊断面板（调试用）

每设备一行：`fill min/max/mean`、`correction ppm`、`starved reads`、`overflows`、`resync 次数`、
`AverageLatency`、`CurrentLatency`、`BufferSize`、`GetBufferSizeLimits`。
→ 没有这些，两个问题都无法调试，而 NAudio 主动隐藏了两种失败（§7.2）。

---

## 11. 参考

**NAudio 3.x**
- [Docs/WasapiPlayer.md](https://raw.githubusercontent.com/naudio/NAudio/main/Docs/WasapiPlayer.md) · [Docs/WasapiRecorder.md](https://raw.githubusercontent.com/naudio/NAudio/main/Docs/WasapiRecorder.md)
- [Docs/MigratingFromNAudio2.md](https://raw.githubusercontent.com/naudio/NAudio/main/Docs/MigratingFromNAudio2.md) · [Docs/Resampling.md](https://raw.githubusercontent.com/naudio/NAudio/main/Docs/Resampling.md)
- [Docs/WasapiLoopbackCapture.md](https://raw.githubusercontent.com/naudio/NAudio/main/Docs/WasapiLoopbackCapture.md) · [Docs/MixMicrophoneAndSystemAudio.md](https://raw.githubusercontent.com/naudio/NAudio/main/Docs/MixMicrophoneAndSystemAudio.md)
- [PR #1409 (TFM 解析矩阵)](https://github.com/naudio/NAudio/pull/1409) · [issue #1407](https://github.com/naudio/NAudio/issues/1407) · [#1442 (player 卡死)](https://github.com/naudio/NAudio/issues/1442) · [PR #1369](https://github.com/naudio/NAudio/pull/1369)

**Windows Core Audio / 蓝牙**
- [IAudioClock::GetPosition](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclock-getposition) · [GetFrequency](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-iaudioclock-getfrequency)
- [IAudioClient::GetStreamLatency](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getstreamlatency) · [GetBufferSize](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-getbuffersize) · [IAudioClient2::GetBufferSizeLimits](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient2-getbuffersizelimits) · [IAudioClient3::GetSharedModeEnginePeriod](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient3-getsharedmodeengineperiod)
- [KSPROPSETID_BtAudio](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/kspropsetid-btaudio) · [KSPROPERTY_ONESHOT_RECONNECT](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-reconnect) · [KSPROPERTY_ONESHOT_DISCONNECT](https://learn.microsoft.com/en-us/windows-hardware/drivers/audio/ksproperty-oneshot-disconnect)
- [Endpoint ID Strings（opaque）](https://learn.microsoft.com/en-us/windows/win32/coreaudio/endpoint-id-strings) · [EndpointFormFactor](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/ne-mmdeviceapi-endpointformfactor) · [IMMNotificationClient](https://learn.microsoft.com/en-us/windows/win32/api/mmdeviceapi/nn-mmdeviceapi-immnotificationclient)
- [WinRT APIs 在 desktop app 中的支持情况（PairAsync 不支持）](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/winrt-api-desktop-app-support)

**DSP / 声学 / 感知**
- [Farina, AES 108th (2000) — swept-sine IR + distortion](https://cir.nii.ac.jp/crid/1570009750250244864) · [Berg et al., arXiv 2208.04654 — GCC-PHAT](https://ar5iv.labs.arxiv.org/html/2208.04654)
- [SoundGuys — Android Bluetooth latency](https://www.soundguys.com/android-bluetooth-latency-22732/) · [BT Core Spec active clock ≤±50 ppm](https://dev.ti.com/tirex/content/simplelink_cc13xx_cc26xx_sdk_7_10_02_23/docs/ble5stack/ble_user_guide/html/ble-stack-5.x/custom-hardware-cc13xx_cc26xx-ble.html)

**可借鉴的 MIT 参考实现（注意署名）**
- [AudioHQ](https://github.com/underfusion/AudioHQ) —— `AdaptiveResampler` 的 trough-P 控制律及 `EngineTunables` 全部常数、
  引擎骨架（事件同步 + push 模式回退、每输出独立 try/catch、每次激活取新 `MMDevice`）。
  **它没有每设备延迟层，本项目是它的真超集。**
- [SoundSync](https://github.com/sugumar247/SoundSync) —— 每设备手动延迟元的形状（但**修掉**它的两个 bug：
  调小延迟时立即 Dequeue 会跳音；以及用交错 float 计数）。
- [double-headphones](https://github.com/maayaranai/double-headphones) —— 0–2000ms 范围 + 数字输入框 +
  对「蓝牙延迟是物理定律」的诚实措辞。**注意：Proprietary 许可，且未发布源码，不可借用代码。**
