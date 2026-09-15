using System.IO.Compression;
using System.Text;

namespace MechrevoLite.Diagnostics;

/// <summary>打包进诊断包的一个文件：源路径 + zip 内条目名 + 给 README 的一句话说明。</summary>
internal sealed record DiagnosticPackFile(string SourcePath, string EntryName, string Description = "");

/// <summary>导出诊断包所需的全部输入（由 <see cref="DiagnosticPackCapture"/> 从真实机器状态采集）。</summary>
internal sealed record DiagnosticPackInputs(
    string Version,
    string ReleaseLabel,
    string SystemInfoText,
    IReadOnlyList<DiagnosticPackFile> LogFiles,
    IReadOnlyList<DiagnosticPackFile> ConfigFiles);

/// <summary>导出结果：zip 路径、实际写入的条目列表（含目录条目）、总字节数。</summary>
internal sealed record DiagnosticPackResult(string ZipPath, IReadOnlyList<string> Entries, long SizeBytes);

/// <summary>
/// 「导出诊断包」的纯文件逻辑（无 UI、无网络）。
/// 把一个 zip 写到磁盘，包含系统信息、说明、日志与配置副本；
/// 任何单个文件缺失都只记进清单，不让整个导出失败。
/// </summary>
internal static class DiagnosticPackExporter
{
    internal const string SystemInfoEntry = "系统信息.txt";
    internal const string GuideEntry = "说明.txt";
    internal const string LogFolderEntry = "日志/";
    internal const string ConfigFolderEntry = "配置/";
    internal const string ConfigReadmeEntry = "配置/README-配置.txt";
    internal const string IssuesUrl = "https://github.com/LiangyuLu-lly/L-Mechrevo/issues";

    /// <summary>单个日志文件的打包上限；超过只保留末尾这么多字节并在清单里注明。</summary>
    internal const long DefaultMaxLogBytes = 8L * 1024 * 1024;

    static readonly UTF8Encoding Utf8Bom = new(encoderShouldEmitUTF8Identifier: true);

    internal static string BuildFileName(string version, DateTime timestamp) =>
        $"L-Mechrevo-诊断包-{version}-{timestamp:yyyyMMdd-HHmmss}.zip";

    internal static DiagnosticPackResult Export(
        string zipPath, DiagnosticPackInputs inputs, long maxLogBytes = DefaultMaxLogBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(zipPath);
        ArgumentNullException.ThrowIfNull(inputs);

        string fullPath = Path.GetFullPath(zipPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        if (File.Exists(fullPath)) File.Delete(fullPath);

        var entries = new List<string>();
        var manifest = new List<string>();

        using (var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            AddDirectory(zip, entries, LogFolderEntry);
            AddDirectory(zip, entries, ConfigFolderEntry);

            var logPlan = PlanLogs(inputs.LogFiles, maxLogBytes, manifest);
            var configPlan = PlanConfigs(inputs.ConfigFiles, manifest);

            WriteText(zip, entries, SystemInfoEntry,
                inputs.SystemInfoText.TrimEnd() + Environment.NewLine + Environment.NewLine +
                "【打包清单】" + Environment.NewLine + string.Join(Environment.NewLine, manifest));
            WriteText(zip, entries, GuideEntry, BuildGuide(inputs.Version));
            WriteText(zip, entries, ConfigReadmeEntry, BuildConfigReadme(inputs.ConfigFiles));

            foreach (var item in logPlan)
                CopyFile(zip, entries, item.File, item.CopyBytes);
            foreach (var item in configPlan)
                CopyFile(zip, entries, item.File, null);
        }

        long size = new FileInfo(fullPath).Length;
        return new DiagnosticPackResult(fullPath, entries, size);
    }

    internal static string BuildGuide(string version) =>
        $"""
        L-Mechrevo 诊断包
        =================
        本压缩包由 L-Mechrevo 的「导出诊断包」功能生成，用于排查问题。它包含：
          - 系统信息.txt：应用版本、Windows 版本、机型、CPU、GPU 与驱动、GCU 连通与能力位；
          - 日志\：应用自身的运行日志（log.txt）；
          - 配置\：应用的配置文件副本（含 README-配置.txt 说明每个文件是什么）。

        应用版本：{version}

        如何提交
        --------
        请把本压缩包作为附件，提交到 GitHub Issues：
        {IssuesUrl}
        或直接发给开发者。

        请在 Issue 里说明：
          1. 你做了什么（操作步骤，越具体越好）；
          2. 你期望发生什么；
          3. 实际发生了什么（报错原文、截图）。

        隐私说明
        --------
        本压缩包在本地生成，不会自动上传、不会联网发送；请自行确认内容后再提交。
        配置文件可能包含本机设备标识（例如已保存的液冷设备 MAC），介意请先检查。
        """;

    internal static string BuildConfigReadme(IReadOnlyList<DiagnosticPackFile> configs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("配置文件夹说明");
        sb.AppendLine("================");
        sb.AppendLine("本文件夹是 L-Mechrevo 配置文件的副本，用于复现问题时的设置差异。");
        sb.AppendLine("这些文件只包含设置项（模式、灯效、开关、窗口外观等），不含账号、密码或网络凭据。");
        sb.AppendLine("可能包含本机设备标识（例如已保存的液冷设备 MAC）。");
        sb.AppendLine();

        if (configs.Count == 0)
        {
            sb.AppendLine("（本次未采集到配置文件。）");
            return sb.ToString();
        }

        foreach (DiagnosticPackFile file in configs)
        {
            string leaf = file.EntryName.StartsWith(ConfigFolderEntry, StringComparison.Ordinal)
                ? file.EntryName[ConfigFolderEntry.Length..]
                : file.EntryName;
            string description = string.IsNullOrWhiteSpace(file.Description) ? "配置文件" : file.Description;
            string present = File.Exists(file.SourcePath) ? "已包含" : "缺失（源文件不存在）";
            sb.AppendLine($"- {leaf}：{description} — {present}");
        }
        return sb.ToString();
    }

    static List<(DiagnosticPackFile File, long? CopyBytes)> PlanLogs(
        IReadOnlyList<DiagnosticPackFile> logs, long maxLogBytes, List<string> manifest)
    {
        var plan = new List<(DiagnosticPackFile, long?)>();
        foreach (DiagnosticPackFile file in logs)
        {
            if (!File.Exists(file.SourcePath))
            {
                manifest.Add($"- {file.EntryName}：缺失（源文件不存在）");
                continue;
            }

            long length = new FileInfo(file.SourcePath).Length;
            if (maxLogBytes > 0 && length > maxLogBytes)
            {
                manifest.Add($"- {file.EntryName}：已包含（超过 {FormatSize(maxLogBytes)} 上限，已截断至末尾 " +
                    $"{FormatSize(maxLogBytes)}，原始 {FormatSize(length)}）");
                plan.Add((file, maxLogBytes));
            }
            else
            {
                manifest.Add($"- {file.EntryName}：已包含（{FormatSize(length)}）");
                plan.Add((file, null));
            }
        }
        return plan;
    }

    static List<(DiagnosticPackFile File, long? CopyBytes)> PlanConfigs(
        IReadOnlyList<DiagnosticPackFile> configs, List<string> manifest)
    {
        var plan = new List<(DiagnosticPackFile, long?)>();
        foreach (DiagnosticPackFile file in configs)
        {
            if (!File.Exists(file.SourcePath))
            {
                manifest.Add($"- {file.EntryName}：缺失（源文件不存在）");
                continue;
            }

            manifest.Add($"- {file.EntryName}：已包含（{FormatSize(new FileInfo(file.SourcePath).Length)}）");
            plan.Add((file, null));
        }
        return plan;
    }

    static void AddDirectory(ZipArchive zip, List<string> entries, string entryName)
    {
        zip.CreateEntry(entryName, CompressionLevel.NoCompression);
        entries.Add(entryName);
    }

    static void WriteText(ZipArchive zip, List<string> entries, string entryName, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        using var writer = new StreamWriter(entryStream, Utf8Bom);
        writer.Write(text);
        entries.Add(entryName);
    }

    static void CopyFile(ZipArchive zip, List<string> entries, DiagnosticPackFile file, long? copyBytes)
    {
        using var source = new FileStream(file.SourcePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (copyBytes is > 0 && source.Length > copyBytes)
            source.Seek(source.Length - copyBytes.Value, SeekOrigin.Begin);

        ZipArchiveEntry entry = zip.CreateEntry(file.EntryName, CompressionLevel.Fastest);
        using var destination = entry.Open();
        source.CopyTo(destination);
        entries.Add(file.EntryName);
    }

    static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024) return $"{bytes / 1024D / 1024D:F1} MB";
        if (bytes >= 1024) return $"{bytes / 1024D:F0} KB";
        return $"{bytes} B";
    }
}
