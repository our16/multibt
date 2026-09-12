# MultiBT — 开发与运行指南

> 这是开发文档：构建、测试、项目结构、易错点与致谢。
> 面向用户的介绍在 [README.md](../README.md)（英文）与 [README.zh-CN.md](../README.zh-CN.md)（中文）。

> **Windows 的「多输出设备 + 自动同步」按钮。**
> 不做音频工作站，不做 Voicemeeter，不做驱动。

把系统声音同时送到多个输出设备（蓝牙音箱 / USB / HDMI / 投影），每个设备独立音量、独立延迟补偿，
并**在播放过程中持续纠正时钟漂移**。一次配置，之后开机自动恢复——**日常不用打开主窗口**。

```text
                Windows Audio
                     │
                     ▼
              ┌─────────────┐
              │   MultiBT   │   WASAPI Loopback（一次捕获）
              └──────┬──────┘
                     │  同一份 PCM，fan-out 到 N 条独立链路
          ┌──────────┼──────────┐
          ▼          ▼          ▼
       Bluetooth   Bluetooth    USB/HDMI
        客厅音箱    桌面音箱     投影
```

**状态：可编译、可测试、可运行的骨架。** 音频通路尚未在真机验证（见下方「进度」）。

---

## 文档

| 文档 | 内容 |
|---|---|
| **[`PROGRESS.md`](PROGRESS.md)** | **当前进度与待修复问题**——本轮修复的缺陷清单、验证状态、真机验证步骤。先看这个。 |
| **[`docs/GOALS.md`](docs/GOALS.md)** | **项目目标**——要解决什么、8 条可验收目标、非目标、三条不可妥协的设计原则、成功判据 |
| **[`docs/SPEC.md`](docs/SPEC.md)** | **v0.1 技术规格（权威依据）**——架构、NAudio/WASAPI 数据流、延迟模型与声学测量、同步与漂移控制律、配置 JSON、UI、异常恢复 |
| **[`docs/PITFALLS.md`](docs/PITFALLS.md)** | **已核实的坑（实现前必读）**——NAudio 3.x 陷阱、会静默毁掉同步的设计错误、蓝牙能做与不能做 |
| **[`docs/TASKS.md`](docs/TASKS.md)** | 实现任务拆分（M0–M12），每个里程碑都有验收标准 |

---

## 快速开始

```powershell
# 0) 若 restore 报 NU1301 / SSL 错误，见 docs/PITFALLS.md §E1（本机代理问题）
#    $env:HTTPS_PROXY = 'http://127.0.0.1:7890'

# 1) 构建（解决方案是 .slnx 格式）
dotnet build MultiBT.slnx

# 2) 单元测试（74 个，含漂移控制律的闭环仿真）
dotnet test MultiBT.slnx

# 3) 真机冒烟检查 —— 不开流、不发声，安全
dotnet run --project tools/SmokeCheck/SmokeCheck.csproj

# 4) 运行 GUI
dotnet run --project src/MultiBT.App/MultiBT.App.csproj

# 5) 端到端静音自检（证明真实音频流到了每个输出 player，且端点未被静音）
dotnet run --project tools/MirrorSelfTest/MirrorSelfTest.csproj

# 6) 重新发布 preview（改完代码要双击的那份，必须跑这个）
pwsh -File publish-preview.ps1

# 7) 编译期 API 契约检查（NAudio 换版本时保证不静默失效）
dotnet build tools/ApiProbe/ApiProbe.csproj
```

> ⚠️ **`bin\Debug` 里的 exe 不是 `publish/preview` 里那个。**
> 改完代码要更新用户可以双击的预览版，必须跑 `publish-preview.ps1`——
> 它会自动停掉运行中的实例、发布自包含单文件、重建 zip，
> **并校验产物确实包含本次代码**（只查 exe 的字符串字面量，
> 因为曾经发生过发布的 exe 静默过期、UI 里看不到新功能）。

**任何新机器上都先跑第 3 步。** 它会打印每个端点的真实 `instanceId`、
混音格式、引擎周期，以及身份读取的原始属性表——这些值**因环境而异**，
`docs/PITFALLS.md §C6` 记录的缺陷就是靠它发现的。

---

## 项目结构

```text
MultiBT.slnx
Directory.Build.props              # 统一 x64 / Nullable / LangVersion（TFM 刻意留在各 csproj）
src/
  MultiBT.Core/                    # 无 UI 依赖，可单元测试
    Audio/
      AudioSource.cs               # WasapiRecorder 封装：loopback 捕获 + 字节分发
      AudioEngine.cs               # 编排：一次捕获 → N 条链 + 5 Hz 控制回路
      OutputChannel.cs             # 一条链的全部所有权与生命周期
      AdaptiveResampler.cs         # 包装 NAudio.Dsp.WdlResampler（唯一可运行时调速的方案）
      DelaySampleProvider.cs       # 整数样本补偿延迟线（独立于漂移缓冲）
      RingDiagnostics.cs           # NAudio 默认隐藏的两种失败：饿死 / 溢出
      EngineTunables.cs            # 所有调参常数集中于此
    Devices/
      DeviceManager.cs             # 枚举 + 通知订阅（NAudio 3.x 事件 API）
      EndpointIdentityReader.cs    # 设备身份读取与规范化（含 PKEY 回退链）
      TransportClassifier.cs       # 蓝牙 / USB / HDMI 分类
      DeviceIdentityMatcher.cs     # 三级身份匹配（endpointId → instanceId → name）
      RecoveryPolicy.cs            # 有界重试 + 蓝牙连接后的稳定等待
      BluetoothConnector.cs        # M8 待实现（契约与调研结论已写入注释）
    Sync/
      DriftController.cs           # 谷底 P 控制器（纯逻辑，可闭环仿真）
      LatencyModel.cs              # 三段延迟模型的唯一计算入口
    Config/                        # profiles.json 原子读写 + 损坏隔离
    Calibration/SweepGenerator.cs  # 指数扫频测试信号
  MultiBT.App/                     # WPF
    ViewModels/                    # MainViewModel / DeviceViewModel
    MainWindow.xaml                # 设备列表 + 系统延迟警示 + 诊断面板
tests/MultiBT.Core.Tests/          # 74 个测试
tools/
  ApiProbe/                        # 编译期 API 契约检查（仅编译，不运行）
  SmokeCheck/                      # 真机冒烟检查（不开流、不发声）
```

---

## 三个最容易做错的地方

实现或阅读代码时请特别注意（详情见 `docs/PITFALLS.md`）：

1. **🔴 补偿延迟和漂移缓冲必须是两个独立缓冲。**
   如果把补偿实现成 ring buffer 的读指针偏移，控制器会忠实地把 fill 稳定在 5 ms，
   而你那 170 ms 的补偿会**悄悄消失**。

2. **🔴 漂移补偿是 Tier 1，不是「第二阶段」。**
   ±50 ppm = 每小时 180 ms，±100 ppm = 每小时 360 ms；5 分钟就到 30 ms（已明显可闻）。
   不补偿的话，应用在 5 分钟内正确、**之后就永远错误**。
   反过来，**声学自动测量可以推后**。

3. **🔴 静默失效比崩溃更危险。** 本项目已经抓到两个：
   - TFM 少写 `-windows` → 整个 WASAPI 栈消失，**无报错无警告**；
   - 直接读 `PKEY_Device_InstanceId` 判定蓝牙 → 该属性在 Win11 24H2 上对所有端点缺失
     → **所有设备判定为 `Other`，蓝牙功能静默全灭**。

   → 身份/分类逻辑必须有回退链，并且**在目标机器上打日志核对真实值**。

---

## 已核实的两个平台事实（本机实测）

| 事实 | 证据 |
|---|---|
| **`DeviceState.All` 是必须的**：已配对未连接的蓝牙端点是 `Unplugged` | 实测 20 个 render 端点，其中 `Active` 只有 2 个 |
| **`PKEY_Device_InstanceId` 不可用**，实例 id 在 `{b3f8fa53-…} pid=2` 且带 `{N}.` 前缀、`#` 分隔符 | 见 `docs/PITFALLS.md §C6`；修复后智能音箱与 MI Portable Speaker 均正确判定为 `Bluetooth` |

---

## 进度

| 里程碑 | 内容 | 状态 |
|---|---|---|
| M0 | 骨架与可编译基线 | ✅ 完成 |
| M1 | 设备枚举与传输分类 | ✅ 完成（含真机验证与缺陷修复） |
| M2 | 单设备镜像 | ✅ 代码就位 · ⏳ 待真机听音确认 |
| M3 | 多设备 fan-out + 每设备音量 | ✅ 代码就位 · ⏳ 待真机听音确认 |
| M4 | 延迟线与延迟模型 | ✅ 算法已实现并测试 |
| M5 | **漂移补偿** | ✅ 控制律闭环验证通过 |
| M6 | Profile 持久化 | ✅ 完成（含激活/保存 API） |
| M7 | 设备通知与自动恢复 | ✅ 完成 |
| M8 | 蓝牙连接管理 | ✅ COM interop 已实现 · ⏳ 待真机确认 |
| M9 | 托盘与单实例 | ✅ **已接线**（此前只是死代码，托盘图标根本不出现） |
| M10 | 诊断面板 | ✅ 完成（含采样率转换诊断） |
| M11 | 延迟诚实 UI | ✅ 已实现 |
| M12 | 声学自动对齐 | ⏳ 信号与门限已实现；端到端待验证 |

---

## 可借鉴的上游参考

| 项目 | 借什么 |
|---|---|
| [AudioHQ](https://github.com/underfusion/AudioHQ) (MIT) | **漂移补偿的谷底-P 控制律及全部常数**、引擎骨架。它**没有**每设备延迟层，本项目是它的真超集。 |
| [SoundSync](https://github.com/sugumar247/SoundSync) (MIT) | 每设备手动延迟元的形状（但需修掉它两个 bug） |
| [double-headphones](https://github.com/maayaranai/double-headphones) | 0–2000 ms 范围 + 数字输入框 + 对「蓝牙延迟是物理定律」的诚实措辞。**Proprietary 许可且未发布源码，不可借用代码。** |
| [Max-Paire](https://github.com/maheshmaximusmax/Max-Paire) | 仓库里**只有 README，没有源码**，自述里 per-device delay 也列为「未实现」。**参考价值仅限营销措辞。** |

---

## 许可

**MIT** —— 见 [LICENSE](LICENSE)。

第三方依赖同样是 MIT，没有 GPL 传染性依赖：

| 依赖 | 许可 | 用途 |
| --- | --- | --- |
| [NAudio](https://github.com/naudio/NAudio) 3.1.0 | MIT | WASAPI 采集与播放 |
| [H.NotifyIcon.Wpf](https://github.com/HavenDV/H.NotifyIcon) 2.4.1 | MIT | 系统托盘图标 |

**MultiBT 不附带任何虚拟声卡驱动。** 程序只把你指向厂商官网（见
`src/MultiBT.App/Recommendations/VirtualAudioRecommendations.cs`），安装与否、以什么条款安装，
都发生在你和厂商之间 —— VB-Audio 的许可本身也不允许转发它的安装包。详见
[docs/DECISIONS.md](docs/DECISIONS.md) 的 ADR-001 与 ADR-003。

> ⚠️ 本项目**没有**借用下方致谢列表中那些项目的代码：它们只是设计思路的来源。
> 特别是 `double-headphones`（Proprietary，未发布源码）**只参考了产品行为，没有参考其代码**。
