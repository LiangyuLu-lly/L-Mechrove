using System.Text;
using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// 官方控制台的识别范围。这里每一个误判都会直接改动用户系统：
/// 删掉一个开机启动项、禁用一个计划任务、把一个文件改名。
/// </summary>
public class IsolationMatchingTests
{
    static string NewStartupFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "L-Mechrevo-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    static string WriteFile(string folder, string name, string content, Encoding? encoding = null)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, content, encoding ?? Encoding.ASCII);
        return path;
    }

    // ---- 文件名匹配 ----

    [Theory]
    [InlineData("CCUWinUI.exe")]
    [InlineData("ccuwinui.exe")]
    [InlineData("SystrayComponent.exe")]
    [InlineData("ControlCenterU.exe")]
    [InlineData("GamingCenterU.exe")]
    [InlineData("GCUUI.exe")]
    public void OfficialExecutableNamesAreRecognised(string fileName)
    {
        string folder = NewStartupFolder();
        try
        {
            string path = WriteFile(folder, fileName, "not scanned");
            Assert.True(OfficialConsoleIsolation.IsOfficialStartupFile(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>
    /// 收紧标记表的核心目的：裸名 "CCUWinUI" / "SystrayComponent" 不再单独构成证据。
    /// 这类名字出现在无关的备份项、日志目录、第三方脚本里非常常见。
    /// </summary>
    [Theory]
    [InlineData("CCUWinUI-backup.txt")]
    [InlineData("SystrayComponent-notes.md")]
    [InlineData("MyTool.exe")]
    [InlineData("readme.txt")]
    public void BareNamesAndUnrelatedFilesAreNotRecognised(string fileName)
    {
        string folder = NewStartupFolder();
        try
        {
            string path = WriteFile(folder, fileName, "nothing official here");
            Assert.False(OfficialConsoleIsolation.IsOfficialStartupFile(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    // ---- 内容扫描的范围 ----

    /// <summary>快捷方式和脚本的内容里写着目标路径，扫描它们是有意义的。</summary>
    [Theory]
    [InlineData("start.cmd")]
    [InlineData("launch.bat")]
    [InlineData("shortcut.lnk")]
    [InlineData("link.url")]
    public void ShortcutAndScriptContentsAreScanned(string fileName)
    {
        string folder = NewStartupFolder();
        try
        {
            string path = WriteFile(folder, fileName, @"start C:\Program Files\OEM\CCUWinUI.exe");
            Assert.True(OfficialConsoleIsolation.IsOfficialStartupFile(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>UTF-16 编码的快捷方式内容同样要能识别。</summary>
    [Fact]
    public void Utf16ShortcutContentIsScanned()
    {
        string folder = NewStartupFolder();
        try
        {
            string path = WriteFile(folder, "shortcut.lnk", @"C:\OEM\GamingCenterU.exe", Encoding.Unicode);
            Assert.True(OfficialConsoleIsolation.IsOfficialStartupFile(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>
    /// 任意可执行文件不再被整体扫描内容：过去一个大 EXE 里恰好出现过标记字符串
    /// 就会被改名，而且会产生数倍文件大小的临时字符串。
    /// </summary>
    [Fact]
    public void ArbitraryExecutableContentIsNotScanned()
    {
        string folder = NewStartupFolder();
        try
        {
            string path = WriteFile(folder, "ThirdParty.exe", "embedded reference to CCUWinUI.exe inside");
            Assert.False(OfficialConsoleIsolation.IsOfficialStartupFile(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>超过大小上限的文件不做内容扫描。</summary>
    [Fact]
    public void OversizedScannableFilesAreSkipped()
    {
        string folder = NewStartupFolder();
        try
        {
            string path = Path.Combine(folder, "huge.cmd");
            var content = new StringBuilder(2 * 1024 * 1024);
            content.Append('x', 2 * 1024 * 1024);
            content.Append("CCUWinUI.exe");
            File.WriteAllText(path, content.ToString(), Encoding.ASCII);

            Assert.False(OfficialConsoleIsolation.IsOfficialStartupFile(path));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void MissingFileIsNotRecognised() =>
        Assert.False(OfficialConsoleIsolation.IsOfficialStartupFile(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".cmd")));

    // ---- 计划任务范围（与恢复策略共用同一个判定）----

    [Fact]
    public void SystemTaskFoldersAreExcludedFromCaptureAndRestore()
    {
        Assert.False(OfficialConsoleIsolation.IsManageableTaskPath(@"\Microsoft\Windows\Defrag\ScheduledDefrag"));
        Assert.True(OfficialConsoleIsolation.IsManageableTaskPath(@"\CCUWinUIStartup"));
    }
}
