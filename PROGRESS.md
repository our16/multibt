# MultiBT 开发进度

> 最后更新：2026-09-12 17:26
> 本轮：**输入侧抽象** —— 引擎不再认识任何虚拟声卡品牌（ADR-003）；
> 抽象过程中暴露出两个真实缺陷（开关设备静默失效、失败启动泄漏端点），已一并修复。

---

## 🎯 音量语义重做：滑杆 = 设备实际音量

### 问题

原来的滑杆是**在设备端点音量之上再乘一个系数**，导致两个后果：

1. **上限被设备音量卡死**：设备端点是 61% 时，滑杆拉到 100% 也只能得到 61% 的响度——
   **"100%" 不代表任何东西**。
2. **初始化时显示的不是真实音量**：滑杆读的是我们存的系数，不是设备当前实际音量，
   所以同一个 80% 在两台设备上响度完全不同，却没地方解释。

### 修复

**滑杆现在直接控制设备的 Windows 端点音量**：

- **初始化时读取真实值**（`RefreshEndpointVolumes()` → `EndpointVolumeReader.TryRead`），
  所以滑杆显示的就是设备当前实际音量（你机器上实测：Realtek **61%**、蓝牙 **100%**）；
- **100% 就是 100%**——设备响度的完整范围都在滑杆上；
- 链路增益（chain gain）**固定为 1.0**，不再叠加，避免又出现"两个系数"的困惑；
  它现在只用于**整体静音**（托盘的暂停）。
- 拖动时**150ms 合并写入**（一次拖动会触发几百次变更，每次都是 COM 调用）；
  写入后**回读并用实际值刷新界面**（端点可能量化）。

**「自动对齐响度」也随之改为直接统一设备音量**：取所有已启用设备中**最小**的音量，
把其余设备降到同一水平（**只降不升**——全部升到最响那台会让整套跟最响的一样吵，与"对齐"相反）。

> 取舍说明：这确实会改动设备在其他应用中的音量（`docs/PITFALLS.md` A6 的原始警告）。
> 但对这个应用来说，用户要的就是"控制这台设备的响度"，而且**只有这样 100% 才有意义**。
> 另外，**场景（profile）不再保存音量**——音量是机器设置而不是场景设置，
> 恢复场景不应该悄悄改掉系统音量。

---

## 🎯 启动淡入：不再"吓一跳"

**问题**：开始同步时所有设备**立刻**以设定音量出声，多个蓝牙音箱同时爆出来很吓人。

**修复**：`OutputChannel.Start()` 现在**从 0 增益开始**，在 **2 秒**内平滑升到设定值
（`EngineTunables.StartupRampMs`）。要点：

- 分步约 25ms，听感平滑，又不至于被 `Task.Delay` 的调度开销主导；
- 淡入期间拖动滑杆**不会跳变**——新值被记为**目标**，淡入会落在新目标上；
- 停止/重同步会**中止淡入**（`_rampAborted`），避免和淡出争抢增益；
- 运行时**新增设备**也走同一条路径，所以中途启用一台音箱同样不会突然炸响。

**实测**（自检工具）：

```text
start-up ramp: gain at Start = 0.000012 (target 0.001) -> now 0.001
OK: ramped up from silence to the configured level.
```

---

## 本轮验证

| 项 | 结果 |
|---|---|
| 构建 | ✅ 0 警告 0 错误 |
| 单元测试 | ✅ 78 通过 / 0 失败 |
| **`MirrorSelfTest`** | ✅ **exit 0** |
| ↳ 启动淡入 | ✅ `gain at Start = 0.000012 → now 0.001` |
| ↳ 设备音量读写 | ✅ `read 61% / 100%, write-back ok=True, read-back ok=True` |
| ↳ 音频通路 | ✅ `signal -3 dBFS`、`fill 95–109 ms`、`starved=0`、端点未静音 |
| **`publish/preview`** | ✅ **已重新发布**：191 MB，`16:26`，校验标记全部命中 |

### ⚠️ 关于发布脚本的一个副作用

`publish-preview.ps1` 会**先停掉正在运行的 `MultiBT.App`**（否则输出文件被占用，发布失败且报错难懂）。
本轮它停掉了 1 个正在运行的实例——如果那正是你在用的，抱歉，重新打开即可。
如果不想被停，可以先手动退出应用再跑脚本。

---


---

## 🔴 本轮最先修的：`publish/preview` 是旧的

**症状**：用户双击 `F:\github\multibt\publish\preview\MultiBT.App.exe`，
**界面里连「主设备」字样都没有**。

**根因**（这条是我的疏漏）：我一直在往 `bin\Debug` 构建并验证，
**但从没重新发布过 preview**。那个 exe 的时间戳是 `15:52`，早于本轮所有修复。
`bin\Debug` 里的可执行文件**不是**用户双击的那一个。

更根本的问题是：**「重新发布」这个动作没有一条可重复的命令**，所以它会被忘记。

**修复**：新增 **`publish-preview.ps1`**，一条命令完成发布，并且**自带过期检测**：

```powershell
pwsh -File publish-preview.ps1
```

它会：
1. 先停掉正在运行的 `MultiBT.App`（否则输出文件被占用，报错很难懂）；
2. `Release` + `win-x64` + **自包含单文件**发布（与原产物一致，191 MB，双击即用，不依赖机器上装没装 .NET）；
3. 重建 `publish/MultiBT-Preview-win-x64.zip`；
4. **校验产物确实包含本次代码**——在 exe 里查找本轮新增的字符串字面量，
   找不到就**直接报错**，而不是产出一个静默过期的包。

> **校验必须同时查两种编码**：C# 字符串字面量以 **UTF-16** 进元数据，
> 而 XAML 里的字面量（如按钮文字「自动对齐响度」）编译进 **BAML，是 UTF-8**。
> 只查 UTF-16 会对「仅存在于 UI 里的字符串」误报 MISSING——这个假阴性我第一次就撞上了。

**实测输出**：

```text
Verifying the published binary is current...
  found (metadata+BAML): 端点音量
  found (metadata+BAML): 主设备
  found (BAML): 自动对齐响度
== Done ==
  exe : F:\github\multibt\publish\preview\MultiBT.App.exe
  size: 191 MB
  zip : publish/MultiBT-Preview-win-x64.zip (75.29 MB)
```

并已**启动发布后的 exe 实测**：主窗口 `MultiBT` 可见、托盘窗口存在、进程正常响应。

---


---

## 🔴 每设备音量：滑杆此前**从未生效**

**根因**：`DeviceViewModel.Gain` 只更新了 view model，
**没有任何地方把新增益推送到正在运行的音频通道**。
`MainViewModel.ApplyGainsToEngine()` 存在，但**从来没有被调用**——
从用户角度看，「拖动滑杆没有任何反应」和「功能没做」完全一样。

**修复**：`RefreshDevices()` 里为每个设备订阅 `PropertyChanged`，
当 `Gain` 变化时把值推给对应的 `OutputChannel.SetGain()`；暂停状态下保持静音，
所以拖动滑杆不会意外取消暂停。

**并且加了可验证性**：`OutputChannel.CurrentGain` 直接回读信号链里那个
`VolumeSampleProvider.Volume`。自检工具现在会实际设置 0.5 并回读校验：

```text
volume control: set 0.5 -> True, clamp(5.0) -> True
```

**如果只改 view model，这一项会直接失败**——这正是它此前能悄悄存在的原因。

---

## 音量模型：三个因子，只有一个是可测的

「为什么两个设备同样增益却音量不同」这个问题，需要先把三个因子拆开：

| 因子 | 是否可测 | 处理方式 |
|---|---|---|
| **① 捕获信号已包含源设备音量** | — | **自动跟随，无需实现** |
| **② 各输出设备自己的 Windows 端点音量** | ✅ 可读 | **可自动对齐**（`自动对齐响度`） |
| **③ 各音箱/功放本身的灵敏度** | ❌ 无 API | **只能靠耳朵**，所以滑杆保留最终决定权 |

### ① 系统音量本来就是自动跟随的

**WASAPI loopback 捕获的是源设备端点音量作用「之后」的信号。**
（这也是 SoundSync 要「把端点音量除掉」的原因：它们实测 `10^(dB/20)` 能精确预测捕获电平。）
所以**调节 Windows 音量会同时改变所有镜像输出**——「跟随主设备音量」这件事
**不需要任何额外模式，它本来就是免费的**。这一点之前没写清楚，容易让人以为要单独实现。

### ② 端点音量差异 = 可自动对齐的部分

实测本机：**Realtek Digital Output = 61%，蓝牙 MI Portable Speaker = 100%**。
同样增益下两者必然响度不同。

新增 **「自动对齐响度」** 按钮：读出每个启用设备的端点音量，
令 `输出增益 × 端点音量` 全部相等（以**最小的**那个端点音量为基准）：

```
gain_i = min(端点音量) / 端点音量_i        // 只衰减，不放大
```

**为什么不放大**：增益 > 100% 会在瞬态上削波，而设备偏小的**正确修法是把该设备
自己的端点音量拉满**——所以 UI 新增了「设备音量设为 100%」按钮，
并且每行都会显示该设备的端点音量（含「Windows 端点已静音」提示）。
把别的设备一起衰减来迁就一个被调小的设备，是错的方向。

### ③ 灵敏度差异 = 必须手动

API 不暴露音箱灵敏度，无法自动。所以自动对齐只是**起点**，
滑杆是**最终权威**——这与用户的原话一致：「有的设备可能音量大小就是不一样，
最后还是需要手动调整」。

---

## 本轮验证

| 项 | 结果 |
|---|---|
| 构建 | ✅ 0 警告 0 错误 |
| 单元测试 | ✅ 78 通过 / 0 失败 |
| **`MirrorSelfTest`** | ✅ **exit 0**；两端点 `volume control: set 0.5 -> True, clamp(5.0) -> True` |
| 端点音量实测 | Realtek 61% / 蓝牙 100%，均 `muted=False` |
| GUI | ✅ 正常启动，托盘创建 |

---


---

## 🎯 本轮根因：不是引擎问题，是 **Windows 端点被静音了**

我上一轮的自检工具**用静音做输入**，所以它只能证明「数据在流动」，
**无法区分「链路正常」和「链路忠实地搬运零」**——这是我上一轮的验证盲区。

本轮把它改成喂入一个**真实但完全听不见的信号**（−66 dBFS 的 440 Hz 正弦），
并新增 `SignalProbe` 在**链路增益之前**测量实际信号电平。结果一次就定位了：

```text
耳机 (MI Portable Speaker) [Bluetooth]
   signal -2 dBFS            ← 我们的音频完全正常（−2 dBFS 是用户当时正在播放的音频）
   fill 95.4 ms, starved=0    ← 缓冲健康
   endpoint volume=100%  muted=True    ← ⚠️ Windows 端点本身被静音
```

**引擎送过去的是完美的音频，Windows 把它全部丢掉了。**
这和我们的链路增益（`VolumeSampleProvider`）完全是两件事：
端点静音是 Windows 层面的状态，任何写代码的方式都绕不过它。

### 为什么这个坑特别难发现

从外部看，这**和引擎坏掉完全一样**：音箱不出声、没有报错、没有异常、
缓冲健康、player 状态正常。所以之前每一轮都在改音频代码，而问题根本不在那里。

### 修复

1. **`SignalProbe`**（新增）：在链路增益**之前**测量峰值/RMS/静音块比例。
   *必须在增益之前*——增益允许为 0（静音设备是正常状态，也是自检静音运行的方式），
   在增益之后测量会把所有「故意静音」的通道都报成静音，反而掩盖了要诊断的东西。
2. **`ChannelDiagnostics`** 新增 `SignalPeak` / `SignalDbFs` / `EndpointVolumeScalar` /
   `EndpointMuted` / `BlockedAtEndpoint`。
   现在「不响」可以被明确区分为三种情况：
   - `signal SILENT` → 引擎在送静音（捕获/转换问题）
   - `signal -2 dBFS` + `⚠ ENDPOINT MUTED` → **引擎正常，Windows 静音了** → 去取消静音
   - 两者都正常但没声音 → 设备/驱动层面的问题
3. **启动时自动取消端点静音**（`OutputChannel`），并在状态里说明。
   理由：用户**明确启用**了这个设备作为输出，静音状态与这个意图直接矛盾；
   但**不动音量**——音量是「大小偏好」，不是矛盾。
4. 自检工具现在**会在端点被静音时报 FAIL 并说明原因**，不会再假装通过。

**实测：修复后同一个蓝牙设备 `muted=False`，自检 exit 0。**

---

## 2. 主设备标记刷新后消失

**根因**：`RefreshDevices()` 会 `Devices.Clear()` 并**重建全部 `DeviceViewModel`**，
于是之前那个主设备实例变成陈旧的、它的 `IsPrimary` 也随之丢失；
而旧代码的判断是「新的列表里还有没有这个 endpoint id」——**仍然有**，
所以它**不会**把 `IsPrimary` 重新应用回新实例上，标记就此消失。

**修复**：
- 重建前先记住**端点 id**（不是 view model 实例），重建后按 id **重新绑定**；
- 回退顺序：上次选择 → 持久化设置 `Engine.SourceDeviceId` → Windows 默认设备；
- 选择主设备时**写入设置**，所以刷新和重启都能保住。

## 3. 切换主设备时同步切换 Windows 默认输出设备

**根因**：之前切换主设备只改 MultiBT 内部状态，Windows 默认输出设备没变。
而**镜像捕获的是「源端点正在播放的声音」，Windows 只会把系统声音送到默认端点**——
所以选了非默认设备作主设备 → **捕获到静音 → 所有输出都没声音**。
这就是第 1 个问题的另一种成因，两者都会表现成「不响」。

**修复**：新增 `Core/Devices/DefaultEndpointSwitcher.cs`，
使用 **`IPolicyConfig::SetDefaultEndpoint`**（Windows 声音控制面板与所有「切换默认设备」工具
用的同一个**未公开** COM 接口，Win7→11 稳定，无需提权、无需打包）。
切换主设备时**同时把三个角色**（Console / Multimedia / Communications）都切过去，
与 Windows 自己的行为一致。

> ⚠️ 该接口**未公开**，成员顺序即 vtable 顺序，**不可改动**（代码里已明确标注）。
> 调用全程防御式处理：失败只影响便利性，不影响镜像本身。

**实测**（`tools/SmokeCheck` 新增第 [7] 项，把当前默认设备设为它自己，**不改动机器状态**）：

```text
[7] Default endpoint switching (IPolicyConfig)
    OK: SetDefaultEndpoint accepted the call (state unchanged - it was already default).
```

## 4. 主设备标记改为文字「主设备」

原来用 `RadioButton`，渲染成一个圆点。已改为**文字按钮**：
主设备那行显示「**主设备**」，其他行显示「**设为主设备**」。
文字在截图和远程会话里都不会有歧义。

---

## 验证状态（本轮）

| 项 | 结果 |
|---|---|
| 构建 | ✅ 0 警告 0 错误 |
| 单元测试 | ✅ 78 通过 / 0 失败 |
| `SmokeCheck`（含 IPolicyConfig 检查） | ✅ exit 0 |
| **`MirrorSelfTest`（真实信号 + 端点检查）** | ✅ **exit 0**，两端点 `muted=False`、`signal -2 dBFS`、`fill 95.4 ms`、`starved=0` |
| GUI | ✅ 主窗口 `MultiBT` 可见，**托盘窗口 `H.NotifyIcon_…` 存在**（托盘确实创建了） |

### 请这样验证你的两个设备

1. 确认两个蓝牙音箱都已连接；
2. 跑自检：`dotnet run --project tools/MirrorSelfTest/MirrorSelfTest.csproj`
   —— **exit 0 表示整条链路把真实音频送到了两个 player，且两个端点都没有被静音**；
3. 打开应用，把主设备设为你实际在听的那台设备（会自动同步切换 Windows 默认设备）；
4. 若某个设备仍无声，看诊断面板那一行，现在能直接区分：
   - `signal SILENT` → 引擎在送静音
   - `⚠ ENDPOINT MUTED` / `endpoint volume 0%` → Windows 端点问题
   - 两者都正常 → 设备/驱动层面（例如单个蓝牙适配器撑不住两路流，见 `docs/PITFALLS.md` D9）

---


---

## 🔴 「其他设备不响」的真正根因（本轮修复）

上一轮我修了 `WdlResamplingSampleProvider`（那是个真缺陷，见下方「缺陷 1」），
**但它不是根因**。本轮我写了 `tools/MirrorSelfTest`——一个**完全静音**的端到端自检
（向默认设备播放数字静音以驱动 loopback，输出通道增益设为 0），
它直接暴露了真相：

**修复前：**

```text
{...}: -200 ppm, fill 0.5–0.5 ms, 48000 Hz, ⚠ 50% of a read was silence, ⚠ correction saturated
```

**`fill 0.5 ms`** —— 环形缓冲里几乎没有数据。这才是「不响」的直接原因。

### 机制：为什么 fill 会停在 0

三个事实叠加，每一个单独看都合理：

1. **没有任何地方预填充环形缓冲。** 引擎用一个**空** ring 启动 player。
2. **捕获与播放都按实时速率跑。** 捕获以实时速率生产，播放以设备时钟消费，
   两者速率相同（差几 ppm）→ **fill 停在初始值 = 0，永远不动。**
3. **控制器无力创建缓冲。** 它的权限是 **±200 ppm = 0.02%**。
   这足以**微调**一个已建立起来的缓冲，但要**创造** 105 ms 的缓冲，
   需要饱和修正约 **500 秒**。所以它只会一直卡在限幅值上。

而 `ReadFully = true` 让这件事**完全静默**：ring 空时 `Read` 不是返回短读，
而是**补零**并照常报告「读取成功」。于是每次读取都返回「0.5 ms 真实音频 + 99.5% 静音」，
播放出来等于没声音——**没有异常、没有短读、饿死计数器也几乎为 0**。

这也解释了为什么我之前加的 `starved` 计数（只统计「读前完全为空」）报的是 3 而不是几百：
绝大多数读取开始时 ring 里**有**那 0.5 ms，所以不算「饿死」。

### 修复

**① 预填充环形缓冲**（`OutputChannel` 构造函数）：
`prefill = (engineLatencyMs + 5ms) + engineLatencyMs + 25ms`。

两个加项都有必要：
- `+ engineLatencyMs`：**player 第一次拉取会把整个 WASAPI 缓冲一次填满**。
  实测 `requested=100ms → granted=100ms`，所以首次拉取会直接从 ring 拿走约 100 ms。
  如果只预填充到 `f*`（105 ms），首次拉取后只剩 ~5 ms，而后续每次读取又会把可用的全部取走 →
  **ring 再也无法自行恢复到目标深度**。
- `+ 25 ms` 余量：首次拉取的实际大小无法预先得知（实测在 95–119 ms 之间浮动）。
  预填充到**略高于**目标，控制器就会**向下收敛**——这是安全方向：
  缓冲多出来的是抗抖动余量，而缓冲不足会让修正饱和、彻底没有余量。

**② 把「重同步」从「清空」改成「裁剪超出部分」**（这是本轮最关键的修复之一）：
原来的实现调用 `_ring.ClearBuffer()`，而 `ClearBuffer()` 会让 ring **变空**，
并且**没有任何机制能把它填回来**（见上面的事实 2 和 3）。
于是**一次重同步就会让该通道永久静音**。

更糟的是它**是间歇性的**：`ShouldResync` 的阈值是 `engineLatencyMs + 25ms = 125ms`，
而预填充是 230 ms —— **我自己的预填充就超过了阈值**。
是否触发取决于「player 的首次拉取」和「第一个控制 tick」之间的竞争：

```
run 1: 通过      run 2: 通过      run 3: 蓝牙通道 fill 0–0 ms，100% 静音，resync 1 → 失败
```

这就是用户说的「**还是不响**」——它不是每次都失败。

现在改为**只裁掉超出目标的那部分**（用临时缓冲读掉多余字节），
既保住了通道不被静音，又顺带让收敛从 0.02%/s 变成**立即完成**。

### 修复后的实测（连续 4 次运行，全部通过）

```text
run 1 (exit 0): fill 94.9 ms /  95.4 ms
run 2 (exit 0): fill 95.4 ms /  95.4 ms
run 3 (exit 0): fill 119.4 ms / 95.4 ms
run 4 (exit 0): fill 119.5 ms / 95.4 ms
```

**fill 稳定在 95–119 ms，不再有静音警告，不再有饿死。** 两个通道（包括 48k→44.1k 的蓝牙）
都持有真实缓冲深度。

### 顺带修好的诊断

新增 `PartialStarvedReads` 与 `WorstSilenceFraction`——**这才是能检测「静音通道」的指标**。
只统计「读前完全为空」是不够的，而那正是我上一轮的盲点：

```text
⚠ 50% of a read was silence     ← 修复前，一眼就能看出问题
```

---

## 2. 主设备默认标记（本轮修复）

**问题**：没有任何地方设置初始主设备，所以单选按钮**一个都没打勾**。

**修复**：`MainViewModel.RefreshDevices()` 现在会默认选中
**Windows 默认输出设备**（找不到就退化为第一个活动设备）。

**为什么必须是默认设备**：镜像捕获的是「**源端点正在播放的声音**」，
而系统声音只会送到 Windows 默认设备。选别的设备做源 → **捕获到的是静音** →
所有输出设备都没声音，而且**不报任何错**。

**并且加了红色警告横幅**（`CaptureSourceWarning`）：当所选主设备 ≠ Windows 默认设备时，
界面会明确说明「其他设备会完全没有声音」以及怎么改。
这正是此类问题本该被立刻看见的地方——而不是让用户对着没声音的音箱猜。

---

## 上一轮修复的缺陷清单（仍然有效，保留记录）

按影响面从大到小排列。每条都注明了「为什么之前看起来是好的」——
这批缺陷的共同特征是**不崩溃、不报错**，只是功能不存在。

### 🔴 缺陷 1：`WdlResamplingSampleProvider` 导致蓝牙设备永久静音

**症状**：`PROGRESS.md` 记录的「其他蓝牙设备不响」。

**根因**：`OutputChannel.cs` 曾这样插入固定采样率转换：

```csharp
if (samples.WaveFormat.SampleRate != DeviceMixFormat.SampleRate)
    samples = new WdlResamplingSampleProvider(samples, DeviceMixFormat.SampleRate);
```

`WdlResamplingSampleProvider` 在 **NAudio 3.1.0** 上有一个缺陷（上游 issue #1412，**只在未发布版本中修复**）：
当它的数据源供给不足时，它会丢样本并**最终永久返回 0**。
而它的数据源在这里正是 `BufferedWaveProvider` 支撑的捕获链——**恰好是触发该缺陷的经典模式**
（见 `docs/PITFALLS.md` A2）。

**为什么偏偏是蓝牙设备**：这段转换**只在采样率不同的时候才插入**。
蓝牙音箱的 mix format 常常是 **44.1 kHz**，而捕获设备（Realtek/HDMI）跑 **48 kHz** →
蓝牙走「需要转换」的分支 → 命中缺陷 → **该通道永久静音**。
有线设备通常同为 48 kHz → 不插入转换 → 一切正常。
于是就表现成「只有（一部分）蓝牙设备不响」，看起来像设备或连接问题。

**修复**：删除这段固定转换，**由 `AdaptiveResampler` 独自完成全部转换**。
它本来就是按「输入采样率 = 源采样率、输出采样率 = 设备 mix 格式」构造的，
所以 `44100↔48000` 和叠加在上面的 ±200ppm 漂移微调是同一套机制。
它的滤波配置（`interp, filtercnt: 2, sinc: false`）**与 NAudio 自带的
`WdlResamplingSampleProvider` 完全相同**，因此转换质量不变——只是绕开了那个缺陷。

**回归测试**（`AudioChainTests.cs`）：新增 4 个测试，覆盖
`44100→48000`、`48000→44100`、立体声双声道、以及「转换之上仍能漂移修正」。
关键点是**连续读 60 个块并要求每一块都有信号**——该缺陷的特征正是
「前几块正常，之后永久静音」，只读一块是抓不到的。

---

### 🔴 缺陷 2：托盘和单实例**从未被实例化**（M9 实际未完成）

**症状**：`PROGRESS.md` 曾把 M9 标为「✅ 完成」，但**运行时根本没有托盘图标**。

**根因**：`TrayHost` 与 `SingleInstance` 两个类写好了，却**没有任何地方构造它们**——
全仓库只有它们自己的声明处引用到自己。`App.xaml` 仍然是 `StartupUri="MainWindow.xaml"`。

**这是本轮影响最大的问题**：`docs/GOALS.md` 的 **G6（托盘可完成全部日常操作，日常不打开主窗口）
完全未达成**，而它是这个产品的核心价值。

**修复**：
- `App.xaml`：移除 `StartupUri`，改为 `ShutdownMode="OnExplicitShutdown"`
  （托盘是主 UI，关掉设置窗口不能杀掉正在镜像的引擎）。
- `App.xaml.cs`：做单实例判定 → 构造 `MainViewModel` / `MainWindow` / `TrayHost` → 接线托盘事件。
- `MainWindow` 改为**注入** `MainViewModel`（托盘也需要它，必须只有一个实例，
  否则会出现两份互相矛盾的「哪些设备已启用」状态）。
- 关闭主窗口 = **隐藏**（不是退出）；只有托盘的 Exit 才真正退出并干净地淡出、join 播放线程。

**验证**：启动两次，第二个实例**自己退出**（exit code 0），存活进程数为 1。

---

### 🔴 缺陷 3：托盘子菜单是空的，且暂停是单向的

`TrayHost` 里的 `_Profiles` / `_Devices` 是**没有任何子项的空 `MenuItem`**，
`SwitchProfileRequested` 事件声明了却**从未 raise**（编译器 CS0067 警告就是在提示这件事）。
`OnPauseClick` **硬编码传 `true`**——一旦从托盘暂停，就再也无法从托盘恢复。

**修复**：`TrayHost` 重写为数据驱动：
- `UpdateProfiles()` 从真实 profile 列表重建子菜单，当前项标 `●`；
- `UpdateDevices()` 重建可勾选设备项，点击 raise `ToggleDeviceRequested`；
- `OnPauseClick` 传**目标状态**，菜单标题在 Pause/Resume 间切换；
- `DelegateCommand.CanExecuteChanged` 改为显式空访问器，消除「从不使用」警告。

`MainViewModel` 新增了支撑这些操作所需的 API：`Profiles` / `ActiveProfileId` /
`ActivateProfileAsync()` / `SaveCurrentStateToActiveProfile()` / `IsPaused` / `SetPaused()` /
`ToggleDeviceAsync()`。

> **暂停的实现选择**：`SetPaused` 用**链增益静音**，而不是停引擎。
> 停引擎会拆掉所有 player 和补偿延迟线，恢复时就得重建整个镜像并重新同步——
> 一个需要重建的暂停开关不叫暂停开关。静音保持捕获、漂移控制器和延迟线继续运行，
> 所以恢复是瞬时的、而且仍然同步。

---

### 🟠 缺陷 4：`_liveChannelDevices` 用了错误的键 → COM 对象泄漏

`_liveChannelDevices` 字典**以 endpoint id 为键**（见 `Start` 与 `AddChannelToEngineAsync`），
但 `RemoveChannelFromEngineAsync` 里却用 **deviceKey** 去查：

```csharp
if (_liveChannelDevices.TryGetValue(deviceKey, out MMDevice? mmDevice))  // 永远查不到
```

结果：**每次移除设备，那个 `MMDevice` 都不会被释放**，并一直留在字典里占着端点，
直到 Stop 才由 `ReleaseLiveDevices` 兜底清理。已改为按 endpoint id 查找。

---

### 🟠 缺陷 5：raw mode 回退路径泄漏一个 player

`InitializePlayer` 在 `Init` 抛 `InvalidOperationException`（raw mode 不被支持）时，
会新建第二个 player 并返回，**但第一个已经构造出来的 player 从未被 Dispose**。
它会带着一个已经打开的 `AudioClient` 留在端点上。

这个路径**恰好是蓝牙端点会走的**——蓝牙端点上常常缺少 raw mode 需要的 `IAudioClient2` 支持。
一个泄漏的、占着端点的 client，正是「设备看着是连着的、却不出声」的典型成因。
已改为在 `catch` 里先 Dispose 掉失败的尝试。

---

### 🟠 缺陷 6：`SingleInstance.IsFirstInstance` 判断是错的

```csharp
public bool IsFirstInstance => _mutex is not null && _mutex.WaitOne(0);   // 旧的
```

第一个实例是用 `new Mutex(true, ...)`（**已持有**）创建的，所以：
1. 从**别的线程**调用 `WaitOne(0)` 会返回 `false` → 把「第一个实例」误判成「不是第一个」；
2. 每次成功的 `WaitOne(0)` 都会**增加递归计数**，导致一次 `ReleaseMutex` 释放不掉。

已改为在构造函数里缓存 `createdNew`，并只在真正持有互斥体时才 `ReleaseMutex`。

---

### 🟠 缺陷 7：`async void` 事件处理器会崩掉进程

`device.IsEnabledChanged += (_, _) => OnDeviceToggled(device);` 中 `OnDeviceToggled` 是 `async void`。
`async void` 里抛出的异常无法被捕获，会直接终止进程——而这个处理器负责**运行时重配音频设备**。
已改为返回 `Task` 并由一个带 try/catch 的包装器调用，失败时写进状态栏而不是崩溃。

---

### 🟡 缺陷 8：诊断在预热期是空白的，且「无数据」被当成「饿死」

- `UpdateDrift()` 在预热期提前 `return`，导致前 3 秒诊断面板完全没有数据。
  「正在预热」和「什么都没产出」恰恰是运维最需要区分的两种状态。已改为**先采样统计再判断**。
- `RingDiagnostics` 在窗口内没有读取时，`FillMinSeconds` 返回 **0**，
  于是 `StarvedInWindow` 误报饿死。已改为返回 **NaN**（表示「无数据」），
  并让 `StarvedInWindow` 要求确实有数据。
- `Reads` 原本是「每窗口重置」，与同样是累计量的 `StarvedReads` 语义不一致，
  会让「完全停止读取的通道」看起来和健康通道一样。已改为累计。
- 缓冲区重同步后 `DriftController.Reset()` 清掉了预热标志但**截止时间留在过去**，
  于是下一 tick 立刻恢复修正一个刚被清空的缓冲。新增 `RestartWarmUp(now)`。

---

### 🟡 缺陷 9：新增格式诊断（回应 PROGRESS 里的调试建议）

原文档建议「在 `OutputChannel` 构造函数里加调试输出，打印 `DeviceMixFormat`」。
与其用 `Console.WriteLine`，不如把它做成**常驻诊断**——这样下次再出现「某个设备不响」时，
用户能从界面上直接看出原因：

`ChannelDiagnostics` 新增 `CaptureSampleRate` / `DeviceSampleRate` /
`CaptureChannels` / `DeviceChannels`，并提供 `RequiresRateConversion` 与
改进后的 `Summarize()`。诊断面板现在会显示例如：

```text
客厅JBL: +42 ppm, fill 95.0–101.0 ms, 48000→44100 Hz
投影   : -0 ppm, fill 60.0–61.0 ms, 48000 Hz
```

**「设备未连接」和「采样率转换坏了」现在是可以区分的**，而不是都表现为「没声音」。

---

## 验证状态

| 项 | 结果 |
|---|---|
| 全解决方案构建 | ✅ 0 警告 0 错误 |
| 单元测试 | ✅ **78 通过 / 0 失败** |
| **端到端静音自检**（`tools/MirrorSelfTest`） | ✅ **连续 4 次全部通过**，fill 稳定 95–119 ms，无静音、无饿死 |
| GUI 启动 | ✅ 主窗口 `MultiBT` 出现 |
| 单实例 | ✅ 第二个实例自行退出 |
| 真机听音（两个蓝牙设备同时出声） | ⏳ **需要你确认**（见下） |

### 新增：端到端静音自检工具

`tools/MirrorSelfTest` 是**本轮最重要的工具**：「其他设备不响」这个问题，
读代码看不出来、在自动化环境里也没法听音验证，但它可以**用数据证明**：

```powershell
dotnet run --project tools/MirrorSelfTest/MirrorSelfTest.csproj
```

它用**生产代码本身**（`AudioSource` + 同步 fan-out + `OutputChannel`）跑完整链路，
并靠两点做到**完全静音**：
- 向源设备播放**数字静音**（不是为了降噪——WASAPI loopback 在设备不渲染时**什么都不投递**，
  必须有东西在渲染才能测试捕获；渲染零样本既能驱动 loopback 又不产生任何信号）；
- 每个输出通道**增益设为 0**。

退出码 0 = 整条链路真的在搬运数据。**这是「有没有声音」唯一可靠的自动化证据。**

### ⚠️ 仍需真机确认

**「其他蓝牙设备不响」的修复无法在本环境中听音验证。**
缺陷 1 的根因、机制与上游 issue 都是确凿的，回归测试也覆盖了它的失败签名，
但**「两个蓝牙设备同时出声」必须在有多个蓝牙设备连接的真机上确认**。

验证步骤：
1. 把**两个**蓝牙音箱都连接好（Windows 设置里确认状态为「已连接」）；
2. 运行 `dotnet run --project tools/SmokeCheck/SmokeCheck.csproj`，
   确认两者的 `transport` 都是 `Bluetooth`、且都显示为 `active`；
3. 启动应用，看诊断面板每一行是否都有 `ppm` 与 `fill` 数值，以及 `48000→44100` 这类标注；
4. 若某个设备仍无声，先看诊断面板：
   - `fill — (no reads)` → 该通道从未被拉取（设备/初始化问题）；
   - `⚠ starved` → 缓冲被饿死（采样率或抖动问题）；
   - 状态列显示 `未连接（已配对）` → 设备其实没连上，不是代码问题。

---

## 🎯 输入侧抽象：引擎不认识「VB-CABLE」这个词

需求原话：**「不要把 MultiBT Engine 写死成 VB-CABLE」**。

### 做了什么

输入侧从「引擎自己去捕获某个端点」改成**引擎接收一个 PCM 来源**：

```csharp
public interface IAudioInputBackend : IDisposable   // 新增
```

| 实现 | 机制 | 状态 |
| --- | --- | --- |
| `NativeLoopbackBackend` | 系统端点 loopback | 已实现 |
| `VirtualCableBackend` | 第三方虚拟声卡 loopback（会校验端点**真的是**声卡） | 已实现 |
| `VirtualAudioDriverBackend` | 自研驱动的 capture 端点 | 占位，大声抛异常 |

品牌只出现在**一个地方**：`MainViewModel.BuildInputBackend`。
`AudioEngine` 的类型、字段、构造参数里没有任何品牌字样——这一条现在由**反射测试**守着。
完整理由见 `docs/DECISIONS.md` **ADR-003**。

### 🔴 抽象过程中暴露的缺陷 1：勾选设备**完全无效**

`AddChannelToEngineAsync` 的守卫是：

```csharp
if (_engine is null || _sourceDevice is null) return;   // 旧
```

`_sourceDevice` 是 ViewModel 自己持有的 `MMDevice`。抽象之后端点归 backend 所有，
这个字段被置空**且再也不会被赋值**——于是守卫**永远为真**，
运行中途勾选任何设备都会静默 return：**没有任何报错，就是没反应**。

这正是上一轮「手动调节延迟没用」同一类的坑：**静默的提前返回**。

修复：守卫改为跟随引擎真实状态——`if (_engine is null || !_engine.IsRunning)`。

### 🔴 抽象过程中暴露的缺陷 2：启动失败会**泄漏捕获端点**

构造 backend 与调用 `engine.Start(backend)` 之间隔着「构建各设备输出链」，
那一步会抛异常（设备被拔掉、端点拒绝格式）。旧代码靠 `DisposeSourceDevice()` 兜底。

问题在于：**引擎无法释放一个从未交给它的 backend**。失败路径上 `_sourceDevice` 已经是 null，
于是捕获端点会一直开到这个进程结束。

修复：新增 `_pendingBackend` 字段显式表达**所有权交接**——

```text
BuildInputBackend ──► _pendingBackend = backend      （此时由 ViewModel 负责释放）
engine.Start(backend) ──► _pendingBackend = null     （此时由引擎负责释放）
```

任何未走到 `Start` 的退出路径都调用 `DisposePendingBackend()`。

顺带修掉 `NativeLoopbackBackend` 构造函数自身的泄漏：`new AudioSource(...)` 抛异常时
端点无人释放，现在 catch 里先 `renderDevice.Dispose()` 再 rethrow。

### 防回归：把约束写成测试

`tests/MultiBT.Core.Tests/AudioInputBackendTests.cs`（新增 8 个测试，**不需要音频硬件**）：

- `AudioEngine.Start` 的参数类型必须是 `IAudioInputBackend`；
- `AudioEngine` 的任何成员名/构造参数名不得包含 `cable` / `vb-audio` / `voicemeeter` / `vac`；
- 该命名空间下不得出现品牌命名的类型；
- 占位实现必须**大声失败**（`Start()` 抛异常并指向 ADR-001），
  且 `Stop()` / `Dispose()` **必须不抛**——拆除路径是无条件调用的。

**负向对照已做过**：故意往 `AudioEngine` 里塞一个 `_vbCableScratch` 字段，
品牌守卫测试立刻失败；移除后恢复通过。**测试确实能失败**，不是摆设。

### 本轮验证

```text
dotnet build MultiBT.slnx   → 已成功生成，0 个警告
dotnet test  MultiBT.slnx   → 通过 118，失败 0   （110 → 118，本轮 +8）
```

---

> 最后更新：2026-09-12 17:40
> 本轮：**虚拟声卡改为自动检测**（用户不再需要手动选择输入）；
> 并查清了一件事：**本机的虚拟声卡在今天 13:43 被卸载了**，所以现在测不了。

---

## 🎯 输入就是一个普通设备选择：**不绑定虚拟声卡**

用户原话：**「简单点，我们不绑定虚拟声卡，用户可以任意选，也可以选实际的主设备」**。

前一轮我把输入做成了「声卡中心」：自动检测虚拟声卡、在列表里给它加星、开始同步时自动把
Windows 默认输出切过去。这一轮把这些**耦合全部拆掉**。

### 拆掉了什么

| 移除 | 原因 |
| --- | --- |
| 虚拟声卡自动选择（`ResolveSourceSink` 及其 10 个测试） | 输入应该是用户选的，不是程序猜的 |
| 选择列表里的「声卡优先排序」和 ★ 标记 | 加星等于告诉用户「这才是正确答案」 |
| 「⚠ 未检测到虚拟声卡」警告横幅 | 没有声卡不是缺陷，是一种正常配置 |
| 开始同步时自动切换 Windows 默认输出 | 程序不该在用户没要求时改动系统音频走向 |
| 工具栏冗余的输入摘要（下拉框已经显示了） | 重复信息 |

### 换成了什么

警告横幅改成**说明性提示**，且只在「解析出来的输入是真实设备」时出现：

> 当前输入是真实设备「X」：它会照常原生播放，所以它自己的延迟和音量不受 MultiBT 控制，
> 也不会收到镜像副本（否则会重复出声）。其他设备正常受控。

这是用户**唯一无法自己推断**的后果——真实设备会从 Windows 直接发声，所以它的延迟滑杆
不动不是 bug，而是这个选择的固有属性。不说清楚，就会被当成故障。

提示解析输入的方式与 `ResolveSourceDevice` 一致（显式选择 → 否则跟随 Windows 默认输出），
因为「自动」是默认状态，而它**经常会落在真实设备上**——那正是最需要这条提示的时候。

### 输入优先级（唯一规则）

```text
1. 用户在下拉框里选的设备（任意端点，声卡或真实设备都行）
2. 用户标记的主设备
3. 配置里的来源设备
4. Windows 默认输出设备
```

### 虚拟声卡只剩三处「知情」，每一处都有理由

1. **提示**：输入是声卡时不显示上面那条提示（声卡不发声，所有设备都可控）；
2. **`SetCaptureSink`**：只有用户**显式选了声卡**时才把 Windows 默认输出切过去
   —— 声卡只转送 Windows 渲染进去的声音，不切就永远没声音，用户的这个选择无法成立；
3. **`BuildInputBackend`**：选中的是声卡时用 `VirtualCableBackend` 校验端点确实是声卡。

除此之外没有任何地方按「是不是声卡」分支，也没有任何地方会自己切换默认输出。

真实设备作为输入**本来就能用**：它会被排除在输出之外（因此不会重复出声），声音按原样捕获。

### 本轮验证

```text
dotnet build MultiBT.slnx   → 0 个警告
dotnet test  MultiBT.slnx   → 通过 121（原 131，去掉 10 个测试自动选择的）
XAML ↔ 本地化交叉检查        → XAML 用到的 27 个 key 在中英文表里都存在
```

> ⚠️ 中途我自己写的补丁脚本有两个 bug 被抓到：`Replace-Once` 用 `Contains('')` 判断
> 「是否已应用」时恒为真，导致两处**删除被静默跳过**；以及 XAML 锚点缩进写错。
> 因为有断言，脚本在写完文件**之前**就抛错了，所以仓库没有被写坏。

---

---

## 🔴 本轮查明：本机的虚拟声卡在今天 13:43 被**卸载**了

用户以为已经装好，实际证据（`C:\Windows\INF\setupapi.dev.log`）：

```text
2025/11/26 22:28:03   Device Install - VBVoicemeeterVAIO   cmd: -h -i -H -n
                      INF: C:\Program Files (x86)\VB\Voicemeeter\vbvoicemeetervaio64_win10.inf
                      → 注册为 ROOT\MEDIA\0000

2026/09/12 13:43:06   Delete Device - ROOT\MEDIA\0000      cmd: -h -u -H
                      → 随后 AudioEndpointBuilder 批量删除全部音频端点
```

也就是说：**本机原本有的是 VoiceMeeter 的 VAIO 虚拟声卡，今天 13:43 被 VB-Audio 的卸载命令删掉了**，
之后没有任何音频驱动被安装。当前实测状态：

```text
Render 端点（20 个，含隐藏）：仅 3 个 ACTIVE，全是真实设备
  ACTIVE  扬声器 (智能音箱 Pro-3420)          ← 蓝牙
  ACTIVE  耳机 (MI Portable Speaker)          ← 蓝牙
  ACTIVE  Realtek Digital Output
Capture 端点：0 个 ACTIVE
名称含 cable / line N / virtual / voicemeeter 的端点：0 个
DriverStore 中最新音频驱动包：2025/11/26（VoiceMeeter VAIO，包还在，但设备实例已删除）
```

所以**代码已就绪，但当前无法端到端验证**。要恢复验证，需要用户重新安装一个虚拟声卡
（VB-CABLE / VoiceMeeter / Virtual Audio Cable 任一，我们的检测都认），然后：
开始同步时 MultiBT 会自动把 Windows 默认输出切到它并开始捕获——不需要手动选。

**待验证**：`NVVoiceMeeterVAIOMME` 服务仍在、驱动包仍在，
因此理论上可以只**重建设备实例**而不重装整套 VoiceMeeter；这一步需要管理员权限。

---

## ✅ 已实测：loopback 捕获是 **PRE-volume**（端点音量不影响我们捕获到什么）

装好 VB-CABLE 后用 `tools/LoopbackVolumeProbe` 对着 **CABLE Input** 实测
（对虚拟声卡测量意味着**完全没有声音**，只有在 cable 上才能这么做）：

```text
volume 100%            wrote  100% -> read  100% muted=False peak    -34.0 dBFS
volume  50%            wrote   50% -> read   50% muted=False peak    -34.0 dBFS
volume  25%            wrote   25% -> read   25% muted=False peak    -34.0 dBFS
volume   0%            wrote    0% -> read    0% muted=False peak    -34.0 dBFS
volume 100% again      wrote  100% -> read  100% muted=False peak    -34.0 dBFS
muted (volume 100%)    wrote  100% -> read  100% muted=True  peak    -34.0 dBFS
```

每一次都**先读回确认写入生效**，并且用 **peak 电平**（不是 RMS、不是静音比例）比较，
0% 前后各测一次 100% 作为对照。结论明确：

**峰值在所有音量下完全相同，连 mute 都照收 -34 dBFS → 捕获发生在端点音量之前。**

三个直接推论：

1. **好消息：不会因为声卡被静音或音量拉低就饿死整条镜像。** 之前担心过的
   「cable 被 mute → 所有输出一起没声音」不成立；
2. **好消息：被捕获的真实设备可以同时被我们控制。** 我们写它的端点音量不会反过来改变捕获内容，
   所以它既能原生播放（音量受我们控制）又能作为输入；
3. ⚠️ **代价：在声卡模式下，键盘音量键会失效。** 因为 Windows 默认输出是 cable，
   音量键调的是 **cable 的**音量，而捕获是 pre-volume —— 所以按了没反应。

> 💡 第 3 条有一个顺理成章的修法（**尚未实现，也未要求**）：读取捕获端点的音量，
> 把它当作一路 master gain 施加到所有输出上。这样音量键就重新生效了，
> 因为音量键动的正是这个值。等需要时再讨论。

### 顺带修掉工具自身两个 bug（都是「看起来在工作但测不到东西」那类）

- **`--device` 参数被 `--nologo` 挤掉**：`dotnet run` 把无法识别的 `--nologo` 当应用参数传了下去，
  而我原来只检查 `args[0]`。结果它在**默认设备上**跑，还打印了默认设备——
  如果没有把收到的参数打出来，这个错误会一直被当成「测的就是 cable」。现在参数先解析并打印；
- **`Extensible` 格式被当成非浮点**：捕获格式是 `WAVE_FORMAT_EXTENSIBLE` 包着 32-bit float
  （显示为 `Extensible` 而不是 `IeeeFloat`），原来的判断会让 `PeakAccumulator` 两个分支都不进，
  **永远报 -inf dBFS（静音）**。这种「工具永远说没声音」的失败模式比崩溃更危险。
  同时 NAudio 的 `SampleToWaveProvider` 拒绝 Extensible，tone 改为按引擎的做法
  生成普通 `IeeeFloat`（引擎自己就是这么做的）。

---

## 里程碑

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 骨架与可编译基线 | ✅ 完成 |
| M1 | 设备枚举与传输分类 | ✅ 完成（含真机验证与缺陷修复） |
| M2 | 单设备镜像 | ✅ 代码就位 · ⏳ 待真机听音确认 |
| M3 | 多设备 fan-out + 每设备音量 | ✅ 代码就位 · ⏳ 待真机听音确认 |
| M4 | 延迟线与延迟模型 | ✅ 完成 |
| M5 | 漂移补偿 | ✅ 闭环仿真验证 |
| M6 | Profile 持久化 | ✅ 完成（本轮补上激活与保存 API） |
| M7 | 设备通知与自动恢复 | ✅ 完成 |
| M8 | 蓝牙连接管理 | ✅ COM interop 已实现 · ⏳ 待真机确认 |
| M9 | 托盘与单实例 | ✅ **本轮真正接线完成**（此前为死代码） |
| M10 | 诊断面板 | ✅ 完成（本轮补上格式诊断） |
| M11 | 延迟诚实 UI | ✅ 完成 |
| M12 | 声学自动对齐 | ⏳ 信号与门限已实现；端到端待验证 |
| M13 | 输入侧后端抽象（引擎不绑定声卡品牌） | ✅ 完成（ADR-003，含反射防回归测试） |

---

## 下一步

1. **真机验证双蓝牙出声**（最高优先级，见上方步骤）；
2. **M8 蓝牙连接的真机确认**：`KSPROPERTY_ONESHOT_RECONNECT` 对
   **A2DP-only 音箱**是否生效（`docs/PITFALLS.md` D2）——
   若不生效，UI 应降级为「请到 Windows 设置里连接」，而不是静默失败；
3. **M12 声学对齐端到端**：需要麦克风，`docs/SPEC.md` §5.4 的算法与门限已实现；
4. **确认「单个蓝牙适配器能否同时支撑两路流」**（`docs/PITFALLS.md` D9）——
   这是已知的物理限制，若撑不住，需要在文档里明确告知用户加一个 USB 蓝牙适配器。

---

## 关键代码位置

| 用途 | 位置 |
|---|---|
| 音频链构造（**采样率转换在此**） | `src/MultiBT.Core/Audio/OutputChannel.cs` 构造函数 |
| **输入侧契约（引擎只认这一层）** | `src/MultiBT.Core/Audio/IAudioInputBackend.cs` |
| **三种输入后端实现** | `src/MultiBT.Core/Audio/AudioInputBackends.cs` |
| **唯一选择后端的地方（品牌只在此处）** | `src/MultiBT.App/ViewModels/MainViewModel.cs` → `BuildInputBackend` |
| 漂移控制律 | `src/MultiBT.Core/Sync/DriftController.cs` |
| 可调速重采样（漂移执行机构） | `src/MultiBT.Core/Audio/AdaptiveResampler.cs` |
| 格式/缓冲诊断 | `src/MultiBT.Core/Audio/RingDiagnostics.cs` |
| 设备枚举与身份读取 | `src/MultiBT.Core/Devices/DeviceManager.cs`、`EndpointIdentityReader.cs` |
| 托盘菜单 | `src/MultiBT.App/Tray/TrayHost.cs` |
| 应用接线（单实例 + 托盘） | `src/MultiBT.App/App.xaml.cs` |
| 设备启停/Profile/暂停 | `src/MultiBT.App/ViewModels/MainViewModel.cs` |

---

## 测试命令

```powershell
# 构建（解决方案是 .slnx 格式）
dotnet build MultiBT.slnx

# 单元测试
dotnet test MultiBT.slnx

# 真机设备检查（不开流、不发声）
dotnet run --project tools/SmokeCheck/SmokeCheck.csproj

# 端到端静音自检（证明音频真的流到了每个 player）
dotnet run --project tools/MirrorSelfTest/MirrorSelfTest.csproj

# 运行 GUI
dotnet run --project src/MultiBT.App/MultiBT.App.csproj

# 重新发布 preview —— 改完代码要双击的那份，必须跑这个
# （会自动停掉运行中的实例、发布自包含单文件、重建 zip，并校验产物不是旧的）
pwsh -File publish-preview.ps1
```

> ⚠️ **`bin\Debug` 里的 exe 不是用户双击的那一个。**
> 改了 UI 或行为之后，只有跑过 `publish-preview.ps1`，`publish/preview` 才会更新。
> 这一点此前没有可重复的命令，导致发布的 exe 静默过期（UI 里看不到新功能）。
