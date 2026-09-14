using System.Runtime.CompilerServices;

namespace MechrevoLite.Tests;

/// <summary>
/// B2：把 AppConfig 指向临时目录，保证测试套件（含会调用 RCollapseGroup.Toggle 的布局测试）
/// 绝不写入用户的 %APPDATA%\MechrevoLite\config.json。模块初始化器在任何测试之前运行。
/// </summary>
internal static class TestConfigIsolation
{
    [ModuleInitializer]
    internal static void Init()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-tests", "config");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("LMECHREVO_CONFIG_FILE", Path.Combine(directory, "config.json"));

        // 自启动快捷开关的接缝替身：SettingsForm 构造时会读一次系统自启动状态，
        // 若默认走生产实现就会在测试里查询真实的 Task Scheduler。这里在模块初始化时
        // 换成纯内存替身，保证测试绝不读/写真实的系统自启动项。需要特定状态的测试
        // 自行保存/恢复这两个委托（套件已禁用并行，静态状态安全）。
        Startup.ReadScheduledState = static () => false;
        Startup.WriteScheduledState = static _ => true;
    }
}
