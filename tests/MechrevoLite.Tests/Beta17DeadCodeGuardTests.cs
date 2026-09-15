namespace MechrevoLite.Tests;

/// <summary>
/// beta17 死代码清除的防回归扫描（docs/beta17-cleanup-plan.md Wave A / docs/beta17-wave-plan.md T2.2/T2.5/T2.6）。
///
/// 这些符号在 beta17 中被证明不可达后删除：外围设备面板挂在恒空的 AllPeripherals() 上、
/// FnLock 与键盘下拉的处理器从未被 += 订阅、fahrenheit 键零写入路径（owner 决定锁定摄氏）。
/// 删除本身不会留下编译期痕迹，重新“填空”也不会报错——这条测试就是红的那一次提醒。
///
/// Wave B（owner U1）追加四个零写入路径配置键：disable_osd / theme(flat) / topmost / sensors_always。
/// theme、topmost 用带引号或带赋值前缀的精确串匹配，避免命中 darkTheme / HWND_TOPMOST 一类正常符号；
/// topmost 只锁 Settings.cs 的读取点，Program.cs 的同键读取点由另一任务保留。
/// </summary>
public class Beta17DeadCodeGuardTests
{
    /// <summary>已删除的符号及删除依据；注释里的历史说明不算引用（既有清理条目就保留这种说明）。</summary>
    static readonly (string Symbol, string Why)[] RemovedSymbols =
    {
        ("ButtonFnLock_Click", "FnLock 按钮处理器从未被任何 += 订阅，beta17 已删"),
        ("ComboKeyboard_SelectedValueChanged", "键盘下拉处理器从未被任何 += 订阅，beta17 已删"),
        ("AllPeripherals", "恒返回空列表的外围设备入口，整条面板链路已删"),
        ("IsFahrenheit", "fahrenheit 配置键零写入路径，温度锁定摄氏（owner 决定）"),
        ("_dashboardPageHost", "从未被赋值的死字段，beta17 已删"),
        ("_lightingActionTable", "从未被赋值的死字段，beta17 已删"),
        ("disable_osd", "OSD 关闭键零写入路径，ToastForm 早退读取点已删（beta17 U1）"),
        ("sensors_always", "传感器常开键零写入路径，读取点已删并收敛为 false 默认（beta17 U1）"),
        ("TopMost = AppConfig.Is(\"topmost\")", "置顶键零写入路径，Settings.cs 三处读取点已删；Program.cs 同键读取点归另一任务（beta17 U1）"),
        ("AppConfig.GetString(\"theme\")", "flat 主题键零写入路径，RForm 读取点已删（beta17 U1）"),
        ("flatTheme", "flat 主题标志已删，RComboBox 不可达分支一并收敛（beta17 U1）"),
    };

    [Fact]
    public void RemovedDeadSymbolsDoNotComeBack()
    {
        var violations = new List<string>();
        foreach (string path in SourceFiles())
        {
            string relative = Path.GetRelativePath(RepoRoot, path);
            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("//")) continue;
                foreach ((string symbol, string why) in RemovedSymbols)
                {
                    if (lines[i].Contains(symbol, StringComparison.Ordinal))
                        violations.Add($"{relative}:{i + 1}: \"{symbol}\"（{why}）：{lines[i].Trim()}");
                }
            }
        }

        Assert.True(violations.Count == 0,
            "beta17 已证明不可达并删除的死代码不得复活：\n  " + string.Join("\n  ", violations));
    }

    static IEnumerable<string> SourceFiles()
    {
        string root = Path.Combine(RepoRoot, "src");
        Assert.True(Directory.Exists(root), "找不到 src 目录");
        return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));
    }

    static string RepoRoot
    {
        get
        {
            _repoRoot ??= FindRepoRoot();
            return _repoRoot!;
        }
    }

    static string? _repoRoot;

    static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
