using Microsoft.Win32;
using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// 隔离快照的恢复策略。快照内容会驱动**管理员级**的注册表写入与文件移动，
/// 所以它必须被当作不可信输入来校验：采集阶段只扫 Run / RunOnce，
/// 恢复阶段就只能写回 Run / RunOnce。
/// </summary>
public class IsolationSnapshotPolicyTests
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    // ---- 注册表白名单 ----

    [Theory]
    [InlineData("HKLM")]
    [InlineData("HKCU")]
    public void OnlyTheTwoCapturedHivesAreRestorable(string hive) =>
        Assert.True(OfficialConsoleIsolation.IsAllowedRegistryHive(hive));

    [Theory]
    [InlineData("HKU")]
    [InlineData("HKCR")]
    [InlineData("hklm")]
    [InlineData("")]
    [InlineData(null)]
    public void EveryOtherHiveIsRejected(string? hive) =>
        Assert.False(OfficialConsoleIsolation.IsAllowedRegistryHive(hive));

    [Theory]
    [InlineData(RunKey)]
    [InlineData(RunOnceKey)]
    public void OnlyTheCapturedRunKeysAreRestorable(string keyPath) =>
        Assert.True(OfficialConsoleIsolation.IsAllowedRunKeyPath(keyPath));

    /// <summary>
    /// 这些是没有校验时可以被写入的高价值目标：服务 ImagePath、IFEO、Winlogon。
    /// </summary>
    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\Evil")]
    [InlineData(@"Software\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\sethc.exe")]
    [InlineData(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\Run\..\..\Evil")]
    [InlineData(@"Software\Microsoft\Windows\CurrentVersion\RunServices")]
    [InlineData("")]
    [InlineData(null)]
    public void AnyOtherKeyPathIsRejected(string? keyPath) =>
        Assert.False(OfficialConsoleIsolation.IsAllowedRunKeyPath(keyPath));

    [Theory]
    [InlineData(RegistryValueKind.String)]
    [InlineData(RegistryValueKind.ExpandString)]
    public void StartupValuesAreStringsOnly(RegistryValueKind kind) =>
        Assert.True(OfficialConsoleIsolation.IsAllowedRegistryValueKind(kind));

    [Theory]
    [InlineData(RegistryValueKind.Binary)]
    [InlineData(RegistryValueKind.DWord)]
    [InlineData(RegistryValueKind.QWord)]
    [InlineData(RegistryValueKind.MultiString)]
    [InlineData(RegistryValueKind.Unknown)]
    [InlineData(RegistryValueKind.None)]
    public void AnyOtherValueKindIsRejected(RegistryValueKind kind) =>
        Assert.False(OfficialConsoleIsolation.IsAllowedRegistryValueKind(kind));

    [Theory]
    [InlineData("CCUWinUI")]
    [InlineData("Official Console")]
    public void OrdinaryValueNamesAreAccepted(string name) =>
        Assert.True(OfficialConsoleIsolation.IsAllowedRegistryValueName(name));

    /// <summary>值名里带分隔符等于换了一个键。</summary>
    [Theory]
    [InlineData(@"sub\key")]
    [InlineData("sub/key")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValueNamesThatCouldEscapeTheKeyAreRejected(string? name) =>
        Assert.False(OfficialConsoleIsolation.IsAllowedRegistryValueName(name));

    [Fact]
    public void ACompleteInPolicyRegistryEntryIsRestorable() =>
        Assert.True(OfficialConsoleIsolation.IsRestorableRegistryEntry(
            "HKLM", RunKey, "CCUWinUI", RegistryValueKind.String));

    /// <summary>四项校验里任何一项不过关，整条都不能恢复。</summary>
    [Theory]
    [InlineData("HKCR", RunKey, "CCUWinUI", RegistryValueKind.String)]
    [InlineData("HKLM", @"SYSTEM\CurrentControlSet\Services\Evil", "ImagePath", RegistryValueKind.String)]
    [InlineData("HKLM", RunKey, @"a\b", RegistryValueKind.String)]
    [InlineData("HKLM", RunKey, "CCUWinUI", RegistryValueKind.Binary)]
    public void AnyPolicyViolationBlocksTheWholeEntry(
        string hive, string keyPath, string name, RegistryValueKind kind) =>
        Assert.False(OfficialConsoleIsolation.IsRestorableRegistryEntry(hive, keyPath, name, kind));

    // ---- 计划任务范围 ----

    [Theory]
    [InlineData(@"\CCUWinUIStartup")]
    [InlineData(@"\OEM\GamingCenter")]
    [InlineData("CCUWinUIStartup")]
    public void VendorTaskPathsAreManageable(string path) =>
        Assert.True(OfficialConsoleIsolation.IsManageableTaskPath(path));

    /// <summary>
    /// 采集阶段遍历全机所有任务，只要参数里出现过标记字符串就会命中。
    /// 系统自带的任务必须无条件排除，否则以管理员运行时会关掉 Windows 维护任务。
    /// </summary>
    [Theory]
    [InlineData(@"\Microsoft\Windows\UpdateOrchestrator\Reboot")]
    [InlineData(@"\Microsoft\Windows\Defrag\ScheduledDefrag")]
    [InlineData(@"Microsoft\Windows\TaskScheduler\Maintenance")]
    [InlineData(@"/Microsoft/Windows/Foo")]
    [InlineData("")]
    [InlineData(null)]
    public void SystemTaskFoldersAreNeverTouched(string? path) =>
        Assert.False(OfficialConsoleIsolation.IsManageableTaskPath(path));

    // ---- 启动目录文件 ----

    [Fact]
    public void StartupFileRestoreOnlyUndoesOurOwnRename()
    {
        string folder = Path.Combine(Path.GetTempPath(), "L-Mechrevo-startup-" + Guid.NewGuid().ToString("N"));
        string original = Path.Combine(folder, "OfficialConsole.lnk");
        string disabled = original + OfficialConsoleIsolation.DisabledStartupSuffix;

        Assert.True(OfficialConsoleIsolation.IsRestorableStartupFile(original, disabled, [folder]));
    }

    /// <summary>
    /// 禁用路径必须正好是原路径加上我们的后缀。任意一对路径都能通过的话，
    /// 这就是一次管理员权限的任意文件移动。
    /// </summary>
    [Fact]
    public void ArbitraryFileMovePairsAreRejected()
    {
        string folder = Path.Combine(Path.GetTempPath(), "L-Mechrevo-startup-" + Guid.NewGuid().ToString("N"));
        string original = Path.Combine(folder, "OfficialConsole.lnk");

        Assert.False(OfficialConsoleIsolation.IsRestorableStartupFile(
            original, Path.Combine(folder, "somethingelse.bak"), [folder]));
        Assert.False(OfficialConsoleIsolation.IsRestorableStartupFile(
            @"C:\Windows\System32\drivers\etc\hosts",
            @"C:\Windows\System32\drivers\etc\hosts" + OfficialConsoleIsolation.DisabledStartupSuffix,
            [folder]));
    }

    /// <summary>原路径必须落在启动目录之内，且是直接子项。</summary>
    [Fact]
    public void PathsOutsideTheStartupFolderAreRejected()
    {
        string folder = Path.Combine(Path.GetTempPath(), "L-Mechrevo-startup-" + Guid.NewGuid().ToString("N"));
        string nested = Path.Combine(folder, "sub", "OfficialConsole.lnk");
        string traversal = Path.Combine(folder, "..", "OfficialConsole.lnk");

        Assert.False(OfficialConsoleIsolation.IsRestorableStartupFile(
            nested, nested + OfficialConsoleIsolation.DisabledStartupSuffix, [folder]));
        Assert.False(OfficialConsoleIsolation.IsRestorableStartupFile(
            traversal, traversal + OfficialConsoleIsolation.DisabledStartupSuffix, [folder]));
    }

    [Fact]
    public void EmptyOrNullStartupPathsAreRejected()
    {
        Assert.False(OfficialConsoleIsolation.IsRestorableStartupFile(null, null, [Path.GetTempPath()]));
        Assert.False(OfficialConsoleIsolation.IsRestorableStartupFile("", "", [Path.GetTempPath()]));
        Assert.False(OfficialConsoleIsolation.IsRestorableStartupFile(
            "a.lnk", "a.lnk" + OfficialConsoleIsolation.DisabledStartupSuffix, []));
    }

    // ---- 恢复结果的判定 ----

    /// <summary>
    /// 快照里有条目却一项都没恢复成功时不能报成功。过去只要不抛异常就返回
    /// true 和「已恢复」，即使实际恢复项数为 0 —— 用户以为恢复了，其实什么都没做。
    /// </summary>
    [Fact]
    public void RestoreOutcomeDetectsASilentNoOp()
    {
        Assert.True(new OfficialConsoleIsolation.RestoreOutcome(
            Restored: 0, Skipped: 3, Failed: 0, Expected: 3).IsSilentNoOp);
        Assert.True(new OfficialConsoleIsolation.RestoreOutcome(
            Restored: 0, Skipped: 0, Failed: 3, Expected: 3).IsSilentNoOp);
    }

    [Fact]
    public void RestoreOutcomeWithNothingToDoIsNotASilentNoOp() =>
        Assert.False(new OfficialConsoleIsolation.RestoreOutcome(
            Restored: 0, Skipped: 0, Failed: 0, Expected: 0).IsSilentNoOp);

    [Fact]
    public void RestoreOutcomeWithPartialSuccessIsNotASilentNoOp() =>
        Assert.False(new OfficialConsoleIsolation.RestoreOutcome(
            Restored: 1, Skipped: 1, Failed: 1, Expected: 3).IsSilentNoOp);
}
