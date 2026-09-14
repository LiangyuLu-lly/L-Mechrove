using System.Reflection;
using MechrevoLite.Hardware;

namespace MechrevoLite.Tests;

/// <summary>
/// 锁住「继承自 g-helper 的 ASUS 专有代码已清理」这个状态。
///
/// 本项目是从 g-helper（华硕控制中心）fork 来的，所以仓库里长期带着一批
/// 华硕专有硬件的实现：ROG Ally 掌机、机盖点阵屏（Anime Matrix）与 Slash 灯条、
/// XG Mobile 外置显卡坞、Splendid 色域引擎、按 ASUS 机型串匹配的一大片谓词。
///
/// 它们的共同特征是**在机械革命机器上恒不可达**，但可达性是靠层层恒 false 的门禁
/// 维持的，不是显式声明的。这种代码的害处不是占空间，而是：
///   1. 有人照着它接线，得到一个「点了没反应」的开关；
///   2. 判据是机型名子串匹配，某台机械革命的机型串一旦意外命中，就会走上
///      一条从未被验证过的分支（IsChargeLimit6080 的候选里有 "H760" 就是实例）。
///
/// 所以这一组测试用反射断言那些符号确实不在了。反射而不是编译期引用，
/// 是因为编译期引用一个不存在的类型会直接编译失败，测试就没机会跑。
/// </summary>
public class AsusResidueCleanupTests
{
    static Assembly App => typeof(MechrevoHw).Assembly;

    /// <summary>
    /// 三族 ASUS 专有硬件的类型必须不存在。
    ///
    /// 这些类型在删除前是 Stubs.cs 里的空存根（方法体全是 <c>{ }</c> 或 <c>=&gt; false</c>），
    /// 每次启动仍会被 new 出来并调用，只是全部落到 no-op。
    /// </summary>
    [Theory]
    // ROG Ally 掌机
    [InlineData("MechrevoLite.AllyControl")]
    [InlineData("MechrevoLite.ControllerMode")]
    [InlineData("MechrevoLite.Handheld")]
    // 机盖点阵屏与 Slash 灯条
    [InlineData("MechrevoLite.AniMatrixControl")]
    [InlineData("MechrevoLite.Matrix")]
    [InlineData("MechrevoLite.SlashDevice")]
    [InlineData("MechrevoLite.SlashMode")]
    // XG Mobile 外置显卡坞
    [InlineData("MechrevoLite.XGM")]
    // ASUS Splendid（GameVisual）色域引擎与它的 ICC 下载表
    [InlineData("MechrevoLite.Display.VisualControl")]
    [InlineData("MechrevoLite.Display.SplendidGamut")]
    [InlineData("MechrevoLite.Display.SplendidCommand")]
    [InlineData("MechrevoLite.Display.ColorProfileHelper")]
    public void AsusOnlyTypesAreGone(string fullName)
    {
        Assert.Null(App.GetType(fullName, throwOnError: false));
    }

    /// <summary>
    /// 按 ASUS 机型串匹配的谓词必须不存在。
    ///
    /// 判据都形如 <c>ContainsModel("TUF")</c>，对机械革命一律为假。
    /// <c>IsChargeLimit6080</c> 尤其要盯住：它的候选清单里有 "H760"，
    /// 而它做的事是在机械革命的三档对齐之前先按 ASUS 那套改一遍充电上限
    /// （ASUS 是 &gt;85→100 / &gt;=80→80 / &lt;60→60，我们是 &gt;=95→100 / &gt;=80→80 / else→60），
    /// 一旦命中就会落到与预期不同的档位。
    /// </summary>
    [Theory]
    [InlineData("IsAlly")]
    [InlineData("IsTUF")]
    [InlineData("IsVivoZenPro")]
    [InlineData("IsDUO")]
    [InlineData("IsSlash")]
    [InlineData("IsSlashLong")]
    [InlineData("IsZ13")]
    [InlineData("HasRearLight")]
    [InlineData("IsChargeLimit6080")]
    [InlineData("IsAMDiGPU")]
    public void AsusModelPredicatesAreGone(string method)
    {
        Assert.Null(typeof(AppConfig).GetMethod(method,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }

    /// <summary>ASUS 专有的显卡设备码与模式常量必须不存在。</summary>
    [Theory]
    [InlineData("GPUEcoROG")]
    [InlineData("GPUEcoVivo")]
    [InlineData("GPUXGConnected")]
    [InlineData("GPUXG")]
    [InlineData("GPUMuxROG")]
    [InlineData("GPUMuxVivo")]
    [InlineData("VivoBookMode")]
    [InlineData("PerformanceFullSpeed")]
    public void AsusOnlyDeviceCodesAreGone(string field)
    {
        Assert.Null(typeof(AsusACPI).GetField(field,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static));
    }

    /// <summary>
    /// 界面侧的 ASUS 专有成员必须不存在。
    ///
    /// 这些方法的共同点是：宿主面板从来没有被 Add 到窗体上（<c>SettingsForm</c>
    /// 只 Add 了 6 个面板），所以它们连渲染都没发生过。
    /// </summary>
    [Theory]
    [InlineData("VisualiseAlly")]
    [InlineData("VisualiseBacklight")]
    [InlineData("VisualiseFPSLimit")]
    [InlineData("VisualiseAutoTDP")]
    [InlineData("InitMatrix")]
    [InlineData("CycleMatrix")]
    [InlineData("CycleAuraMode")]
    [InlineData("VisualiseMatrixPicture")]
    [InlineData("VisualiseMatrixRunning")]
    [InlineData("VisualizeXGM")]
    [InlineData("InitVisual")]
    [InlineData("CycleVisualMode")]
    [InlineData("VisualiseGamut")]
    [InlineData("VisualiseBrightness")]
    [InlineData("InitRearLight")]
    public void AsusOnlySettingsMembersAreGone(string method)
    {
        Assert.Null(typeof(SettingsForm).GetMethod(method,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    /// <summary>
    /// <c>MechrevoHw</c> 上不得再出现与 <c>MechrevoService</c> 平行的第二张下发表。
    ///
    /// 那批方法只发不确认、零调用方，而且会随协议演进静默腐化。
    /// </summary>
    [Theory]
    [InlineData("SetQuickSwitch")]
    [InlineData("SetUsbCharger")]
    [InlineData("SetFanBoost")]
    [InlineData("SetRefreshRate")]
    [InlineData("SetGpuPowerSaving")]
    [InlineData("SetDisconnectMonitor")]
    [InlineData("SetDcOnce")]
    public void MechrevoHwHasNoParallelUnconfirmedCommandTable(string method)
    {
        Assert.Null(typeof(MechrevoHw).GetMethod(method,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance));
    }

    // ---------------------------------------------------------------------
    // 下面这一组是反向断言：这些**名字看起来像 ASUS、实际是活代码**，
    // 按关键字批量清理时最容易被误删。每一条都写明它为什么必须留下。
    // ---------------------------------------------------------------------

    /// <summary>
    /// <c>RyzenSmu</c> 里的 Strix 不是华硕 ROG Strix，是 **AMD Zen5 的代号**
    /// （Strix Point = Ryzen AI 9，Strix Halo = Ryzen AI MAX）。
    /// 它是活的 SMU 信箱路由：机械革命 AMD 机型的 TDP 与曲线优化都走它。
    /// 按 "Strix" 批量删会直接打断 AMD 调压。
    /// </summary>
    [Fact]
    public void AmdStrixCodeNamesSurvive()
    {
        Type? cpuCodeName = App.GetType("PawnIO.CpuCodeName", throwOnError: false);
        Assert.NotNull(cpuCodeName);
        Assert.Contains("StrixPoint", Enum.GetNames(cpuCodeName!));
        Assert.Contains("StrixHalo", Enum.GetNames(cpuCodeName!));

        // CpuFamily 里的同名成员是 SMU 信箱的路由分支，同样不能删。
        Type? cpuFamily = App.GetType("PawnIO.CpuFamily", throwOnError: false);
        Assert.NotNull(cpuFamily);
        Assert.Contains("StrixPoint", Enum.GetNames(cpuFamily!));
        Assert.Contains("StrixHalo", Enum.GetNames(cpuFamily!));
    }

    /// <summary>
    /// <c>KeyboardRgb.ModeMatrix</c> / <c>MatrixSpeed</c> 与机盖点阵屏（Anime Matrix）
    /// 毫无关系：那是机械革命键盘 RGB 的一个固件灯效档位，是在用的功能。
    /// </summary>
    [Fact]
    public void KeyboardMatrixEffectSurvives()
    {
        Assert.NotNull(typeof(KeyboardRgb).GetField("ModeMatrix",
            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic));
    }

    /// <summary>
    /// <c>AsusACPI</c> 类本身是活的适配层（shim）：它把继承自 g-helper 的界面调用
    /// 转成 <c>MechrevoHw</c> 的操作。风扇曲线的读写就走这里。
    /// 名字带 Asus 不等于死代码。
    /// </summary>
    [Fact]
    public void AsusAcpiShimSurvivesBecauseItIsTheLiveAdapter()
    {
        foreach (string method in new[] { "GetFan", "GetFanCurve", "SetFanCurve", "DeviceGet", "SetPerformanceMode" })
            Assert.NotNull(typeof(AsusACPI).GetMethod(method,
                BindingFlags.Public | BindingFlags.Instance));
    }

    /// <summary>
    /// <c>AsusFan</c> 枚举是活的：<c>CPU</c> / <c>GPU</c> 是风扇曲线映射的入参。
    /// 只有 <c>XGM</c>（外置显卡坞的风扇）被删掉了；
    /// <c>Mid</c> 保留但恒不可用——这套协议里没有第三颗风扇的读数。
    /// </summary>
    [Fact]
    public void AsusFanEnumKeepsTheLiveMembersAndDropsXgm()
    {
        string[] names = Enum.GetNames<AsusFan>();
        Assert.Contains("CPU", names);
        Assert.Contains("GPU", names);
        Assert.Contains("Mid", names);
        Assert.DoesNotContain("XGM", names);
    }

    /// <summary>
    /// <c>IsXGConnected()</c> 保留并恒 false，是**有意的显式否认**而不是漏改：
    /// 机械革命全系都没有 XG Mobile 接口，恒 false 就是正确答案。
    /// 删掉它会让调用方失去这个明确的语义锚点。
    /// </summary>
    [Fact]
    public void ExplicitDenialOfXgMobileSurvives()
    {
        MethodInfo? method = typeof(AsusACPI).GetMethod("IsXGConnected", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(method);
        Assert.False((bool)method!.Invoke(new AsusACPI(), null)!);
    }

    /// <summary>
    /// Overlay（硬件状态悬浮窗）是机械革命在用的功能，入口在动作面板里
    /// （<c>AddAction("悬浮监控", ...)</c>）。它的按钮 <c>buttonOverlay</c> 曾经和
    /// ROG Ally 的两个按钮同处一个不可见的表格里，清理时容易被一起删掉。
    /// </summary>
    [Fact]
    public void OverlayToggleSurvives()
    {
        Assert.NotNull(typeof(SettingsForm).GetMethod("ToggleOverlay",
            BindingFlags.Public | BindingFlags.Instance));
        Assert.NotNull(typeof(SettingsForm).GetMethod("ToggleOverlayGameOnly",
            BindingFlags.Public | BindingFlags.Instance));
    }

    /// <summary>
    /// 真实屏幕亮度走 <c>ScreenBrightness</c> + <c>BrightnessCommitQueue</c>，
    /// 与被删掉的 Splendid gamma 假调光（sliderGamma）是两回事。
    /// </summary>
    [Fact]
    public void RealScreenBrightnessPathSurvives()
    {
        Assert.NotNull(typeof(MechrevoHw).GetProperty("ScreenBrightness"));
        Assert.NotNull(typeof(MechrevoHw).GetProperty("ScreenBrightnessSeen"));
        Assert.NotNull(App.GetType("MechrevoLite.Display.BrightnessCommitQueue", throwOnError: false));
    }

    /// <summary>
    /// 机械革命自己的色彩校正写入通路必须还在——它是删掉 Splendid 色域通路之后
    /// 这块功能的唯一承载者。（2026-09-14：ColorCalibrationForm 表单删除，
    /// 改为屏幕行头内联下拉；类型断言随之移除，写入方法保留。）
    /// </summary>
    [Fact]
    public void MechrevoColorCalibrationSurvives()
    {
        Assert.NotNull(typeof(MechrevoService).GetMethod("SetColorCalibration"));
    }
}
