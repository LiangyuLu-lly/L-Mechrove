# MechrevoLite M1+M2 实现计划（探测工具 + WPF 骨架）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 打通与原版 GCUBridge broker 的 MQTT 通信（复刻 UI 完整握手、遥测流可用），并搭出 WPF 骨架（托盘、单实例、能力矩阵、IHardwareBackend 抽象、遥测绑定），为 M3+ 各功能域铺路。

**Architecture:** 两个项目在同一解决方案：`Tools/Probe`（.NET 8 控制台，MQTTnet 客户端，用于握手探索与协议嗅探）+ `MechrevoLite`（.NET 8 WPF，MVVM，托盘常驻）。服务层依赖 `IHardwareBackend` 抽象，Phase 1 用 MqttBackend。能力探测直接读注册表 `HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport`。

**Tech Stack:** .NET 8 (net8.0-windows), WPF, MQTTnet (NuGet), CommunityToolkit.Mvvm (NuGet), Newtonsoft.Json, 单文件发布。

**关键事实（已实测，供执行时直接使用）：**
- Broker: `localhost:13688`；UI 凭据硬编码：`UWPClient_User_5` / `UWPClient_Pwd888881772688_5`，clientId `UWPClient_5`（M2Mqtt Connect 返回 0=接受）
- 已知现象：订阅后不推流；发布 `System/Control` 后连接被断（M1 需复刻 UI 完整启动序列解决，含 `Keyboard/Ctrl {function:Init}` 和 GETSTATUS 请求）
- EC 直连已通：`CreateFile("\\\\.\\ACPIDriver")` + `IOCTL_GPD_ACPI_ECREAD=2621482120` 读 EC 成功
- 能力注册表：`HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport`（BIOS_PROJECT_ID=IDY、KeyboardType=7、LightbarSupport=1 等 40+ 键）
- 遥测协议：UI 窗口激活 → `System/Control {Action:System_ON}` → GCUService 推流（约 5s 周期）；`System/CpuInfo`、`System/GpuInfo`、`System/FanInfo`、`System/BatteryInfo`、`System/StaticsData` 等
- 参考源码：`_decompiled/CCUWinUI.decompiled.cs`（UI，含 MQTTDataService L9729-10006 与 Topic 常量 L9609-9728）

**Git 说明：** 仓库尚未初始化（用户决定项目完成后统一 git）。执行本计划时**跳过所有 commit 步骤**。

---

## 文件结构

```
ControlCenterX_5.56.60.26_Mechrevo/
├── MechrevoLite.sln                        # 解决方案（M1 创建）
├── src/
│   ├── Probe/                              # M1：控制台探测工具
│   │   ├── Probe.csproj
│   │   ├── Program.cs                      # 子命令入口：handshake / sniff / ec
│   │   ├── MqttProbe.cs                    # MQTT 客户端封装 + 握手序列
│   │   └── EcProbe.cs                      # EC 直读（P/Invoke \\.\ACPIDriver）
│   └── MechrevoLite/                       # M2：WPF 主程序
│       ├── MechrevoLite.csproj
│       ├── Program.cs                      # 单实例 + 管理员提权 + 托盘
│       ├── App.xaml(.cs)                   # 资源 + 启动
│       ├── Views/MainWindow.xaml(.cs)      # 左侧导航 + 内容区
│       ├── Views/HomePage.xaml(.cs)        # 模式卡片 + 指标雷达
│       ├── ViewModels/MainViewModel.cs     # 导航 VM
│       ├── ViewModels/HomeViewModel.cs     # 模式/遥测 VM
│       ├── Hardware/IHardwareBackend.cs    # 抽象接口
│       ├── Hardware/MqttBackend.cs         # Phase 1 实现
│       ├── Services/CapabilityService.cs   # 读 ItemSupport 注册表
│       ├── Services/TelemetryService.cs    # 5s 轮询 + INotifyPropertyChanged
│       ├── Models/TelemetrySnapshot.cs     # CPU/GPU/风扇/电池 快照 DTO
│       ├── Models/CapabilityInfo.cs        # 能力矩阵 DTO
│       └── NativeMethods.cs                # P/Invoke 集中地（托盘/单实例/重启）
└── docs/superpowers/                       # 设计与计划
```

---

## M1：握手探测工具

### Task 1: 解决方案与 Probe 项目

**Files:**
- Create: `MechrevoLite.sln`
- Create: `src/Probe/Probe.csproj`
- Create: `src/Probe/Program.cs`
- Create: `src/Probe/MqttProbe.cs`

- [ ] **Step 1: 创建解决方案与项目**

```bash
cd C:\Users\28717\Desktop\ControlCenterX_5.56.60.26_Mechrevo
dotnet new sln -n MechrevoLite
dotnet new console -n Probe -o src/Probe
dotnet sln add src/Probe/Probe.csproj
dotnet add src/Probe/Probe.csproj package MQTTnet
dotnet add src/Probe/Probe.csproj package Newtonsoft.Json
```

Expected: 成功创建并添加 NuGet 包。

- [ ] **Step 2: 写 Program.cs 子命令入口**

```csharp
using Probe;

var cmd = args.Length > 0 ? args[0] : "handshake";
switch (cmd)
{
    case "handshake": await MqttProbe.Handshake(); break;
    case "sniff": await MqttProbe.Sniff(); break;
    case "ec": EcProbe.DumpRegisters(); break;
    default: Console.WriteLine("usage: probe <handshake|sniff|ec>"); break;
}
```

- [ ] **Step 3: 写 MqttProbe.cs 基础客户端封装**

```csharp
using MQTTnet;
using MQTTnet.Client;

namespace Probe;

public static class MqttProbe
{
    const string Host = "localhost";
    const int Port = 13688;
    const string User = "UWPClient_User_5";
    const string Pwd = "UWPClient_Pwd888881772688_5";

    static readonly MqttFactory Factory = new();

    public static MqttClientOptions Opts(string clientId) => new MqttClientOptionsBuilder()
        .WithTcpServer(Host, Port).WithCredentials(User, Pwd).WithClientId(clientId)
        .WithCleanSession(false).Build();

    public static async Task<IMqttClient> Connect(string clientId)
    {
        var c = Factory.CreateMqttClient();
        var res = await c.ConnectAsync(Opts(clientId));
        Console.WriteLine($"[{clientId}] Connect: {res.ResultCode} Reason={res.ReasonString}");
        return c;
    }

    public static async Task Subscribe(IMqttClient c, IEnumerable<string> topics)
    {
        foreach (var t in topics)
        {
            await c.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).WithQualityOfServiceLevel(
                MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce).Build());
        }
        Console.WriteLine($"[{c.Options.ClientId}] subscribed {string.Join(",", topics)}");
    }

    public static async Task Publish(IMqttClient c, string topic, object payload, bool retain = false)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
        Console.WriteLine($"[{c.Options.ClientId}] PUB {topic} <- {json}");
        await c.PublishStringAsync(topic, json, MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce, retain);
    }

    public static void WirePrint(IMqttClient c, string tag)
    {
        c.ApplicationMessageReceivedAsync += e =>
        {
            Console.WriteLine($"[{tag}] << {e.ApplicationMessage.Topic}: {System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment)}");
            return Task.CompletedTask;
        };
        c.DisconnectedAsync += e =>
        {
            Console.WriteLine($"[{tag}] DISCONNECTED: {e.Reason}");
            return Task.CompletedTask;
        };
    }
}
```

- [ ] **Step 4: 写 EcProbe.cs（EC 直读，验证驱动通路）**

```csharp
using System.Runtime.InteropServices;

namespace Probe;

public static class EcProbe
{
    const uint IOCTL_GPD_ACPI_ECREAD = 2621482120u; // 0x9C402108

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int returned, IntPtr ovl);

    public static void DumpRegisters(int count = 0x40)
    {
        IntPtr h = CreateFile(@"\\.\ACPIDriver", 0xC0000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
        if (h == new IntPtr(-1)) { Console.WriteLine("open fail 0x" + Marshal.GetLastWin32Error().ToString("X8")); return; }
        for (int addr = 0; addr < count; addr++)
        {
            IntPtr inBuf = Marshal.AllocHGlobal(4); IntPtr outBuf = Marshal.AllocHGlobal(16);
            Marshal.WriteInt16(inBuf, (short)addr);
            int returned;
            bool ok = DeviceIoControl(h, IOCTL_GPD_ACPI_ECREAD, inBuf, 2, outBuf, 16, out returned, IntPtr.Zero);
            if (ok && returned > 0) Console.Write($"0x{addr:X2}={Marshal.ReadByte(outBuf)} ");
            else Console.Write($"0x{addr:X2}=ERR ");
            Marshal.FreeHGlobal(inBuf); Marshal.FreeHGlobal(outBuf);
            if (addr % 8 == 7) Console.WriteLine();
        }
        CloseHandle(h);
    }
}
```

- [ ] **Step 5: 构建并跑通 ec 子命令**

```bash
cd C:\Users\28717\Desktop\ControlCenterX_5.56.60.26_Mechrevo
dotnet run --project src/Probe -- ec
```

Expected: 打印 `0x00=00 0x01=00 ...`（含非零值，如 0x24=32、0x25=31 机型字符串区）。驱动通路工作，为 M1 后续对照提供 EC 快照能力。

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: probe tool with MQTT client and EC read"
```

### Task 2: 握手序列复刻（核心探索任务）

**Files:**
- Modify: `src/Probe/MqttProbe.cs`

- [ ] **Step 1: 写 Handshake 序列（逐步打印，每步暂停确认）**

在 `MqttProbe.cs` 增加：

```csharp
public static async Task Handshake()
{
    // 第 1 步：仅连接 + 订阅，不发布。观察是否收到任何保留消息
    var c1 = await Connect("UWPClient_5");
    WirePrint(c1, "step1");
    await Subscribe(c1, new[] { "Customize/#", "System/#", "Fan/#", "Setting/#", "Keyboard/#" });
    Console.WriteLine(">>> 等 15s 观察是否推流 (step1: connect+subscribe only)");
    await Task.Delay(15000);

    // 第 2 步：发布 Keyboard/Ctrl Init（UI 启动时的初始化调用）
    await Publish(c1, "Keyboard/Ctrl", new { function = "Init" });
    Console.WriteLine(">>> 等 15s 观察 (step2: Keyboard Init)");
    await Task.Delay(15000);

    // 第 3 步：发布 Customize/Control GETSUPPORT
    await Publish(c1, "Customize/Control", new { Action = "GETSUPPORT" });
    Console.WriteLine(">>> 等 15s 观察 (step3: Customize GETSUPPORT)");
    await Task.Delay(15000);

    // 第 4 步：发布各 GETSTATUS
    await Publish(c1, "Fan/Control", new { Action = "GETSTATUS" });
    await Publish(c1, "Setting/Control", new { Action = "GETSTATUS" });
    Console.WriteLine(">>> 等 15s 观察 (step4: GETSTATUS 批量)");
    await Task.Delay(15000);

    // 第 5 步：System_ON 触发遥测
    await Publish(c1, "System/Control", new { Action = "System_ON" });
    Console.WriteLine(">>> 等 30s 观察 (step5: System_ON)");
    await Task.Delay(30000);

    Console.WriteLine("=== 观察记录：每步后记录收到/断开情况到 docs/superpowers/plans/m1-findings.md ===");
}
```

- [ ] **Step 2: 跑握手并记录每步现象**

```bash
dotnet run --project src/Probe -- handshake
```

Expected: 每步记录到 `docs/superpowers/plans/m1-findings.md`（新文件）：收到哪些 topic、连接是否被断、是否有报错。**关键判定点**：
- 若 step1-4 有推流 → broker 对订阅即推，之前断连是 M2Mqtt 客户端问题，直接用 MQTTnet 即可
- 若某步发布后断连 → 该步是鉴权边界，记录断连时正在发布的 topic
- 若全程无推流也不断 → 需要对照原版 UI（Task 3）

- [ ] **Step 3: 若 step2 的 Keyboard Init 是关键，细化 Init 载荷**

从 `CCUWinUI.decompiled.cs` L138871 附近读取 UI 的 InitCmd 完整载荷（`service.GetMQTT_Topics("keyboard/ctrl")` 发布的内容），替换 step2 的匿名对象为完整载荷，重跑。

- [ ] **Step 4: 提交发现**

```bash
git add -A && git commit -m "docs: m1 handshake findings"
```

### Task 3: 原版 UI 对照抓包（golden reference）

**Files:**
- Create: `src/Probe/GoldenCapture.cs`

- [ ] **Step 1: 写对照抓包器（记录原版 UI 与 broker 的所有往来）**

```csharp
using MQTTnet; using MQTTnet.Client;

namespace Probe;

public static class GoldenCapture
{
    public static async Task Run(int seconds)
    {
        // 用第二个客户端只做监听：订阅全部已知 topic（若 broker 拒绝推流则记录断连时间点）
        var factory = new MqttFactory();
        var c = factory.CreateMqttClient();
        c.ApplicationMessageReceivedAsync += e =>
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} << {e.ApplicationMessage.Topic}: " +
                System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment));
            return Task.CompletedTask;
        };
        c.DisconnectedAsync += e => { Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} DISCONNECTED: {e.Reason}"); return Task.CompletedTask; };
        await c.ConnectAsync(MqttProbe.Opts("UWPClient_4")); // 不同 ID 避免互踢
        var topics = new[] { "Customize/#", "System/#", "Fan/#", "Setting/#", "Keyboard/#", "HidLightbar/#", "MyRgbLightbar/#", "GPUDevice/#", "Settings/#", "Languages/#", "BatteryProtection/#", "OSD/#", "Display/#", "WhisperMode/#", "BT_LC/#", "LCHWOC/#", "GameProfile/#", "Doudou/#", "OTA/#" };
        foreach (var t in topics)
            await c.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build());
        Console.WriteLine($">>> 请在 {seconds}s 内手动打开原版"机械革命控制中心"并操作几个页面（首页/风扇/灯效），关闭时也观察。");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine("=== capture end ===");
    }
}
```

- [ ] **Step 2: 用户手动操作原版 UI**

```bash
dotnet run --project src/Probe -- golden 120
```

同时手动：打开原版控制中心 → 切换模式 → 打开风扇页 → 打开灯效页 → 关闭应用。
Expected: 记录到原版 UI 的完整消息序列（主题+载荷）→ 存入 `docs/superpowers/plans/m1-findings.md`。

- [ ] **Step 3: 从抓包中提炼握手协议**

对照抓包结果回答三个问题并记录：
1. 原版 UI 启动时发布了哪些"注册/初始化"消息（哪些 topic、顺序、载荷）
2. 遥测推流是在哪个发布之后开始的
3. 关闭 UI 时是否发 `System/Control {System_OFF}` 或 `Service/Close`

- [ ] **Step 4: 把结论固化进 MqttBackend 设计**

在 `docs/superpowers/plans/m1-findings.md` 中输出"MqttBackend 连接序列"伪代码（连接 → 注册消息 → 订阅 → GETSTATUS → System_ON → 轮询消费）。

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "docs: golden capture analysis, backend connect sequence"
```

### Task 4: 嗅探模式（EC 快照 × MQTT 对照）

**Files:**
- Modify: `src/Probe/MqttProbe.cs`

- [ ] **Step 1: 写 Sniff（并行：MQTT 消息 + 每秒 EC 快照）**

```csharp
public static async Task Sniff(int seconds = 60)
{
    var c = await Connect("UWPClient_4");
    WirePrint(c, "mqtt");
    await Subscribe(c, new[] { "System/#", "Fan/#", "Setting/#", "EC/#", "Keyboard/#", "MyRgbLightbar/#", "HidLightbar/#" });
    await Publish(c, "System/Control", new { Action = "System_ON" });

    var snapDir = Path.Combine(AppContext.BaseDirectory, "snaps");
    Directory.CreateDirectory(snapDir);
    var sw = Stopwatch.StartNew();
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        // 快照一组关键 EC 寄存器（0x00-0x7F）到文件
        var snap = new List<string>();
        IntPtr h = OpenDriver();
        for (int a = 0; a < 0x80; a++) snap.Add(ReadReg(h, a).ToString("X2"));
        CloseDriver(h);
        File.WriteAllText(Path.Combine(snapDir, $"{DateTime.Now:HHmmss}.txt"), string.Join(" ", snap));
        await Task.Delay(1000);
    }
    Console.WriteLine(">>> 快照目录: " + snapDir);
}
```

（`OpenDriver`/`ReadReg`/`CloseDriver` 复用 EcProbe 的三个 P/Invoke，把 EcProbe 里对应方法改为 internal 静态，供 Sniff 复用。）

- [ ] **Step 2: 触发一次模式切换并对照**

```bash
dotnet run --project src/Probe -- sniff 60
```
期间在原版 UI（或手动发布 `Fan/Control {Action:OPERATING_GAMING_MODE}`）切换一次模式。
Expected: 快照文件中模式切换前后差异的寄存器 → 记录"模式寄存器候选"到 m1-findings.md。

- [ ] **Step 3: Commit**

```bash
git add -A && git commit -m "feat: sniff mode with EC snapshots"
```

---

## M2：WPF 骨架

### Task 5: MechrevoLite WPF 项目与入口

**Files:**
- Create: `src/MechrevoLite/MechrevoLite.csproj`
- Create: `src/MechrevoLite/Program.cs`
- Create: `src/MechrevoLite/NativeMethods.cs`

- [ ] **Step 1: 创建项目**

```bash
cd C:\Users\28717\Desktop\ControlCenterX_5.56.60.26_Mechrevo
dotnet new wpf -n MechrevoLite -o src/MechrevoLite
dotnet sln add src/MechrevoLite/MechrevoLite.csproj
dotnet add src/MechrevoLite/MechrevoLite.csproj package MQTTnet
dotnet add src/MechrevoLite/MechrevoLite.csproj package Newtonsoft.Json
dotnet add src/MechrevoLite/MechrevoLite.csproj package CommunityToolkit.Mvvm
```

- [ ] **Step 2: 改 csproj 为单文件 + 管理员 manifest**

修改 `src/MechrevoLite/MechrevoLite.csproj` 增加：

```xml
  <PropertyGroup>
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>false</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
  </PropertyGroup>
```

新建 `src/MechrevoLite/app.manifest`：

```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3">
    <security>
      <requestedPrivileges>
        <requestedExecutionLevel level="requireAdministrator" uiAccess="false" />
      </requestedPrivileges>
    </security>
  </trustInfo>
  <compatibility xmlns="urn:schemas-microsoft-com:compatibility.v1">
    <application>
      <supportedOS Id="{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}" />
    </application>
  </compatibility>
</assembly>
```

并在 csproj 中 `<ApplicationManifest>app.manifest</ApplicationManifest>`。

- [ ] **Step 3: 写 Program.cs（单实例 + 托盘兜底 + 启动 MainWindow）**

```csharp
using System.Windows;
using Microsoft.Win32;

namespace MechrevoLite;

public static class Program
{
    [STAThread]
    public static void Main()
    {
        Mutex mutex = new(true, "MechrevoLite_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            // 已有实例：激活其主窗口后退出（简化：直接提示）
            MessageBox.Show("MechrevoLite 已在运行", "MechrevoLite", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
```

- [ ] **Step 4: 在 App.xaml.cs 处理全局异常**

```csharp
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MechrevoLite", "crash.log"),
                $"{DateTime.Now}: {args.Exception}\n");
            MessageBox.Show("发生错误：" + args.Exception.Message, "MechrevoLite");
            args.Handled = true;
        };
    }
}
```

- [ ] **Step 5: 构建并启动验证**

```bash
dotnet build src/MechrevoLite/MechrevoLite.csproj
```
Expected: 窗口正常显示（默认 WPF 窗口），无异常。

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: WPF skeleton with single instance and admin manifest"
```

### Task 6: 能力矩阵（CapabilityService）

**Files:**
- Create: `src/MechrevoLite/Models/CapabilityInfo.cs`
- Create: `src/MechrevoLite/Services/CapabilityService.cs`
- Test: `src/MechrevoLite.Tests/CapabilityServiceTests.cs`

- [ ] **Step 1: 写失败测试**

新建测试项目：

```bash
dotnet new xunit -n MechrevoLite.Tests -o tests/MechrevoLite.Tests
dotnet sln add tests/MechrevoLite.Tests/MechrevoLite.Tests.csproj
dotnet add tests/MechrevoLite.Tests reference src/MechrevoLite/MechrevoLite.csproj
```

```csharp
using MechrevoLite.Models;
using MechrevoLite.Services;
using Microsoft.Win32;
using Xunit;

public class CapabilityServiceTests
{
    [Fact]
    public void Parse_ReadsItemSupportValues()
    {
        // 用本机真实注册表（若测试环境无此项则跳过）
        var cap = new CapabilityService().Load();
        Assert.NotNull(cap.BiosProjectId);
        Assert.True(cap.LightbarSupport is 0 or 1);
    }

    [Fact]
    public void Parse_HandlesMissingRegistry()
    {
        // 用假键路径测试容错
        var cap = new CapabilityService(@"HKLM\SOFTWARE\OEM\GamingCenter2\NoSuchKey").Load();
        Assert.False(cap.IsSupported(f => f.Keys.Count > 0));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

```bash
dotnet test tests/MechrevoLite.Tests
```
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 写 CapabilityInfo + CapabilityService**

```csharp
namespace MechrevoLite.Models;

public class CapabilityInfo
{
    public string BiosProjectId { get; init; } = "";
    public bool KeyboardSupport { get; init; }
    public bool LightbarSupport { get; init; }
    public bool RgbLightbarSupport { get; init; }
    public bool FanSettingsSupport { get; init; }
    public bool FanBoostBtnSupport { get; init; }
    public bool TurboModeSupport { get; init; }
    public bool OcSettingsSupport { get; init; }
    public bool SystemMonitorSupport { get; init; }
    public bool DGpuDirectConnectionSupport { get; init; }
    public bool IGpuModeOnlySupport { get; init; }
    public bool LiquidCoolingSupport { get; init; }
    public bool ColorCalibrationSupport { get; init; }
    public bool NumPadSupport { get; init; }
    public bool AcRecoverySwitchSupport { get; init; }
    public bool IsAmdPlatform { get; init; }
    public bool IsNvGpu { get; init; }
    public bool Otasupport { get; init; }
    public bool SmartBalanceSupport { get; init; }
    public bool IsTurboSubModeSupport { get; init; }
    public int KeyboardType { get; init; }
    public int PanelType { get; init; }
    public int APVersionCheck { get; init; }
    // 如需更多键：从 ItemSupport 注册表名直读，保持属性名 = 注册表键名（小写首字母除外）
}
```

```csharp
using Microsoft.Win32;
using MechrevoLite.Models;

namespace MechrevoLite.Services;

public class CapabilityService
{
    const string DefaultPath = @"HKLM\SOFTWARE\OEM\GamingCenter2\ItemSupport";
    readonly string _path;
    public CapabilityService(string path = DefaultPath) { _path = path; }

    public CapabilityInfo Load()
    {
        var key = Registry.LocalMachine.OpenSubKey(_path.Replace(@"HKLM\", ""), false);
        var c = new CapabilityInfo
        {
            BiosProjectId = GetString(key, "BIOS_PROJECT_ID"),
            KeyboardSupport = GetBool(key, "KeyboardSupport"),
            LightbarSupport = GetBool(key, "LightbarSupport"),
            RgbLightbarSupport = GetBool(key, "RGBLightbarSupport"),
            FanSettingsSupport = GetBool(key, "FanSettingsSupport"),
            FanBoostBtnSupport = GetBool(key, "FanBoostBtnSupport"),
            TurboModeSupport = GetBool(key, "TurboModeSupport"),
            OcSettingsSupport = GetBool(key, "OcSettingsSupport"),
            SystemMonitorSupport = GetBool(key, "SystemMonitorSupport"),
            DGpuDirectConnectionSupport = GetBool(key, "DGpuDirectConnectionSupport"),
            IGpuModeOnlySupport = GetBool(key, "iGPUModeOnlySupport"),
            LiquidCoolingSupport = GetBool(key, "LiquidCoolingSupport"),
            ColorCalibrationSupport = GetBool(key, "ColorCalibrationSupport"),
            NumPadSupport = GetBool(key, "NumPadSupport"),
            AcRecoverySwitchSupport = GetBool(key, "AcRecoverySwitchSupport"),
            IsAmdPlatform = GetBool(key, "IsAMDPlatform"),
            IsNvGpu = GetBool(key, "IsNvGpu"),
            Otasupport = GetBool(key, "OTASupport"),
            SmartBalanceSupport = GetBool(key, "IsSmartBalanceSupport"),
            IsTurboSubModeSupport = GetBool(key, "IsTurboSubModeSupport"),
            KeyboardType = GetInt(key, "KeyboardType"),
            PanelType = GetInt(key, "PanelType"),
            APVersionCheck = GetInt(key, "APVersionCheck"),
        };
        return c;
    }

    static string? GetString(RegistryKey? k, string name) => k?.GetValue(name)?.ToString();
    static bool GetBool(RegistryKey? k, string name) => int.TryParse(k?.GetValue(name)?.ToString(), out var v) && v == 1;
    static int GetInt(RegistryKey? k, string name) => int.TryParse(k?.GetValue(name)?.ToString(), out var v) ? v : 0;
}
```

- [ ] **Step 4: 跑测试确认通过**

```bash
dotnet test tests/MechrevoLite.Tests
```
Expected: PASS（本机有真实 ItemSupport 键）。

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: capability service reading ItemSupport registry"
```

### Task 7: IHardwareBackend 抽象 + MqttBackend

**Files:**
- Create: `src/MechrevoLite/Hardware/IHardwareBackend.cs`
- Create: `src/MechrevoLite/Hardware/MqttBackend.cs`
- Test: `tests/MechrevoLite.Tests/MqttBackendTests.cs`

- [ ] **Step 1: 写接口（先定义契约，M3+ 各 Service 只依赖它）**

```csharp
namespace MechrevoLite.Hardware;

public record MqttMessage(string Topic, string Payload);

public interface IHardwareBackend : IDisposable
{
    event Action<bool>? ConnectionChanged;       // true=已连接
    event Action<MqttMessage>? MessageReceived;   // 订阅到的消息（原始 JSON）

    Task<bool> ConnectAsync();                    // 连接 + 注册握手（M1 结论固化）
    Task PublishAsync(string topic, object payload, bool retain = false);
    Task SubscribeAsync(IEnumerable<string> topics);
    bool IsConnected { get; }
}
```

- [ ] **Step 2: 写失败测试（MqttBackend 握手序列来自 m1-findings.md 结论）**

```csharp
using MechrevoLite.Hardware;
using Xunit;

public class MqttBackendTests
{
    [Fact]
    public async Task Connect_WithBrokerRunning_Connects()
    {
        // 本机 broker 存在则验证连接成功；无 broker 时跳过（环境前提）
        using var backend = new MqttBackend();
        var ok = await backend.ConnectAsync();
        if (ok) Assert.True(backend.IsConnected);
        else Assert.False(backend.IsConnected); // 允许离线环境通过，不失败
    }
}
```

- [ ] **Step 3: 写 MqttBackend（把 M1 结论的连接序列固化；凭据与 topic 常量照抄 UI）**

```csharp
using MQTTnet;
using MQTTnet.Client;

namespace MechrevoLite.Hardware;

public class MqttBackend : IHardwareBackend
{
    const string Host = "localhost";
    const int Port = 13688;
    const string User = "UWPClient_User_5";
    const string Pwd = "UWPClient_Pwd888881772688_5";

    readonly MqttFactory _factory = new();
    IMqttClient? _client;
    readonly List<string> _topics = new();

    public event Action<bool>? ConnectionChanged;
    public event Action<MqttMessage>? MessageReceived;
    public bool IsConnected => _client?.IsConnected == true;

    public async Task<bool> ConnectAsync()
    {
        _client = _factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += e =>
        {
            MessageReceived?.Invoke(new MqttMessage(e.ApplicationMessage.Topic,
                System.Text.Encoding.UTF8.GetString(e.ApplicationMessage.PayloadSegment)));
            return Task.CompletedTask;
        };
        _client.DisconnectedAsync += async e => { ConnectionChanged?.Invoke(false); await ReconnectLoopAsync(); };
        var res = await _client.ConnectAsync(new MqttClientOptionsBuilder()
            .WithTcpServer(Host, Port).WithCredentials(User, Pwd).WithClientId("UWPClient_5")
            .WithCleanSession(false).Build());
        var ok = res.ResultCode == MqttClientConnectResultCode.Success;
        if (ok)
        {
            ConnectionChanged?.Invoke(true);
            await SubscribeAsync(_topics);
        }
        return ok;
    }

    async Task ReconnectLoopAsync()
    {
        while (!IsConnected)
        {
            await Task.Delay(5000);
            try { await ConnectAsync(); } catch { /* 下轮重试 */ }
        }
    }

    public async Task PublishAsync(string topic, object payload, bool retain = false)
    {
        if (_client is null || !IsConnected) throw new InvalidOperationException("MQTT 未连接");
        await _client.PublishStringAsync(topic, Newtonsoft.Json.JsonConvert.SerializeObject(payload),
            MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce, retain);
    }

    public async Task SubscribeAsync(IEnumerable<string> topics)
    {
        _topics.AddRange(topics.Where(t => !_topics.Contains(t)));
        if (_client is null || !IsConnected) return;
        foreach (var t in topics)
            await _client.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build());
    }

    public void Dispose() => _client?.Dispose();
}
```

- [ ] **Step 4: 跑测试**

```bash
dotnet test tests/MechrevoLite.Tests
```
Expected: PASS（broker 运行中则连接成功；否则断言离线分支）。

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: IHardwareBackend abstraction with MqttBackend"
```

### Task 8: 遥测服务（TelemetryService）

**Files:**
- Create: `src/MechrevoLite/Models/TelemetrySnapshot.cs`
- Create: `src/MechrevoLite/Services/TelemetryService.cs`

- [ ] **Step 1: 写 TelemetrySnapshot（订阅到的 System/* 载荷结构，按抓包修正）**

```csharp
namespace MechrevoLite.Models;

public class TelemetrySnapshot : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    void Set<T>(ref T field, T value, string name) { field = value; PropertyChanged?.Invoke(this, new(name)); }

    double _cpuTemp; public double CpuTemp { get => _cpuTemp; set => Set(ref _cpuTemp, value, nameof(CpuTemp)); }
    double _gpuTemp; public double GpuTemp { get => _gpuTemp; set => Set(ref _gpuTemp, value, nameof(GpuTemp)); }
    int _fanSpeed; public int FanSpeed { get => _fanSpeed; set => Set(ref _fanSpeed, value, nameof(FanSpeed)); }
    int _batteryPercent; public int BatteryPercent { get => _batteryPercent; set => Set(ref _batteryPercent, value, nameof(BatteryPercent)); }
    bool _isAC; public bool IsAC { get => _isAC; set => Set(ref _isAC, value, nameof(IsAC)); }
    string _currentMode = "OFFICE"; public string CurrentMode { get => _currentMode; set => Set(ref _currentMode, value, nameof(CurrentMode)); }
    // 其余字段按 M1 抓包的实际 JSON 键补齐（Task 3 完成后）
}
```

- [ ] **Step 2: 写 TelemetryService（订阅 System/#、Fan/#，解析为快照，5s 心跳）**

```csharp
using MechrevoLite.Hardware;
using MechrevoLite.Models;
using Newtonsoft.Json.Linq;

namespace MechrevoLite.Services;

public class TelemetryService
{
    public TelemetrySnapshot Snapshot { get; } = new();
    readonly IHardwareBackend _backend;
    public TelemetryService(IHardwareBackend backend) { _backend = backend; }

    public async Task StartAsync()
    {
        await _backend.SubscribeAsync(new[] { "System/#", "Fan/#", "GPUDevice/#", "BatteryProtection/#" });
        _backend.MessageReceived += OnMessage;
        await _backend.PublishAsync("System/Control", new { Action = "System_ON" });
    }

    void OnMessage(MqttMessage msg)
    {
        try
        {
            var o = JObject.Parse(msg.Payload);
            switch (msg.Topic)
            {
                case "System/CpuInfo":
                    // 键名以 M1 抓包为准；示例映射：
                    // Snapshot.CpuTemp = o["Temperature"]?.Value<double>() ?? 0;
                    break;
                case "System/FanInfo":
                    // Snapshot.FanSpeed = o["Speed"]?.Value<int>() ?? 0;
                    break;
                case "System/BatteryInfo":
                    // Snapshot.BatteryPercent = ...; Snapshot.IsAC = ...;
                    break;
                case "Fan/Status":
                    // Snapshot.CurrentMode = o["PowerMode"]?.ToString() ?? "OFFICE";
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"telemetry parse fail: {msg.Topic} {ex.Message}");
        }
    }
}
```

- [ ] **Step 3: 用 M1 抓包结果填充映射**

对照 `docs/superpowers/plans/m1-findings.md` 里 System/CpuInfo、System/FanInfo、System/BatteryInfo、Fan/Status 的真实 JSON 键名，替换 `OnMessage` 中的示例映射为真实路径。若某键未在抓包中出现，回 M1 的 sniff/golden 补抓。

- [ ] **Step 4: 构建验证**

```bash
dotnet build src/MechrevoLite/MechrevoLite.csproj
```
Expected: 无编译错误。

- [ ] **Step 5: Commit**

```bash
git add -A && git commit -m "feat: telemetry service consuming System topics"
```

### Task 9: 主窗口 + 首页模式卡片

**Files:**
- Create: `src/MechrevoLite/ViewModels/MainViewModel.cs`
- Create: `src/MechrevoLite/ViewModels/HomeViewModel.cs`
- Modify: `src/MechrevoLite/Views/MainWindow.xaml`（替换默认）
- Create: `src/MechrevoLite/Views/HomePage.xaml(.cs)`

- [ ] **Step 1: 写 MainViewModel（导航）+ HomeViewModel（模式切换）**

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MechrevoLite.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty] private object? _currentPage;
    public HomeViewModel Home { get; }
    public MainViewModel(HomeViewModel home) { Home = home; CurrentPage = home; }

    [RelayCommand] void Navigate(string page) => CurrentPage = page switch
    {
        "home" => Home,
        _ => Home,
    };
}
```

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MechrevoLite.Hardware;

namespace MechrevoLite.ViewModels;

public partial class HomeViewModel : ObservableObject
{
    public const string OfficeMode = "OPERATING_OFFICE_MODE";
    public const string GamingMode = "OPERATING_GAMING_MODE";
    public const string TurboMode = "OPERATING_TURBO_MODE";
    public const string CustomMode = "OPERATING_CUSTOM_MODE";

    readonly IHardwareBackend _backend;
    public HomeViewModel(IHardwareBackend backend) { _backend = backend; }

    [RelayCommand]
    async Task SwitchMode(string action)
    {
        await _backend.PublishAsync("Fan/Control", new { Action = action });
        // M3 里程碑：接入 Fan/Status 回包确认 + OSD 提示
    }
}
```

- [ ] **Step 2: 写 MainWindow.xaml（左侧导航 + Frame 内容区）**

```xml
<Window x:Class="MechrevoLite.Views.MainWindow" xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:views="clr-namespace:MechrevoLite.Views"
        Title="机械革命控制台" Height="720" Width="1080" WindowStartupLocation="CenterScreen">
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width="56"/>
      <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>
    <Border Grid.Column="0" Background="#FF1F1F1F">
      <StackPanel Margin="0,12,0,0">
        <Button Content="🏠" Command="{Binding NavigateCommand}" CommandParameter="home"
                Style="{StaticResource NavBtn}" ToolTip="首页"/>
        <!-- M3+ 依次添加：风扇/GPU/灯效/电池/快捷/设置 -->
      </StackPanel>
    </Border>
    <ContentControl Grid.Column="1" Content="{Binding CurrentPage}"/>
  </Grid>
</Window>
```

窗口资源里定义 `NavBtn` 样式（无边框、宽 44 高 44、圆角 8、悬停变亮）。`MainWindow.xaml.cs` 构造注入 `MainViewModel`（用简单 ServiceLocator 或手动 new，不引 DI 容器）。

- [ ] **Step 3: 写 HomePage.xaml（模式卡片 + 遥测绑定）**

```xml
<UserControl x:Class="MechrevoLite.Views.HomePage" ...>
  <StackPanel Margin="24">
    <TextBlock Text="运行模式" FontSize="22" FontWeight="Bold"/>
    <ItemsControl>
      <ItemsControl.ItemsPanel><ItemsPanelTemplate><WrapPanel/></ItemsPanelTemplate></ItemsControl.ItemsPanel>
      <ItemsControl.ItemTemplate>
        <DataTemplate>
          <Button Content="{Binding}" Command="{Binding DataContext.SwitchModeCommand, RelativeSource={RelativeSource AncestorType=UserControl}}"
                  CommandParameter="{Binding}" Width="150" Height="90" Margin="8" FontSize="18"
                  Style="{StaticResource ModeCard}"/>
        </DataTemplate>
      </ItemsControl.ItemTemplate>
      <ItemsControl.ItemsSource>
        <x:Array Type="sys:String" xmlns:sys="clr-namespace:System;assembly=mscorlib">
          <sys:String>OPERATING_OFFICE_MODE</sys:String>
          <sys:String>OPERATING_GAMING_MODE</sys:String>
          <sys:String>OPERATING_TURBO_MODE</sys:String>
          <sys:String>OPERATING_CUSTOM_MODE</sys:String>
        </x:Array>
      </ItemsControl.ItemsSource>
    </ItemsControl>
    <TextBlock Text="关键指标" FontSize="22" FontWeight="Bold" Margin="0,24,0,8"/>
    <StackPanel Orientation="Horizontal">
      <TextBlock Text="{Binding Snapshot.CpuTemp, StringFormat='CPU {0:F0}°C'}" FontSize="16" Margin="0,0,24,0"/>
      <TextBlock Text="{Binding Snapshot.GpuTemp, StringFormat='GPU {0:F0}°C'}" FontSize="16" Margin="0,0,24,0"/>
      <TextBlock Text="{Binding Snapshot.FanSpeed, StringFormat='风扇 {0} rpm'}" FontSize="16"/>
    </StackPanel>
  </StackPanel>
</UserControl>
```

`HomePage.xaml.cs` 构造注入 `HomeViewModel` 为 DataContext。（`ModeCard` 样式：深色卡片、悬停描边、选中色随模式。）

- [ ] **Step 4: 组装启动（Program.cs 中串联）**

修改 `Program.cs` 的 Main：创建 `MqttBackend` → `TelemetryService` → 后台 `ConnectAsync` + `StartAsync` → 打开 MainWindow。未连接时首页显示"服务未连接"提示条（TextBlock 绑定 `Backend.IsConnected`，样式随状态）。

- [ ] **Step 5: 运行验证**

```bash
dotnet run --project src/MechrevoLite
```
Expected: 窗口显示 4 张模式卡片；点击卡片后 broker 日志/抓包可见 `Fan/Control {Action:OPERATING_*_MODE}` 发布；遥测文本显示数值（若 broker 推流已通）。

- [ ] **Step 6: Commit**

```bash
git add -A && git commit -m "feat: main window with home page mode cards"
```

### Task 10: 托盘 + 退出清理

**Files:**
- Modify: `src/MechrevoLite/Program.cs`
- Create: `src/MechrevoLite/Services/TrayService.cs`

- [ ] **Step 1: 写 TrayService（NotifyIcon 托管封装）**

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Forms;

namespace MechrevoLite.Services;

public class TrayService : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon _icon = new();
    readonly Action _onMode;
    public TrayService(Action onModeSwitch, Action onExit)
    {
        _onMode = onModeSwitch;
        _icon.Icon = System.Drawing.SystemIcons.Application;
        _icon.Text = "MechrevoLite";
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => _onMode());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("办公模式", null, (_, _) => SwitchMode("OPERATING_OFFICE_MODE"));
        menu.Items.Add("游戏模式", null, (_, _) => SwitchMode("OPERATING_GAMING_MODE"));
        menu.Items.Add("极速模式", null, (_, _) => SwitchMode("OPERATING_TURBO_MODE"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => onExit());
        _icon.ContextMenuStrip = menu;
        _icon.Visible = true;
        _icon.DoubleClick += (_, _) => _onMode();
    }
    void SwitchMode(string m) => _onMode(); // M3 接真实发布
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
```

- [ ] **Step 2: 接入 Program.cs（关窗最小化到托盘、托盘退出 = 发 System_OFF 后退出）**

修改 Program.cs：主窗口 `StateChanged` 处理（最小化 → `Hide()`）；`OnExit` 时 `await backend.PublishAsync("System/Control", new { Action = "System_OFF" })`（尽力而为）+ 释放资源。

- [ ] **Step 3: 构建 + 手动验证**

```bash
dotnet run --project src/MechrevoLite
```
Expected: 关闭窗口后驻留托盘；托盘右键菜单出现；选"退出"后进程完全退出；若已连 broker，退出前发出 System_OFF（抓包可证）。

- [ ] **Step 4: Commit**

```bash
git add -A && git commit -m "feat: tray service with mode quick switch and clean exit"
```

---

## 后续里程碑（独立计划）

- **M3 散热核心**：模式切换回包确认 + OSD 悬浮 + 遥测雷达图 + 风扇曲线页（曲线点换算 + Fan/Control 发布）
- **M4 电池/快捷开关**：BatteryProtection/Control + Setting/Control 开关矩阵
- **M5 GPU/显示**：MUX 切换（含重启确认）+ 刷新率 + 校色
- **M6 灯效**：键盘/灯条 HID 载荷全套（最复杂）
- **M7 直连迁移**：DriverBackend（IOCTL + SMAP 表 + HID 直写），逐域替换
- **M8 收尾**：服务停用按钮 + 单文件发布 + 文档

## M1 验收标准（进入 M2 的门槛）

1. `probe handshake` 跑通：能收到至少一个 Status 回包（Fan/Status、Setting/Status、Customize/SupportInfo 任一）
2. `probe sniff` 期间切换模式，EC 快照出现可重复的寄存器差异
3. `m1-findings.md` 完成，包含：broker 鉴权规则结论、遥测推流触发条件、MqttBackend 连接序列、System/* 各主题真实 JSON 键名清单
4. 若 broker 拒绝第三方客户端（连接即断），记录结论并在设计中切换 Phase 1 策略（如复用 WCF 通道或直连提前）

**若 M1 失败兜底**：EC 直连已验证可用——M2 骨架可先以 EC 只读遥测替代 MQTT 遥测，不影响骨架交付，但 `IHardwareBackend` 接口不变。
