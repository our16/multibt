# 决策记录（ADR）

## ADR-001：不自研虚拟音频驱动，改用第三方虚拟声卡

**日期**：2026-09-12
**状态**：已采纳
**相关提交**：`9782609`（曾嵌入驱动 fork）、`d3de9d3`（撤销）

---

### 背景

Windows 把系统声音**直接渲染**到默认输出端点。WASAPI loopback 只是从那条通路旁路复制一份，
所以**默认设备始终会自己发声**——0 延迟、音量也不受我们控制。

这就是"主设备无法延迟"的根本原因：它不是被我们跳过，而是它走的是**一条不经过本程序的通路**。

要让每个音箱都可控，必须让 Windows 把声音渲染到一个**听不见的地方**。
两条路：

| 方案 | 内容 |
|---|---|
| **A. 自研驱动** | fork SysVAD 衍生项目（如 Virtual Audio Driver），裁剪成 `MultiBT Virtual Speaker` |
| **B. 第三方虚拟声卡** | 用 VB-CABLE / VoiceMeeter / Virtual Audio Cable 现成的虚拟端点 |

### 决策

**采用 B。**

### 理由（按权重排序）

**1. 编译只是便宜的那一半，真正的门槛是签名。**

在用户机器上安装内核驱动，只能二选一：

- **测试签名**：`bcdedit /set testsigning on` + 重启 + 桌面水印 —— **不可能交付给用户**；
- **认证签名（Attestation）**：需要 **EV 代码签名证书 + Partner Center 账号**，且是持续成本。

这是一项**与产品价值无关**的固定负担。而"安装时一起装驱动、卸载时一起卸"这个要求，
恰恰只能靠认证签名实现。

**2. 这个问题已经被免费解决了。**

VB-CABLE、VoiceMeeter、Virtual Audio Cable 都提供我们需要的那个虚拟 render 端点，
而应用**已经能识别并使用它们**（`VirtualCableDetector` + 「捕获出口」选择器）。

**3. 自研驱动不会带来任何新能力。**

MultiBT 的全部价值都在用户态：fan-out、每设备延迟、漂移补偿、Profile、设备恢复。
这些在**别人的虚拟声卡之上**运行时行为**完全一样**。

因此自研驱动的净收益是：**零功能增量，换一份签名成本 + 一份内核维护面**。

### 后果

**正面**
- 没有内核驱动 → 没有蓝屏风险、没有签名成本、没有 WHK 认证流程；
- 没有第三方依赖之外的授权归属负担（移除 fork 后连 MIT/SysVAD 归属也不必再维护）；
- 用户可以今天就跑通完整架构（装个免费虚拟声卡即可）。

**负面 / 需要接受的**
- 用户**必须自己装一次虚拟声卡**（一次性，免费）；
- 我们要**持续跟进**第三方声卡的命名变化（`VirtualCableDetector` 靠名称匹配，
  而没有任何 API 会告诉你"这个端点是虚拟的"）；
- 系统音量的行为取决于第三方声卡的实现（见下方"待验证"）。

### 待验证（**不要当成已知事实**）

**系统音量滑块是否仍然有效？**
这取决于 loopback 捕获是发生在端点音量**之前**还是**之后**：

- 若**之前**：系统音量会失效（音量作用在我们的捕获之后），需要由我们统一控音量；
- 若**之后**：系统音量正常传导到所有输出。

我曾试图测出结论，但**实验本身有两处缺陷**，所以**没有结论**：

1. 写入被夹到 **3.9%** 而非 0（未真正归零）；
2. 检测器只判断"样本非零"（阈值 1e-7），**分不出"满电平"和"衰减 50 dB"**。

要做对，必须**比较峰值电平**（而不是布尔值），并**回读确认写入生效**。
在那之前，任何关于"音量是否跟随"的说法都只是猜测。

### 若将来重新考虑

fork 仍可从提交 `9782609` 恢复：

```powershell
git checkout 9782609 -- driver
```

但请先回答 ADR 里那条签名问题：**谁来出 EV 证书，以及由谁承担驱动随 Windows 更新失效的维护责任。**

---

## ADR-002：驱动不参与同步，同步逻辑留在用户态

**状态**：已采纳（即使将来自研驱动也适用）

**驱动只做三件事**：注册一个 render 端点、稳定接收 PCM、把 PCM 交给用户态。
**不得包含**：JSON、HTTP、蓝牙、GUI、Profile、设备发现、延迟计算、同步、重采样、漂移控制。

理由：这些逻辑在内核里出错是 **bugcheck**，在用户态出错只是**崩溃**。
而且它们全部依赖于应用侧已经写好、已有 110 个测试覆盖的代码。

**推论**：驱动**不应调用 `MultiBT.exe`**。它照普通音频设备的方式接收音频，
用户态用**现有的 WASAPI loopback 路径**捕获——也就是应用**现在已经能做的事**。
因此引入虚拟声卡**不需要任何新的 IPC**，应用侧代码**一行都不用改**。

### 与用户态引擎的边界

```text
Windows Audio Engine ──► 虚拟声卡（第三方）──► PCM
                                                │ WASAPI loopback（已有实现）
                                                ▼
                                    MultiBT Engine（用户态，已实现）
                                    ring buffer / resampler / delay / drift
                                                │
                              ┌─────────────────┼─────────────────┐
                              ▼                 ▼                 ▼
                             JBL               Sony             XIGMI
```

采样率与声道差异统一在**用户态**处理（`AdaptiveResampler` 已实现），
不要指望驱动去做格式转换。

---

## ADR-003：多设备输出引擎不绑定任何虚拟声卡品牌

**状态**：已采纳

### 背景

需求原话：**「不要把 MultiBT Engine 写死成 VB-CABLE」**。

这很容易在不经意间违反。VB-CABLE 是当前唯一装得上的方案，所以「引擎从 CABLE 取音频」这种写法
最省事，也最容易被写进字段类型、构造参数、选择逻辑里。一旦写进去，VB-CABLE 就从**一个可替换的
依赖**变成**产品的隐含前提**——换 VoiceMeeter、换 Virtual Audio Cable、或者将来换成自研驱动，
都要动引擎本体。

### 决策

输入侧抽象为一个接口：`IAudioInputBackend`。

```csharp
public interface IAudioInputBackend : IDisposable
{
    string Description { get; }
    AudioInputKind Kind { get; }
    WaveFormat Format { get; }
    event AudioDataAvailableHandler? DataAvailable;
    event EventHandler<Exception?>? Stopped;
    void Start();
    void Stop();
}
```

三个实现，对应三种**机制**而不是三个设置项：

| 实现 | 机制 | 状态 |
| --- | --- | --- |
| `NativeLoopbackBackend` | 系统端点的 loopback 捕获 | 已实现 |
| `VirtualCableBackend` | 第三方虚拟声卡 render 端点的 loopback 捕获 | 已实现 |
| `VirtualAudioDriverBackend` | 自研驱动暴露的 **capture** 端点 | 占位，抛 `NotSupportedException` |

### 接口为什么是「给我 PCM」而不是「做一次 loopback」

命名上刻意不叫 `ILoopbackCapture`。前两者在 WASAPI 下机制相同（都是对某个 render 端点做 loopback），
**区别只在于捕获哪个端点、以及为什么那个端点是对的**；而自研驱动走的是**普通 capture 流**，
是真正不同的机制。把接口定义成「给我 PCM」，第三种实现才不需要改动接口，也不需要改动引擎。

### 品牌认识在哪里

只在一个地方：`MainViewModel.BuildInputBackend`。

```text
VirtualCableDetector 判断「这个端点是不是虚拟声卡」──► 选 Backend
                                                        │
AudioEngine 只看到 IAudioInputBackend ◄─────────────────┘
```

- 判断「是不是虚拟声卡」属于 `VirtualCableDetector`（已识别 VB-CABLE / VoiceMeeter / VAC）；
- `AudioEngine` 的类型、字段、构造参数里**没有任何品牌字样**；
- `VirtualCableBackend` 会**拒绝**非虚拟声卡端点。把真扬声器当声卡捕获，会导致该扬声器
  「系统原生播放 + 我们的延迟副本」双重发声，这种故障宁可**启动前报错**，不要事后排查。

### 所有权契约（容易写错的地方）

`IAudioInputBackend` **拥有**它内部的端点，`Dispose()` 负责释放。所有权在 **`AudioEngine.Start()`
真正被调用时才转移**。

这不是形式主义。构造 backend 与启动引擎之间隔着「构建各设备输出链」，那一步会抛异常
（设备被拔掉、端点拒绝某个格式）。引擎**无法释放一个从未交给它的 backend**，
所以失败的启动路径必须由调用方释放——否则捕获端点会一直开到这个进程结束。
`MainViewModel._pendingBackend` 就是为此存在的。

### 防回归

`tests/MultiBT.Core.Tests/AudioInputBackendTests.cs` 用反射把这条约束固化下来，不需要音频硬件：

- `AudioEngine.Start` 的参数类型必须是 `IAudioInputBackend`；
- `AudioEngine` 的任何成员名、构造参数名**不得**包含 cable / vb-audio / voicemeeter / vac；
- 该命名空间下不得存在品牌命名的类型；
- 占位实现必须**大声失败**（`Start()` 抛异常并指向 ADR-001），
  且 `Stop()` / `Dispose()` **必须不抛**——拆除路径是无条件调用的，
  在那里抛异常会把真正的错误替换成拆除错误。

### 后果

- 换声卡、换驱动 = **换一个 backend 实现**，引擎与 UI 不动；
- 引擎可以脱离虚拟声卡运行（`NativeLoopbackBackend`，能同步大部分设备）；
- 代价是多了一层间接，以及一次所有权交接需要维护。

### 待验证（**不要当成已知事实**）

`VirtualAudioDriverBackend` 只是**形状**，不是实现。将来若真做驱动，需要确认：
capture 端点的格式协商是否与 loopback 一致、是否需要额外的时钟对齐、以及
Windows 是否会把这个 capture 端点计入默认设备切换的影响范围。
