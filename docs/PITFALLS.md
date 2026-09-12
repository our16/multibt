# 已核实的坑（实现前必读）

> 这份清单里的每一条都在本机核实过（`dotnet build` / 读 NAudio 源码 / 读微软文档），
> **不是推测**。目的是让实现者不重复踩这些坑。
>
> 每条都标注：**结论 → 证据 → 必须怎么做**。
> 未核实的内容集中在最后一节（UNCONFIRMED），**不要按假设写死**。

---

## A. NAudio 3.x —— 与 2.x 的心智模型不同

### A1. ⚠️ TFM 必须带 `-windows` 后缀，否则 WASAPI **静默消失**

**结论**：`net10.0` 会解析到 NAudio 的**可移植 `net9.0` 依赖组**，整个 WASAPI 栈消失，
**没有任何报错或警告**。这是 NAudio #1407，3.0.1 才修。

**证据**：nuspec 依赖组为 `net9.0` / `net9.0-windows7.0` / `net9.0-windows10.0.19041`；
本机实测 `net10.0-windows10.0.19041.0` → 解析到 `net9.0-windows10.0.19041`，8 个包全部到位。

**必须怎么做**：
```xml
<TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
```
NAudio **不支持** `net8.0` / `netstandard2.0` / `net472`（会 `NU1202` 硬失败）。最低 `net9.0`。

---

### A2. ⚠️ 3.1.0 带两个 bug，只在上游**未发布**版本修掉

**不要**用「升级 NAudio」来规避——3.1.1 尚未发布。

#### #1412 —— `WdlResamplingSampleProvider` 丢样本，最终永久返回 0
在源供给不足时会丢样本并最终**永久返回 0**。它打断的正是「从 `BufferedWaveProvider` 喂数据」
这个**常见**模式——**恰好是本项目的模式**。

→ **规避：漂移补偿不使用 `WdlResamplingSampleProvider`，自己包装 `NAudio.Dsp.WdlResampler`**（见 `SPEC.md` §6.4）。

#### #1442 —— `WasapiPlayer` 卡在 `Playing` 状态
当源的 `Read` 抛异常、或源在首个缓冲填满前就结束，player 会被**永久卡在 `Playing`，无法再播放**
（3.1.0 的清理逻辑写在 `try` 末尾而不是 `finally`）。

→ **规避：恢复路径必须 `new` 一个全新 player，绝不复用可能已卡住的实例。**
→ 同时订阅 `PlaybackStopped` **并**加一个看门狗。

---

### A3. ⚠️ 捕获/播放 API 已换代

| 2.x（旧） | 3.x（用这个） |
|---|---|
| `WasapiLoopbackCapture` / `WasapiCapture` | **`WasapiRecorder` + `WasapiRecorderBuilder`** |
| `WasapiOut` | **`WasapiPlayer` + `WasapiPlayerBuilder`**（`WasapiOut` 仍在但已被 supersede） |
| `IMMNotificationClient` + `RegisterEndpointNotificationCallback` | **`MMDeviceNotificationClient` 事件 API**（旧接口已变 `internal`） |

```csharp
// 捕获
var recorder = new WasapiRecorderBuilder().WithLoopbackCapture().WithDevice(dev)
    .WithEventSync().WithBufferLength(50).Build();
recorder.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => { /* ReadOnlySpan<byte>！ */ };

// 播放
var player = new WasapiPlayerBuilder().WithDevice(dev).WithEventSync()
    .WithLatency(100).WithRawMode().WithMmcssThreadPriority("Pro Audio").Build();

// 通知
using var n = enumerator.CreateNotificationClient(useSynchronizationContext: false);
n.DeviceStateChanged += (_, e) => { /* e.DeviceId, e.NewState */ };
```

**附加事实**：
- 音频类型（`ISampleProvider`、`IWaveProvider`、`WaveFormat`、`BufferedWaveProvider`、
  `VolumeSampleProvider`、`SampleToWaveProvider`…）**没有改名也没有换命名空间**。
- `WaveFormat` 在 3.1.0 起**不再有 `[StructLayout]`** → 对它调 `Marshal.SizeOf` 会**抛异常**。
- `AudioClient.IsFormatSupported` 的 `out` 参数从 `WaveFormatExtensible` 改成了 `WaveFormat`。
- `MMDevice.AudioClient` 已 `[Obsolete]` → 用 `CreateAudioClient()`。
- `BufferedWaveProvider` 的容量现在是**仅构造函数参数**，`BufferLength`/`BufferDuration` **只读**。

---

### A4. ⚠️ `WasapiPlayer.Init` 非线程安全，且每个实例只能调一次

**证据**：`Init` 里没有锁，且会改动共享状态（`waveProvider`、`OutputWaveFormat`、`audioClient`、
`renderClient`…），在两条路径里还会**销毁并重建 `audioClient`**（IAudioClient3 回退、独占模式重试）。

**必须怎么做**：每个实例在 `Play` 之前调用一次，**绝不**与 `Play`/`Stop`/`Dispose`/另一个 `Init` 并发。
要换音源就 `Dispose` 旧的、`new` 一个新的。

---

### A5. ⚠️ MMCSS 只能通过 builder 开启

**证据**：`AvSetMmThreadCharacteristics` 所在的 `NativeMethods` 是 `internal static partial class`。

→ **无法**绕过 builder 自己调。必须用 `.WithMmcssThreadPriority("Pro Audio")`。
每个 player 在自己的线程上各自注册一次。

---

### A6. ⚠️ 音量有三个层级，选错会出大事

| 属性 | 含义 | 本项目 |
|---|---|---|
| `player.Volume` / `IsMuted` | **session** 音量（本应用在音量合成器里的滑杆） | 可用，但不是每设备独立的 |
| `player.SessionVolume` / `StreamVolume` | session 的完整控制 / 本流每声道 | 不用 |
| `player.DeviceVolume` | **端点全局**音量，影响**所有应用** | **禁止用于每设备音量** |

→ **每设备音量必须用链上的 `VolumeSampleProvider`。**
（另注：`SoundDeck` 就是写 `AudioEndpointVolume.MasterVolumeLevelScalar`，那是端点音量，不是流增益。）

---

### A7. ⚠️ `IWavePosition.GetPosition()` **会掩盖漂移**

**结论**：它返回的是 `AdjustedPosition`，实现里用**标称** `Frequency` 做了外推：
```csharp
pos += ((qposNow - qpos) * Frequency) / TimeSpan.TicksPerSecond;
```
最后这一行**假设了零漂移**。拿它做回归会**在偏差 200ppm 的设备上读出 ~0ppm**——
正好把你唯一想测的量插值掉了。

→ **必须用裸的一对**：`AudioClockClient.GetPosition(out ulong pos, out ulong qpos)`。
→ 丢弃返回 `S_FALSE` 的读；注意连续两次读可能**完全相等**（驱动每个引擎周期才更新一次）。

---

### A8. ⚠️ NAudio 默认隐藏两种失败模式

| 隐藏的失败 | 机制 | 后果 |
|---|---|---|
| **饿死不可见** | `ReadFully = true`（**默认**）时 `Read` **零填充并仍返回 `count`** | 饿死静音与真实静音**无法区分** |
| **溢出静默丢内容** | `DiscardOnBufferOverflow = true` 时 `AddSamples` 写入能放下的部分、**静默丢弃最新样本** | 无异常、无计数器 |

→ 每次 read **前后**采样 `BufferedDuration` 并计数，自己实现 starve/overflow 计数器。
→ 本项目 `DiscardOnBufferOverflow` 保持 **false**，改为在有界 backlog 上显式 `ClearBuffer` 重同步。

---

### A9. ⚠️ loopback 静音时不投递数据

**证据**：NAudio 文档原文——`DataAvailable` 只在设备**真的在播声音**时才触发。
「如果要捕获连续的静音，就在设备上播放静音。」

→ 每条链的 ring 必须能**独立产出静音**，`ReadFully = true` 是**必须**的。
否则 `FillBuffer` 把 0 字节读当作**流结束**，该 player **永久停摆**。

---

### A10. ⚠️ `devicePosition` / `qpcPosition` 不能盲信

**证据**：NAudio 自己的文档指出，在部分共享模式驱动上「第一个包是真的，之后为 **0**」。

→ 必须**校验**（连续包 position 单调、增量非零），通过后才用于诊断。
→ **任何情况下都不进控制回路**：捕获侧位置只反映**捕获**端点时钟，对你**输出**设备一无所知。

---

### A11. ⚠️ `IMMNotificationClient` 已 `internal`

**证据**：3.x 里 `RegisterEndpointNotificationCallback` 是 `internal int`。
`Docs/MigratingFromNAudio2.md` 给出了旧→新写法。

→ 用 `enumerator.CreateNotificationClient()` 返回的 `MMDeviceNotificationClient` 事件。
→ `useSynchronizationContext: false` 时回调在音频/COM 线程上：**必须非阻塞，且不得回调进音频栈
（例如 dispose 一个 player），否则有死锁风险**。
→ `PropertyValueChanged` **每次音量变化都触发**（高频），处理器必须极轻。

---

### A12. ⚠️ 两个现成的重采样器都**不能**在运行时改比率

| 类型 | 命名空间 | 运行时改比率？ |
|---|---|---|
| `WdlResamplingSampleProvider` | `NAudio.Wave.SampleProviders` | ❌ 内部 `WdlResampler` 是 `private readonly` |
| `MediaFoundationResampler` | `NAudio.Wave` | ❌ `CreateTransform()` 推入**固定**输出格式 |
| **`WdlResampler`** | **`NAudio.Dsp`** | ✅ **`SetRates(double, double)` 是 public** |

重建前两者会重置 `m_fracpos` 和滤波状态 → **不连续/爆音**。

**`SetRates` 可以安全地在流中途调用**：源码里只在值变化时重算 `m_ratio`，
**不碰** `m_fracpos`、输入缓冲和 IIR 滤波历史 → 相位保持。

→ **自己写一个 `ISampleProvider` 包装 `NAudio.Dsp.WdlResampler`。**

**两个附带要点**：
- `sinc` 模式会在 `filtpos` 变化时 `Array.Resize` + 重建 2048 抽头表——
  在 5Hz 控制回路里等于每 200ms 在音频线程重建一次。**必须保持 interp 模式**。
- `ResampleOut` 可能返回**少于**请求的帧数 → 必须循环填充，**绝不能返回 0**（见 A9）。

---

## B. 算法 —— 会静默毁掉同步的设计错误

### B1. 🔴 最大陷阱：把「漂移缓冲」和「补偿延迟」合成一个缓冲

**如果把补偿延迟实现成 ring buffer 的读指针偏移**，那么 `fill` 和 `delay` 就是**同一个变量**——
控制器会忠实地把它稳定在 5ms，而你那 170ms 的补偿**悄悄消失了**。

→ **两个独立缓冲**：ring = 漂移（被控量）；delay line = 补偿（只在补偿值变化时改）。
→ **顺序：先重采样，后延迟**（延迟线因此运行在设备 mix 速率上，算术精确，控制器无法扰动它）。

---

### B2. 🔴 测量值作为**绝对值**是不可测的

麦克风自身通路（ADC + USB/驱动缓冲）给**每一次**测量加一个**未知常量**。
它在**差值**中抵消，**在绝对值中不抵消**。

→ 字段必须定义为**相对参考设备**的延迟。
→ **绝不允许** UI 把最大值当作「你的系统延迟 240ms」展示——会错几十毫秒。

---

### B3. ⚠️ 对齐到最慢设备会让**全部**输出变晚

对齐到最慢设备把系统端到端延迟设为 `max(L_i)`。蓝牙 200–400ms 时，
**超过 ITU-R BT.1359-1 的 125ms 唇音同步可察觉阈值**。

→ 在同时有蓝牙音箱和 DAC 的机器上，**「已同步」和「能看视频」互斥**。
→ UI **必须**显示系统总延迟并提供「音乐模式 / 视频模式」选择。**不要藏起来。**

---

### B4. ⚠️ 分布是**右偏**的 → 用中位数，不要用 P95

反射和 codec 抖动只会**增加**延迟。P95 有意去采样混响偏置。

→ **中位数**作为点估计；`P90 − P10` 只作**不确定度**上报。

---

### B5. ⚠️ 检测算法：匹配滤波 + **前沿**阈值，不是 argmax，也不是 GCC-PHAT

- **不要 GCC-PHAT**：它是为「源信号未知」准备的。我们有已知参考信号，匹配滤波已是最优；
  PHAT 给所有频点单位权重，会在音箱/麦克风根本没信号的频点把纯噪声提上来，**主动变差**。
- **不要 `argmax`**：混响下相关峰会**偏向能量重心（偏晚）**，因为直达声和早期反射都与参考相关。
- **要**：包络上**第一次越过自适应阈值**的位置 → 目标直指**直达声到达**。
  这是把精度从 ±10ms 提升到 ±1ms 的**唯一最关键实现细节**。

---

### B6. ⚠️ 空气传播项在**同房间**测量时**不是误差**

麦克风放在**听音位**时，`d/343`（**2.915 ms/米**）**就是正确答案的一部分**。
减掉它会让音箱「电学同时」而「声学错开」。

→ v0.1 明确只支持**同房间**，保留空气项，UI 提示「请把麦克风放在你坐的位置」。
→ 跨房间（派对模式）才需要减，且需要用户输入距离。**推到 v0.2+。**

---

### B7. ⚠️ P 控制器**不要加 I 项**

被控对象是**积分器**（`d(fill)/dt = c − d`）。取 `c = K·e` 得 `E(t) → d/K`——
**对积分对象做 P 控制本身已具有零稳态速率误差**。

→ 加显式 I 项会让回路变成**双积分器**，这正是制造**极限环**的标准配方。**不要加。**

数值：`K = 0.5 /s`，`d = 100ppm` → 稳态偏差 `2e-4 s = 0.2ms`：
比两个相关声源的可闻阈值低 **15 倍**，而且恒定、不累积。
**→ 永远不需要知道漂移率。**

---

### B8. ⚠️ 控制**谷底**（窗口最小值），不要控制均值

无线端点的拉取粒度可以粗到 **~60ms**（AudioHQ 在 PlayStation Link 源上实测：
backlog 从 ~10ms 锯齿到 ~70ms）。针对均值控制会把谷底拖到一次 render 拉取之下 → 饿死 → 爆音。

→ 针对谷底控制，**无论突发多大，低点都安全**。

---

### B9. ⚠️ 延迟变化：小幅滑动，**大幅必须静音重同步**

- 小（≤20ms）：滑动分数读指针，速率限幅使隐含音高偏移 <0.5%。20ms 需 4s。不可闻。
- **大（>20ms）：绝不滑动。** 170ms 在 0.5% 限制下要滑 **34 秒**的明显失谐音频。
  → **静音 → 重新应用 → 10ms 淡入**。短暂静默优于半分钟滑音。

---

### B10. ⚠️ 动态修正即使很小也可能可闻（FM ≠ 静态失谐）

静态失谐的可闻阈值是 ~5–10 cents，所以 ±200ppm（0.35 cents）静态下完全安全。
**但「移动」的修正就是调频（FM），其可检测阈值远低于静态音高 JND。**

→ 三重防护：**上限 ±200ppm** + **速率限幅 ≤50ppm/tick** + **EMA 平滑（α=0.3）**。
→ 残差抖动在 1–10Hz（FM 最灵敏频段）应基本无频谱内容。**验耳：持续钢琴/人声音调。**

---

### B11. ⚠️ 全局 `f*` 必须对所有设备相同

由 `c_i(t) = R_i(t) − b_i(t)`，对齐要求 `f_i + b_i` 对所有设备为常数。
若每条链用**不同**的 `f*_i`，就等于注入了等于该差值的**静态错位**。

→ **一个全局 `engineLatencyMs`**；允许每设备覆盖，但 UI 标注「需要重新测量」。

---

### B12. ⚠️ 测量路径必须等于播放路径

用一个**单独的**校准 `WasapiOut` 会有不同的缓冲占用和拉取粒度，
**仅这一项就能引入最多 ~25ms 的误差**（对 60ms 粒度的无线端点）。

→ **架构要求：扫频必须能注入每条链的正常管线**，且漂移控制器**已锁定并预热 ≥3s**。
→ 一次只测一个设备，**其余输出在端点级静音**（消灭「录到了错设备」这一整类失败）。

---

## C. 蓝牙 —— 能做与不能做

### C1. ✅ 连接/断开**已配对**设备是可行的，**无需提权**

**做法**：Core Audio + `IKsControl` + `KSPROPSETID_BtAudio`：
`KSPROPERTY_ONESHOT_RECONNECT` / `KSPROPERTY_ONESHOT_DISCONNECT`（`KSPROPERTY_TYPE_GET`）。

**这是受支持的做法**：WHQL `Device.Audio.Bluetooth.AtleastOneProfileSupport` **要求**
蓝牙音频设备暴露这两个 oneshot 属性，以便声音控制面板能控制连接/断开。

**三个必须处理的点**：
1. **成功 ≠ 连上**：文档原文「成功只表示驱动**尝试**连接，不保证成功」→ 轮询端点状态到
   `DEVICE_STATE_ACTIVE`（有超时），失败按上限重试。
2. **必须按 container 分组**：一个物理设备暴露多个端点（A2DP render、HFP render、HFP capture），
   Windows 只在**全部**断开时才真正断开 → 按 `PKEY_Device_ContainerId` 分组，对每组每个 KS filter 都发。
3. **连接后等 3–5 秒** Windows 才初始化完所有音频设备 → 恢复走延迟队列。

---

### C2. ❌ 明确做不了的（不要尝试）

| 方案 | 为什么不行 |
|---|---|
| `BluetoothDevice.FromIdAsync` 当连接用 | 它只**构造对象**；WinRT 蓝牙 API **根本没有** connect/disconnect 动词 |
| `AudioPlaybackConnection` | **方向反了**——它让 PC 当蓝牙**音箱**接收手机音频 |
| 裸 L2CAP / AVDTP PSM 25 | 会与系统自带 `BthA2dp.sys` 的 AVDTP 信令通道**冲突** |
| `GetRfcommServicesAsync`/`StreamSocket` 当**主**机制 | A2DP-only 音箱（**多数蓝牙音箱**）根本没有 RFCOMM 服务；仅带 HFP 的耳机可用 |
| SetupAPI `DIF_PROPERTYCHANGE` disable/enable | 需要**提权**且具侵入性 |
| `PairAsync` 配对**新**设备 | 官方标注 desktop app **不支持** |
| `System.Devices.Aep.DeviceCategory` | **该属性不存在**（正确的是 `System.Devices.Aep.Category`） |
| `Windows.Media.Devices.MediaDevice` | 需要 package identity |

**→ v0.1 的承诺范围必须限定为「已配对设备」。** 「用户再也不需要打开 Windows 设置」这句话
**只对已配对设备成立**。

---

### C3. ⚠️ 重连会让测量值失效

重配会强制重新协商 codec 并可能**静默回落到 baseline SBC**，而 SBC 与 AAC 实测差 **~60ms**。
没有任何用户可见信号。

→ 端点重新注册或设备重连，**必须**标记 `measuredDelay` 失效（`SPEC.md` §5.3）。

---

### C4. ⚠️ `MMDevice.ID` 是 opaque 的，**不得解析**

微软明确声明其格式**未定义（undefined）**；生命周期绑定「设备安装」——
**驱动升级/重装会变**，重启不变，USB 拔插不变。

→ profile 认设备要**三级回退**：`device.ID` → `InstanceId` → `FriendlyName + 序号`。
→ `DeviceNotificationEventArgs.DeviceId` 同样是 opaque：**只比较，不解析**。

---

### C5. ⚠️ `MMDeviceCollection` 是快照，且每次都新建包装

索引器/枚举**每次访问都 materialise 一个全新的 `MMDevice`**。

→ 设备变化后**必须重新枚举**；每个 `MMDevice` 都要 `Dispose()`。
→ **不要缓存 `MMDevice`**：休眠/唤醒后缓存的 COM 对象会失效，即使端点仍能枚举到。
  **每次激活都取新的。**

---

## C6. ✅ 已在本机确认：`PKEY_Device_InstanceId` 不可靠（原 D3/D4）

**结论：不要直接读 `PKEY_Device_InstanceId` 做蓝牙判定——在 Windows 11 24H2 上它不存在。**

这是本项目**唯一一个「静默失效」的真实缺陷**：蓝牙检测会全部退化为 `Other`，
而应用的核心功能恰恰就是蓝牙。它是靠 `tools/SmokeCheck` 实测出来的，不是猜出来的。

**实测数据（20 个 render 端点）：**

| 观察项 | 结果 |
|---|---|
| `PKEY_Device_InstanceId`（`{78c34fc8-104a-4aca-9ea4-524d52996e57}, 256`） | ❌ **20 个端点全部缺失**，`TryGetValue` 返回 `false` |
| `MMDevice.InstanceId` | ❌ 空字符串（它就是上面那个键） |
| 真正的实例 id 在哪 | ✅ `{b3f8fa53-0004-438e-9003-51a46e139bfc}` **pid=2**，值形如 `{1}.HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&...\4&29ACFEC2&0&0001` |
| 设备接口路径 | ✅ 同 formatId **pid=11**，形如 `{2}.\?\hdaudio#func_01&...#...\rearlineoutwave3` |
| `PropertyStore[i]` 索引器 | ⚠️ 对 blob 型属性**抛** `CoreAudioException 0xE000020B`（每个端点都有数个），**必须 try/catch** |

**两个独立的坑，都会让朴素前缀匹配失败：**

1. **`{N}.` 序号前缀**——真实值以 `{1}.` 开头。
2. **`#` 分隔符**——设备路径用 `#` 代替 `\`，并可能带 `\\?\` 前缀。
   而且**序号在外层**：`{2}.\?\hdaudio#...` 里的 `\\?\` 藏在序号**后面**，
   所以「先剥 `\\?\` 再剥序号」这种单次有序处理**永远匹配不到**，必须**循环剥离**。
   （这个 bug 是被单元测试抓住的，见 `EndpointIdentityReaderTests`。）

**修复方式（已实现）：** `src/MultiBT.Core/Devices/EndpointIdentityReader.cs`
统一规范化（循环剥 `{N}.` / `\\?\`，`#`→`\`），按
`PKEY_Device_InstanceId` → `PKEY_Device_ControllerDeviceId` → 接口路径的链条回退，
并排除 `SWD\MMDEVAPI\` 这个「软件设备包装」（**每个**端点都有一条，与传输方式完全无关，
绝不能让它在竞争中胜出）。

**效果（同机实测对比）：**

| 设备 | 修复前 | 修复后 |
|---|---|---|
| 扬声器 (智能音箱 Pro-3420) | `Other` | ✅ **`Bluetooth`** |
| 耳机 (MI Portable Speaker) | `Other` | ✅ **`Bluetooth`** |
| 数字音频(HDMI) ×4 | `Hdmi`（靠设备名猜中） | `Hdmi` |

→ 修复前蓝牙分类**静默失效**：不报错、不崩溃，只是永远判定不出蓝牙。

**附带的经验：** 端点属性可用性是**环境相关**的（微软文档承认这一点），
所以任何身份/分类逻辑都必须有回退链，并且**在目标机器上打日志核对真实值**。
`tools/SmokeCheck` 的 `[3b]` 段会 dump 原始属性表，就是为这件事存在的。

---

## G. 一类值得单独记录的缺陷：**声明了但从未接线**

本轮抓到的最严重问题不属于任何 API 细节，而是一类结构性缺陷，值得写进原则里。

**观察**：`TrayHost` 和 `SingleInstance` 两个类**写得很完整**——事件、注释、菜单构造一应俱全——
但**全仓库没有任何地方构造它们**，只有它们自己引用自己。
`App.xaml` 仍然是 `StartupUri="MainWindow.xaml"`，托盘图标在运行时**根本不存在**。

**为什么危险**：

1. **它编译通过、测试通过、GUI 也能正常起来。** 主窗口照常显示，一切看起来是好的。
2. **静态检查抓不到。** 公共类 + 公共构造函数 + 无警告。CS0067（事件从不使用）是**唯一**的线索，
   而它只出现在 `TrayHost` 内部的两个事件上，很容易被当成噪音忽略。
3. **进度文档写「✅ 完成」时，没人会去验证图标是否真的出现过。**

**结果**：`docs/GOALS.md` 的 **G6**（托盘可完成全部日常操作，日常不打开主窗口）
——也就是这个产品**最核心的价值主张**——完全未达成，而所有检查都是绿的。

**防范规则（本项目从此遵循）**：

- **任何标记为「完成」的功能，必须指出它在运行时被谁构造/调用。** 找不到调用点就是没完成。
- **把 CS0067 当真实信号看**：一个「从不使用的事件」通常意味着**没人在 raise 它**，
  也就是某个 UI 动作点了没反应。不要用 `#pragma` 压掉。
- **入口点接线要有验证**：本轮就是靠「启动两次、检查第二个实例是否自行退出、检查存活进程数」
  才发现单实例从未生效的。
- 对 UI 可见的功能，**用进程级观察验证**（窗口标题、进程数、托盘图标），而不是只看编译结果。

---

## H. 其他已确认的静默失效（均已修复，记录以免重犯）

| 缺陷 | 症状 | 根因 |
|---|---|---|
| `WdlResamplingSampleProvider` 用于 44100↔48000 | **部分蓝牙设备永久静音**，有线设备正常 | NAudio 3.1.0 缺陷 #1412：源供给不足时丢样本并最终永久返回 0。**只在采样率不同时才插入该转换**，于是失败精确地落在 44.1kHz 的蓝牙设备上。见 A2 |
| `_liveChannelDevices` 键不一致 | COM 对象泄漏、端点长期被占用 | 字典以 **endpoint id** 为键，但移除时用 **deviceKey** 去查，永远查不到 |
| raw mode 回退路径 | 泄漏一个已打开 `AudioClient` 的 player | `Init` 抛异常后直接新建第二个 player，第一个从未 Dispose。**蓝牙端点正是走这条路的**（常缺 `IAudioClient2`） |
| `SingleInstance.IsFirstInstance` | 单实例判定错误 / 互斥体释放不掉 | 对**已持有**的互斥体调 `WaitOne(0)`：跨线程返回 false，且增加递归计数 |
| `async void` 事件处理器 | 抛异常**直接终止进程** | 该处理器负责运行时重配音频设备，必须由 `Task` + try/catch 包装 |
| 诊断窗口内无数据被当成饿死 | 误报「饿死」 | `FillMinSeconds` 在无读取时返回 0；应返回 NaN 表示「无数据」 |

---

## D. 仍需在真机验证的 UNCONFIRMED 项

**不要按假设写死，要实测并打日志核对。**

（D3/D4 已在上面 C6 确认并修复，此处保留原编号以便对照。）

| # | 待验证 | 影响 | 回退策略 |
|---|---|---|---|
| 1 | `devicePosition`/`qpcPosition` 在目标机器的 loopback 上是否真非零且单调 | 仅诊断 | 不通过就完全不用；不影响控制回路 |
| 2 | `KSPROPERTY_ONESHOT_RECONNECT` 对**耳机**和 **A2DP-only 音箱**是否都生效 | 蓝牙连接功能 | 失败时 UI 显示「请在 Windows 设置里连接」 |
| 3 | `PKEY_Device_ContainerId` 在蓝牙 render 端点上是否被填充 | 蓝牙分组 | 回退到 BTHENUM 路径里的 MAC 字符串匹配 |
| 4 | `BTHENUM\` 前缀的真实形态 | 传输方式分类 | 分类器无法判定时安全回退 `Other`（不崩、延迟区间放宽） |
| 5 | `System.Devices.Aep.DeviceAddress` 的字符串格式（12 位 hex 还是带冒号） | 蓝牙↔端点关联 | 用 `BluetoothDevice.BluetoothAddress` 格式化 `X12` 交叉校验 |
| 6 | `IAudioClock` 在蓝牙 A2DP 端点上的位置反映**远端 DAC** 还是 Windows 合成 | ppm 上报是否有意义 | 无意义就只保留 fill 观测 |
| 7 | 各设备实测 `GetBufferSizeLimits` / `GetDevicePeriod` | 决定 `engineLatencyMs` | 蓝牙保守用 100ms preset |
| 8 | **±0.05% 的流中途 `SetRates` 听感是否真的无爆音** | 漂移补偿质量 | **源码显示无不连续机制，但 NAudio 没有任何文档保证** → 对比率做斜坡（~100ms 内 0.05%）+ 持续音调验耳 |
| 9 | 三台蓝牙设备同播时，单个蓝牙适配器是否够用 | 产品可行性 | 已知问题：单个内置适配器可能撑不住两路流，需备用 USB 蓝牙适配器（~10 美元） |
| 10 | 耳机 A2DP↔HFP 自动切换导致的抖动 | 镜像稳定性 | 唯一可靠抑制手段是**提权**禁用 HFP 端点 → **v0.1 接受抖动，不提权**。**不要同时承诺「无需提权」和「完全稳定」。** |

---

## E. 环境

### E1. ⚠️ `api.nuget.org` 在本机直连会 TLS 失败

**现象**：`dotnet restore` 报
`NU1301: 无法加载源 https://api.nuget.org/v3/index.json 的服务索引 / The SSL connection could not be established`。

**根因**：本机有 Clash 在 `127.0.0.1:7890`，但 `ProxyEnable=0` 且**没有** `HTTP(S)_PROXY` 环境变量。
（普通 HTTP 可达，**所有 HTTPS** 都失败。）

**解决**：开启本地代理，或
```powershell
$env:HTTPS_PROXY = 'http://127.0.0.1:7890'; dotnet restore
```

### E2. `NAudio.Core` 的传递依赖

`System.Numerics.Tensors 9.0.0` 是 `NAudio.Core` 的**传递运行时依赖**（不在 .NET 10 BCL 里），
会出现在 publish 输出里。**不要**试图排除它。

### E3. 项目必须是 x64

WASAPI/COM interop 场景不要用 AnyCPU。统一：
```xml
<Platforms>x64</Platforms><PlatformTarget>x64</PlatformTarget>
```

---

## F. 编译期契约检查

`tools/ApiProbe/` 把本文档要求的所有 API 写进一个**只做编译检查**的文件。
未来 NAudio 改名或删成员时，它会**编译失败**，而不是在流中途运行时失败。

```powershell
dotnet build tools/ApiProbe/ApiProbe.csproj
```

**状态：已在 `NAudio 3.1.0` + `net10.0-windows10.0.19041.0` 上通过（0 警告 0 错误），
并做过负向对照**（故意引用不存在的成员，确认构建真的会失败——防止「探针其实没在编译」这种假通过）。
