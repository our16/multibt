# 实现任务拆分（交给编码 Agent）

> 每个里程碑都是**可独立验证**的增量。**按顺序做，不要跳。**
> 每个任务都给出：目标 / 交付物 / 验收 / 注意。
> 动手前先读 `docs/SPEC.md` 和 `docs/PITFALLS.md`。
>
> **贯穿全程的三条铁律（违反任何一条都会导致难以调试的音频故障）：**
> 1. `TargetFramework` 必须是 `net10.0-windows10.0.19041.0`（PITFALLS A1）。
> 2. fan-out = 每条链**自己**的 `BufferedWaveProvider`，**绝不**多 reader 共用一个。
> 3. 「漂移缓冲」和「补偿延迟」必须是**两个**缓冲（PITFALLS B1）。

---

## M0 — 骨架与可编译基线

**目标**：`dotnet build` 通过，窗口能起来，但没有音频。

- [ ] 创建 `MultiBT.sln`、`Directory.Build.props`（统一 TFM / `Nullable` / `LangVersion` / x64 / `Platforms`）
- [ ] `src/MultiBT.Core`（classlib）+ `src/MultiBT.App`（WPF）+ `tests/MultiBT.Core.Tests`（xunit）
- [ ] 引用 `NAudio 3.1.0`、`H.NotifyIcon.Wpf` 2.4.1、`CommunityToolkit.Mvvm`
- [ ] `MultiBT.App` 设置 `<UseWPF>true</UseWPF>`，App.xaml 起一个空 `MainWindow`
- [ ] `.gitignore`（`bin/`、`obj/`、`*.user`）

**验收**：`dotnet build` 0 error；`dotnet run --project src/MultiBT.App` 出现空窗口。

**注意**：`Directory.Build.props` 里查 `NAudio` 版本集中管理，方便将来升 3.1.1。

---

## M1 — 设备枚举与分类（无音频）

**目标**：能把所有 render 端点列出来，并正确标注传输方式。

- [ ] `Devices/AudioEndpointInfo.cs`：DTO（`Id`、`InstanceId`、`FriendlyName`、`Transport`、`State`、`FormFactor`）
- [ ] `Devices/TransportClassifier.cs`：按 `InstanceId` 前缀分类 `Bluetooth` / `Usb` / `Hdmi` / `Other`
- [ ] `DeviceManager.EnumerateRenderEndpoints()`：`DeviceState.All`，**每个 `MMDevice` 都 Dispose**
- [ ] 单元测试：分类器对若干真实 `InstanceId` 样例的判定 + **未知形态安全回退 `Other`**

**验收**：控制台/WPF 列表显示本机全部 render 端点，蓝牙/HDMI/USB 标注正确。

**注意**：
- 必须用 `DeviceState.All`——**已配对未连接的蓝牙端点是 `Unplugged` 而不是 `Active`**。
- **实测并打印 `InstanceId` 真实值**再定分类规则（PITFALLS D4）。
- **不要解析 `MMDevice.ID`**（opaque，PITFALLS C4）。

---

## M2 — 单设备镜像（walking skeleton）

**目标**：默认输出设备的声音，从**另一个**设备播放出来。这是最关键的第一步。

- [ ] `Audio/AudioSource.cs`：`WasapiRecorderBuilder().WithLoopbackCapture()...`，
      `DataAvailable` 里把 `ReadOnlySpan<byte>` push 进下游
- [ ] `Audio/OutputChannel.cs`：一条链的状态与所有权（ring → player）
- [ ] `Audio/AudioEngine.cs`：`Start()` / `Stop()` 生命周期
- [ ] ring = `BufferedWaveProvider(fmt, 2s) { ReadFully = true, DiscardOnBufferOverflow = false }`
- [ ] player = `WasapiPlayerBuilder().WithDevice(dev).WithEventSync().WithLatency(100).WithRawMode()
      .WithMmcssThreadPriority("Pro Audio").Build()`

**验收**：播放音乐，声音从第二个设备出来，**连续 30 分钟无爆音、无断续**。

**注意**：
- `DataAvailable` 的 span **只在回调期间有效** → 立刻分发，不要存起来异步处理。
- 捕获格式用**设备 mix format**，不要强行改。
- **静音时 loopback 不投递数据** → `ReadFully = true` 是必须的（PITFALLS A9）。
- raw mode 不支持时 `Init` 会抛 `InvalidOperationException` → 捕获并去掉该选项重试一次。

---

## M3 — 多设备 fan-out + 每设备音量

**目标**：3 个设备同时出声，各自音量独立。

- [ ] `AudioEngine` 持有 `List<OutputChannel>`，捕获回调里**循环 push 到每一条链**
- [ ] 每条链的捕获分发用**独立 `try/catch`**——一个设备坏掉不能拖垮整个会话
- [ ] 每设备链上加 `VolumeSampleProvider`
- [ ] `Audio/LevelMeter.cs`（`MeteringSampleProvider`）
- [ ] UI：设备勾选列表 + 每设备音量滑杆 + 电平条

**验收**：3 设备同播；拖动一个滑杆其他设备音量不受影响；拔掉一个设备其他两个继续正常。

**注意**：
- **绝不**让两个 player 读同一个 provider 实例（`BufferedWaveProvider` 不支持多读者）。
- **绝不**用 `player.DeviceVolume` 做每设备音量（那是端点全局，PITFALLS A6）。

---

## M4 — 补偿延迟线 + 延迟模型

**目标**：能把某个设备延后 N ms，使多设备听感同步（手动）。

- [ ] `Audio/DelaySampleProvider.cs`：**整数样本**延迟线，运行在**设备 mix 速率**上
      （`delaySamples = round(ms/1000 * Fs)`）
- [ ] 链顺序固定：`ring → (固定重采样) → AdaptiveResampler(暂为直通) → DelaySampleProvider → Volume → Meter`
- [ ] `Sync/LatencyModel.cs`：**唯一的**有效延迟计算入口
      `ComputeEffectiveDelaySamples(settings, fs, maxDelayMs)`
- [ ] 延迟变化策略：≤20ms **滑动**（速率限幅，隐含音高偏移 <0.5%）；>20ms **静音→重应用→10ms 淡入**
- [ ] UI：每设备 `measuredDelay`（只读）+ `compensationMs`（只读）+ 「微调」按钮（`manualOffsetMs`，有符号）
- [ ] 「在单个设备上播放测试音」按钮
- [ ] 单元测试：`LatencyModel` 的夹紧、符号、整数样本取整

**验收**：两个设备，一个 0ms 一个手动 +150ms，**耳听无 flam**；快速拖动滑杆**不出现爆音**。

**注意**：
- **延迟线必须独立于 ring**（PITFALLS B1）。
- `manualOffsetMs` **有符号**且夹紧；UI 显示**生效值**而不是裸 offset（当 `compensationMs == 0`
  时负 offset 没有移动空间）。
- 大变化**绝不**用滑动（170ms 要滑 34 秒的失谐音频，PITFALLS B9）。

---

## M5 — 漂移补偿（**最重要**）

**目标**：播放 1 小时，设备间偏差仍 <5ms。

- [ ] `Audio/AdaptiveResampler.cs`：包装 `NAudio.Dsp.WdlResampler`
      （`SetMode(interp:true, filtercnt:2, sinc:false)` + 运行时 `SetRates`）
- [ ] `Audio/EngineTunables.cs`：所有常数集中（见 `SPEC.md` §6.3 表）
- [ ] `Sync/SyncController.cs`：5Hz，**trough** → EMA(α=0.3) → P(K=0.5) → clamp(±200ppm) → rate-limit(50ppm/tick)
- [ ] 把 `AdaptiveResampler` 插进每条链（M4 的直通位置）
- [ ] `BufferedDuration` 在每次 read **前后**采样，实现 starve / overflow 计数器
- [ ] 预热：启动后 3s 内不做补偿决策
- [ ] 有界重同步：`backlog > engineLatencyMs + 25ms` → 记日志 + `ClearBuffer()`

**验收**：
- 3 设备同播 **60 分钟**，用 `SPEC.md` §6.6 的方法观测，**偏差 <5ms**
- 无爆音、无「哇音」、无可闻失谐（**持续钢琴/人声音调验耳**）
- 诊断显示 correction 在 ±200ppm 内，且在收敛后有界

**注意**：
- **不要用 `WdlResamplingSampleProvider`**（3.1.0 的 #1412 bug，PITFALLS A2）。
- **`ResampleOut` 可能返回少于请求的帧数** → 循环填充，**绝不返回 0**（否则 player 永久停摆）。
- **保持 interp 模式**（sinc 模式每 200ms 会重建 2048 抽头表，PITFALLS A12）。
- **不要加 I 项**（PITFALLS B7）。
- **控制谷底而不是均值**（PITFALLS B8）。
- 全局 `f*` 对所有设备相同（PITFALLS B11）。
- ±0.05% 流中途 `SetRates` 的听感**必须实测** → 对比率做斜坡过渡（PITFALLS D8）。
- **`GetPosition()` 不能用**来测漂移（它假设零漂移，PITFALLS A7）。诊断要用裸 `AudioClockClient.GetPosition`。

---

## M6 — Profile 持久化

**目标**：保存/加载场景；重启后自动恢复。

- [ ] `Config/Models.cs`：严格对应 `SPEC.md` §8 的 schema（含 `schemaVersion`）
- [ ] `Config/ProfileStore.cs`：`%APPDATA%\MultiBT\profiles.json`，**原子写**（临时文件 + `File.Replace`）
- [ ] 设备身份**三级回退**：`endpointId` → `instanceId` → `friendlyName + 序号`
- [ ] 解析失败 → 重命名为 `profiles.corrupt-<ts>.json` + 默认配置启动 + **UI 明确提示**（不崩、不静默重置）
- [ ] UI：场景 tab + 保存/重命名/删除

**验收**：配置 3 个设备 → 退出 → 重启 → **自动回到同样状态**；手工破坏 JSON → 应用仍能启动并提示。

**注意**：`MMDevice.ID` 是 opaque 且**驱动重装会变**（PITFALLS C4）→ 必须三级回退。

---

## M7 — 设备通知与自动恢复

**目标**：设备掉线/重连自动恢复，用户无感。

- [ ] `DeviceManager` 订阅 `enumerator.CreateNotificationClient(useSynchronizationContext: false)`
- [ ] 回调**只 post 到队列后立刻返回**（在里面碰音频栈**会死锁**，PITFALLS A11）
- [ ] `Devices/RecoveryPolicy.cs`：实现 `SPEC.md` §7.1 的状态机
- [ ] 恢复**必须 `new` 一个全新 `WasapiPlayer`**（3.1.0 #1442 卡死 bug）
- [ ] 重连后**延迟 3–5 秒**再开流
- [ ] 指数退避，**最多 3 次**；失败进 `Failed`，只有用户操作能离开
- [ ] 设备 `UNPLUGGED` → 拆链但**留在期望集合**里，下次 `ACTIVE` 自动重新武装
- [ ] 异常映射表（`SPEC.md` §7.3）
- [ ] 关闭顺序（`SPEC.md` §7.4），退出前**先淡出**

**验收**：播放中拔掉一个设备 → 其余不受影响；插回 → **自动恢复**；
蓝牙断开再连 → 自动恢复；全程**无崩溃、无死锁、无卡死状态**。

**注意**：`PropertyValueChanged` 每次音量变化都触发 → 处理器必须极轻。

---

## M8 — 蓝牙连接管理

**目标**：UI 里能连接/断开**已配对**的蓝牙音频设备。

- [ ] `Devices/BluetoothConnector.cs`：Core Audio + `IKsControl` + `KSPROPSETID_BtAudio`
      oneshot reconnect/disconnect（`SPEC.md` §4.4）
- [ ] 按 `PKEY_Device_ContainerId` **分组**，对同组每个 KS filter 都发
- [ ] 发送后**轮询端点状态到 `ACTIVE`**（有超时），失败按上限重试
- [ ] UI：显示「已连接 / 已配对未连接」，提供 [连接] 按钮
- [ ] 重连后标记该设备 `measurementIsStale = true`

**验收**：对一个已配对未连接的音箱点 [连接] → **不需要打开 Windows 设置**就出声；
点 [断开] → 断开。

**注意**：
- **成功 ≠ 连上**（文档明确：「成功只表示驱动**尝试**连接」）→ 必须轮询状态。
- **先做真机验证**（PITFALLS D2）：`KSPROPERTY_ONESHOT_RECONNECT` 对**耳机**和 **A2DP-only 音箱**
  是否都生效。**若不生效，功能降级为提示用户去 Windows 设置，不要硬撑。**
- ❌ 不要试 `PairAsync` / `AudioPlaybackConnection` / 裸 L2CAP（PITFALLS C2）。

---

## M9 — 托盘与单实例

- [ ] `Tray/TrayHost.cs`（`H.NotifyIcon.Wpf`）：场景切换 + 设备勾选 + 暂停 + 打开主窗口 + 退出
- [ ] `Services/SingleInstance.cs`：第二实例唤起已有实例主窗口
- [ ] 关闭主窗口 = 最小化到托盘（可配置）
- [ ] 开机自启选项（注册表 Run 或启动文件夹）

**验收**：托盘可完成**全部**日常操作，日常**不需要打开主窗口**（`SPEC.md` G6）。

---

## M10 — 诊断面板

**目标**：两个核心问题都可观测。

- [ ] 每设备：`fill min/max/mean`、`correction ppm`、`starved reads`、`overflows`、`resync 次数`
- [ ] 每设备：`AverageLatency`、`CurrentLatency`、`BufferSize`、`GetBufferSizeLimits`、`GetDevicePeriod`
- [ ] `Sync/ClockDriftEstimator.cs`：裸 `AudioClockClient.GetPosition` 回归 → ppm 上报
      （**仅诊断，绝不进控制回路**）
- [ ] 归因诊断：区分「设备快」和「写者慢」（`SPEC.md` §6.6）

**验收**：诊断面板能看出漂移补偿在做什么，且能定位一次偶发爆音是饿死还是溢出。

**注意**：没有这个里程碑，M5 的问题**无法调试**——NAudio 默认隐藏饿死和溢出（PITFALLS A8）。

---

## M11 — 延迟诚实 UI（Tier 1 收尾）

- [ ] 各传输方式的预期区间展示（有线 5–40 / HDMI 10–120 / 蓝牙 150–400 ms）
- [ ] 每设备显示测量离散度（`±N ms`）
- [ ] **系统总延迟显示** + 「音乐模式 / 视频模式」切换（`SPEC.md` §6.7、`profiles[].mode`）
- [ ] `engineLatencyMs` preset 选择（15/30/60/100，蓝牙默认 100）

**验收**：用户能看懂「已同步」与「能看视频」可能互斥，并能一键选择。

---

## M12 — 声学自动对齐（Tier 2，可推后）

**目标**：自动测出各设备延迟。

- [ ] `Calibration` 模块：生成指数扫频（200Hz–8kHz / 250ms / 48kHz / −12dBFS / 10ms 淡入出）
- [ ] 麦克风捕获（`WasapiRecorder`，capture 端点）
- [ ] 匹配滤波 + 包络 + **前沿自适应阈值** + 亚样本抛物线插值（`SPEC.md` §5.4.2）
- [ ] 质量门限（PSR ≥12dB、峰值优势 ≥4×、按传输方式的合理性窗口、≥3/≤10 接受）
- [ ] 统计：MAD 剔除 → **中位数** + `P90−P10` 离散度
- [ ] **一次只测一个设备，其余端点级静音**；扫频**注入正常管线**（不是单独的 player）
- [ ] 向导 UI（4 步）；**任何设备未通过门限 → 拒绝自动应用**

**验收**：对 2 个真实设备测出的值，与手动调出来的值**相差 <10ms**；失败时**明确报失败**，不回退到 0。

**注意**：
- **中位数，不要 P95**（PITFALLS B4）。
- **前沿阈值，不要 `argmax`，不要 GCC-PHAT**（PITFALLS B5）。
- **保留空气传播项**（同房间时它是正确的，PITFALLS B6）。
- 这是**辅助**不是主机制（`SPEC.md` §5.4.4）。**推后它不影响 v0.1 可用性。**

---

## 建议的实现顺序（依赖关系）

```text
M0 ─► M1 ─► M2 ─► M3 ─┬─► M4 ─► M5 ──┐
                       │              ├─► M9 ─► M11
                       └─► M6 ─► M7 ──┤
                                  └─► M8
                             M10 ──────┘（可与 M5 并行）
                             M12（Tier 2，最后）
```

**如果时间有限，砍 M12 和 M8，不要砍 M5。**
理由：不补偿漂移的话，应用在 5 分钟内正确、之后永远错误（`SPEC.md` §6.1）。
