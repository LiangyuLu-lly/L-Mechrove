namespace MechrevoLite.Diagnostics;

/// <summary>
/// 从真实运行状态采集诊断包输入：应用版本、系统信息、应用日志、配置文件。
/// 只读本机；路径取自 Logger / AppConfig 的真实解析结果。
/// </summary>
internal static class DiagnosticPackCapture
{
    internal static DiagnosticPackInputs Capture()
    {
        var logs = new List<DiagnosticPackFile>
        {
            new(Logger.logFile,
                DiagnosticPackExporter.LogFolderEntry + Path.GetFileName(Logger.logFile),
                "应用运行日志（追加写入，超过 2 MB 时只保留末尾 1 MB）"),
        };

        List<DiagnosticPackFile> configs = AppConfig.ExportableConfigFiles()
            .Select(file => new DiagnosticPackFile(
                file.SourcePath,
                DiagnosticPackExporter.ConfigFolderEntry + file.FileName,
                file.Description))
            .ToList();

        return new DiagnosticPackInputs(
            Program.ReleaseVersion,
            Program.ReleaseLabel,
            DiagnosticSystemInfo.Build(),
            logs,
            configs);
    }
}
