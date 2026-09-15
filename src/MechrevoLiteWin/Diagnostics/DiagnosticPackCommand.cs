using System.Diagnostics;

namespace MechrevoLite.Diagnostics;

/// <summary>
/// 「导出诊断包」的 UI 入口：标准保存对话框 → 线程池打包 → 成功提示（可打开所在文件夹）。
/// 不含任何网络调用；目的地不可写时只报错，绝不阻塞程序。
/// </summary>
internal static class DiagnosticPackCommand
{
    /// <summary>自动化/测试旁路：置为非空路径时跳过保存对话框，直接导出到该路径。</summary>
    internal const string DestinationOverrideVariable = "LMECHREVO_DIAGNOSTIC_PACK_PATH";

    /// <summary>返回导出的 zip 路径；用户取消或导出失败时返回 null。</summary>
    internal static async Task<string?> RunAsync(Form owner, Control? trigger)
    {
        string? destination = ResolveDestination(owner);
        if (destination is null) return null;

        if (trigger is not null && !trigger.IsDisposed) trigger.Enabled = false;
        try
        {
            DiagnosticPackInputs inputs = DiagnosticPackCapture.Capture();
            DiagnosticPackResult result = await DiagnosticPackRunner.ExportAsync(destination, inputs);
            ShowSuccess(owner, result);
            return result.ZipPath;
        }
        catch (Exception ex)
        {
            // 目的地不可写（只读盘、权限不足、路径非法）时必须报告而不是卡住整个程序。
            Logger.WriteLine("诊断包导出失败: " + ex);
            MessageBox.Show(owner,
                "导出诊断包失败：\n" + ex.Message,
                "L-Mechrevo", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return null;
        }
        finally
        {
            if (trigger is not null && !trigger.IsDisposed) trigger.Enabled = true;
        }
    }

    static string? ResolveDestination(Form owner)
    {
        string? overridden = Environment.GetEnvironmentVariable(DestinationOverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden)) return overridden;

        using var dialog = new SaveFileDialog
        {
            Title = "导出诊断包",
            Filter = "Zip 压缩包 (*.zip)|*.zip",
            DefaultExt = "zip",
            AddExtension = true,
            OverwritePrompt = true,
            RestoreDirectory = true,
            FileName = DiagnosticPackExporter.BuildFileName(Program.ReleaseVersion, DateTime.Now),
            InitialDirectory = ResolveDefaultDirectory(),
        };
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.FileName : null;
    }

    /// <summary>默认保存目录：桌面 → 下载 → 文档 → 程序目录（取第一个真实存在者）。</summary>
    static string ResolveDefaultDirectory()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (Directory.Exists(desktop)) return desktop;

        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) return downloads;

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (Directory.Exists(documents)) return documents;

        return AppContext.BaseDirectory;
    }

    static void ShowSuccess(Form owner, DiagnosticPackResult result)
    {
        DialogResult open = MessageBox.Show(owner,
            $"诊断包已导出：\n{result.ZipPath}\n\n" +
            $"大小 {FormatSize(result.SizeBytes)}，共 {result.Entries.Count} 个条目。\n\n" +
            "是否打开所在文件夹？",
            "L-Mechrevo", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (open == DialogResult.Yes) RevealInExplorer(result.ZipPath);
    }

    internal static void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.WriteLine("打开所在文件夹失败: " + ex.Message);
        }
    }

    static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024) return $"{bytes / 1024D / 1024D:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024D:F0} KB";
        return $"{bytes} B";
    }
}
