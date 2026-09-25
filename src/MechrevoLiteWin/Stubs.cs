using System.Drawing;
using System.Windows.Forms;

namespace MechrevoLite;

// ---- 已删除 ASUS 模块的存根类型：保证继承自 g-helper 的 UI 编译与运行，功能空实现 ----
//
// 成员签名对齐 g-helper-main/app 的对应原文件（InputDispatcher.cs / Aura.cs /
// AutoUpdateControl.cs / PeripheralsProvider.cs / AsusLampArray.cs）。
//
// 已经连同调用点一起删掉的族（**不要再加回来**）：
//   AllyControl / ControllerMode / Handheld —— ROG Ally 掌机
//   AniMatrixControl / Matrix / SlashDevice / SlashMode —— ROG 机盖点阵屏与 Slash 灯条
//   XGM —— XG Mobile 外置显卡坞
// 这三族在机械革命上没有任何对应硬件，删除前的状态是「每次启动都调用、但全部落到空实现」，
// 而所有能产生可见效果的分支都被恒 false 的门禁挡住。
// 判断依据与逐项调用链见 CHANGELOG 的清理条目。
//
// 剩下这些还留着，是因为 UI 里仍有活的调用点在引用它们的签名（多为开机/灯效路径），
// 把它们一起删掉需要连带重写那些路径，属于另一件事。

public class InputDispatcher : IDisposable
{
    public void Init() { }
    public void InitBacklightTimer() { }
    public void RegisterKeys() { }
    public void Dispose() { }
    public static void ShutdownStatusLed() { }
    public static void InitScreenpad() { }
    public static void InitStatusLed() { }
    public static void AutoKeyboard() { }
    public static void StartupBacklight() { }
    public static void InitFNLock() { }
    public static int GetBacklight() => 0;
    public static bool lidClose;
}

public class AutoUpdateControl
{
    public AutoUpdateControl(SettingsForm settingsForm) { }
    public void CheckForUpdates() { }
    public void Update() { }
}

public class Updates : Form
{
    public void InitTheme() { }
}

public static class NumberPad
{
    public static void Init() { }
}

// Aura 是 ASUS 的键盘/灯带灯效栈。机械革命的键盘 RGB 与灯带走的是
// KeyboardRgb + LightForm + HidLightbar/* 主题，与这里无关。
//
// 只保留仍有活调用点的成员（多在盖子开合、会话锁、GPU 模式切换那几条路径上），
// 零调用方的那批已删除：Init / ApplyPower / SetColor / SetColor2 / GetModes /
// GetSpeeds / IsBacklightDetected / IsOldStrix / isWhite / Speed，
// 以及整套机背灯（RearColor / RearMode / SetRearColor / GetRearModes / HasRearglow）、
// HasLightbar / HasLogo。
public static class Aura
{
    /// <summary>机械革命已连接时灯效走 KeyboardRgb/LightForm，禁止落到 ASUS Aura/ACPI。</summary>
    internal static bool IsMechrevoConnected => Program.hw is { IsConnected: true };

    public static void ApplyAura()
    {
        if (IsMechrevoConnected) return;
    }

    internal static void NotifyApplyOutcome(bool success)
    {
        if (success) return;
        Helpers.ToastForm.ShowFailure("灯效设置失败。");
    }
    public static void ApplyBrightness()
    {
        if (IsMechrevoConnected) return;
    }
    public static void ApplyBrightness(int brightness, string log = "Backlight")
    {
        if (IsMechrevoConnected) return;
    }
    public static void SleepBrightness()
    {
        if (IsMechrevoConnected) return;
    }
    public static Color Color1 { get; set; } = Color.White;
    public static Color Color2 { get; set; } = Color.Black;
    public static AuraMode Mode { get; set; }
    public static bool sessionLock { get; set; }
    public static bool HasRandomColor() => false;
    public static bool HasSecondColor() => false;

    public static class CustomRGB
    {
        public static void ApplyGPUColor(int gpuMode = -1)
        {
            if (IsMechrevoConnected) return;
        }
    }
}


public static class PeripheralsProvider
{
    public static void RefreshBatteryForAllDevices(bool force) { }
    public static void RegisterForDeviceEvents() { }
    public static void UnregisterForDeviceEvents() { }
    public static void DetectAllAsusMice() { }
}

public enum AuraMode
{
    Single = 0, ColorCycle = 1, Breathing = 2, Strobe = 3, Rainbow = 4,
    Star = 5, Rain = 6, Highlight = 7, Wave = 8, Marquee = 9,
    Composition = 10, Native = 11, Ambient = 12, AMBIENT = 12, Static = 13,
    Custom = 14, AuraStatic = 0, AuraBreathe = 2, AuraStrobe = 3, AuraColorCycle = 1,
    AuraRainbow = 4, AuraComposition = 10, AuraWave = 8, AuraStar = 5, AuraRain = 6,
    AuraHighlight = 7, AuraMarquee = 9, AuraNative = 11
}

public static class KeyboardHook
{
    public static void KeyKeyPress(Keys key1, Keys key2) { }
}

public static class AsusLampArray
{
    public static void Release() { }
}
