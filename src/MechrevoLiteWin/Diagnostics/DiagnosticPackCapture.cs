namespace MechrevoLite.Diagnostics;

/// <summary>
/// 从真实运行状态采集诊断包输入：应用版本、系统信息、应用日志、配置文件。
/// 只读本机；路径取自 Logger / AppConfig 的真实解析结果。
/// </summary>
internal static class DiagnosticPackCapture
{
    internal static DiagnosticPackInputs Capture()
    {
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
            BuildLogFiles(),
            configs,
            CrashRingBufferText: Logger.SnapshotRingBuffer(),
            LogLevel: Logger.CurrentLevel.ToString());
    }

    /// <summary>
    /// 日志类条目：运行日志 + 崩溃现场。日志级别 OFF 时 log.txt 不存在，crash.txt 与
    /// 内存环形缓冲快照（见 DiagnosticPackInputs.CrashRingBufferText）就是唯一证据。
    /// </summary>
    internal static List<DiagnosticPackFile> BuildLogFiles() => new()
    {
        new(Logger.logFile,
            DiagnosticPackExporter.LogFolderEntry + Path.GetFileName(Logger.logFile),
            "应用运行日志（日志级别 OFF 时不存在；超过 10 MB 时保留末尾 1 MB）"),
        new(Logger.crashFile,
            DiagnosticPackExporter.LogFolderEntry + Path.GetFileName(Logger.crashFile),
            "崩溃现场日志（进程异常退出时落盘的内存环形缓冲）"),
    };
}
