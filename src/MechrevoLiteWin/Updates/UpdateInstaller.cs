using MechrevoLite.Helpers;
using System.Diagnostics;
using System.IO.Compression;
using System.Windows.Forms;
using System.Security.Cryptography;

namespace MechrevoLite.Update;

/// <summary>更新包校验结果。<see cref="Ok"/> 为 false 时 <see cref="Reason"/> 是给用户看的原因。</summary>
internal sealed record PackageVerification(bool Ok, string Reason);

/// <summary>一次下载尝试的结果：成功时 <see cref="Path"/> 非空；失败时 <see cref="Reason"/> 是给用户看的原因。</summary>
internal sealed record DownloadResult(string? Path, string? Reason);

/// <summary>
/// 更新包的下载、校验、解压与替换安装。
///
/// 安全边界（静态 OSS 后端，fail-closed）：
/// <list type="bullet">
/// <item>下载前必须通过 <see cref="UpdatePolicy"/>：https（回环例外）、host 白名单、sha256 合法；</item>
/// <item>下载后必须与 sha256（以及 size，若提供）严格匹配，再校验 zip 与包内 exe —— 全部通过才允许安装；</item>
/// <item>校验失败会删除临时包并中止，运行中的程序与用户配置保持不变；</item>
/// <item>安装只发生在用户明确点击"下载并安装"之后，绝不静默执行；</item>
/// <item>替换前先备份旧 exe，失败自动回滚；更新器只复制 exe，不碰同目录的 config.json（用户配置）。</item>
/// </list>
/// </summary>
internal static class UpdateInstaller
{
    internal const string ExpectedExePrefix = "L-Mechrevo";

    internal static string TempRoot => Path.Combine(Path.GetTempPath(), "L-Mechrevo-update");

    /// <summary>当前正在运行的 exe（单文件发布下 <c>Assembly.Location</c> 为空，必须用 ProcessPath）。</summary>
    internal static string CurrentExePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    internal static string PackageFileName(UpdateInfo info)
    {
        // 服务端给的 filename 不可信（可能是 ..\..\ 之类），只取它最后一个路径段做提示。
        string? name = info.FileName is { Length: > 0 } ? Path.GetFileName(info.FileName) : null;
        if (string.IsNullOrWhiteSpace(name))
            name = $"L-Mechrevo-{info.LatestVersion ?? "update"}.zip";
        string safe = string.Join('_', name.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        return safe.Length > 0 ? safe : "L-Mechrevo-update.zip";
    }

    /// <summary>
    /// 下载更新包到临时目录，边下边算 SHA-256。
    ///
    /// 每次尝试写入独立子目录（<c>TempRoot/&lt;版本&gt;/attempt-&lt;guid&gt;/</c>），并顺手清理历史
    /// 残留：旧尝试或旧版本留下的锁定/损坏文件不会再让后续重试永久失败。写入句柄在离开
    /// 块作用域时必然释放，删除路径绝不会撞上自己还开着的文件。失败时
    /// <see cref="DownloadResult.Reason"/> 是可直接展示给用户的原因。
    /// </summary>
    internal static async Task<DownloadResult> DownloadAsync(UpdateInfo info, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
            return new DownloadResult(null, "服务端没有提供下载地址");
        if (!UpdatePolicy.TryAcceptDownloadUrl(info.DownloadUrl, out Uri? uri, out string urlReason))
        {
            Logger.WriteLine($"更新包下载被拒：{urlReason}");
            return new DownloadResult(null, urlReason);
        }
        // 没有合法 sha256 就绝不下载：静态后端下无法确认来源与完整性。
        if (!UpdatePolicy.IsValidSha256(info.Sha256))
        {
            Logger.WriteLine("更新包下载被拒：服务端未提供有效的 SHA-256 校验值");
            return new DownloadResult(null, "服务端未提供有效的 SHA-256 校验值，拒绝下载");
        }

        string attemptDirectory = Path.Combine(
            TempRoot, info.LatestVersion ?? "unknown", "attempt-" + Guid.NewGuid().ToString("N"));
        string filePath = Path.Combine(attemptDirectory, PackageFileName(info));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(UpdateHttp.DownloadTimeout);
        try
        {
            Directory.CreateDirectory(attemptDirectory);
            CleanStaleTempArtifacts(attemptDirectory);

            using var response = await UpdateHttp.Download
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is long declared && declared > UpdateChecker.MaxPackageBytes)
            {
                Logger.WriteLine($"更新包过大（声明 {declared} 字节），已放弃");
                return new DownloadResult(null, $"更新包超过体积上限（声明 {declared} 字节）");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            long total = 0;
            bool tooLarge = false;
            // 显式块作用域：写出句柄在这里退出时释放；后面的删除/失败处理不会自锁。
            await using (var target = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > UpdateChecker.MaxPackageBytes)
                    {
                        tooLarge = true;
                        break;
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    if (contentLength is long length && length > 0)
                        progress?.Report((int)Math.Clamp(total * 100 / length, 0, 100));
                }
            }

            if (tooLarge)
            {
                Logger.WriteLine("更新包超过体积上限，已中止下载");
                DeleteFileLogged(filePath);
                return new DownloadResult(null, "更新包超过体积上限，已中止下载");
            }

            progress?.Report(100);
            Logger.WriteLine($"更新包已下载：{filePath}（{total} 字节）");
            return new DownloadResult(filePath, null);
        }
        catch (OperationCanceledException)
        {
            Logger.WriteLine("更新包下载已取消（超时或调用方取消）");
            DeleteFileLogged(filePath);
            return new DownloadResult(null, "下载已取消或超时");
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"更新包下载失败：{ex.GetType().Name} {ex.Message}");
            DeleteFileLogged(filePath);
            return new DownloadResult(null, "下载失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 强校验：sha256 必备且必须匹配（缺失/非法直接拒绝），大小声明必须一致，
    /// 且必须是含预期 exe 的合法 zip。解析/提取之前调用；失败一律不允许继续安装。
    /// </summary>
    internal static PackageVerification Verify(string filePath, UpdateInfo info)
    {
        if (!File.Exists(filePath)) return new PackageVerification(false, "下载文件不存在");

        if (!UpdatePolicy.IsValidSha256(info.Sha256))
            return new PackageVerification(false, "服务端未提供有效的 SHA-256 校验值，拒绝安装");

        long size = new FileInfo(filePath).Length;
        if (size <= 0) return new PackageVerification(false, "下载文件为空");
        if (info.Size is long declaredSize && declaredSize > 0 && declaredSize != size)
            return new PackageVerification(false, $"文件大小与服务器声明不符（{size} ≠ {declaredSize}）");

        string actual = ComputeSha256(filePath);
        if (!string.Equals(actual, info.Sha256!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Logger.WriteLine($"更新包 SHA-256 不匹配：期望 {info.Sha256} 实际 {actual}");
            return new PackageVerification(false, "更新包校验失败（SHA-256 不匹配）");
        }

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(filePath);
            if (archive.Entries.Count == 0) return new PackageVerification(false, "更新包是空的");
            if (FindPackageExe(archive) is null)
                return new PackageVerification(false, $"更新包里没有找到 {ExpectedExePrefix}*.exe");
        }
        catch (Exception ex)
        {
            return new PackageVerification(false, "更新包不是有效的 zip：" + ex.Message);
        }

        return new PackageVerification(true, "SHA-256 校验通过");
    }

    /// <summary>校验失败时删除临时包，避免把已知被篡改/损坏的文件留在盘上。</summary>
    internal static void DiscardPackage(string? packagePath)
    {
        if (string.IsNullOrWhiteSpace(packagePath)) return;
        TryDelete(packagePath);
        Logger.WriteLine($"临时更新包已删除：{packagePath}");
    }

    /// <summary>解压到临时目录并返回包内 exe 的绝对路径；失败返回 null。</summary>
    internal static string? ExtractPackage(string zipPath, UpdateInfo info)
    {
        string directory = Path.Combine(TempRoot, info.LatestVersion ?? "unknown", "extracted");
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);
            ZipFile.ExtractToDirectory(zipPath, directory);

            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            ZipArchiveEntry? entry = FindPackageExe(archive);
            if (entry is null) return null;

            string candidate = Path.GetFullPath(Path.Combine(directory, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            // 防 zip-slip：解压结果必须仍在目标目录内（ExtractToDirectory 已处理，这里再兜一次底）。
            if (!candidate.StartsWith(Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return null;
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("更新包解压失败：" + ex.Message);
            return null;
        }
    }

    static ZipArchiveEntry? FindPackageExe(ZipArchive archive) =>
        archive.Entries
            .Where(e => e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .Where(e => e.Name.StartsWith(ExpectedExePrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Count(c => c is '/' or '\\'))
            .FirstOrDefault();

    /// <summary>
    /// 当前是否支持自更新。更新器只复制 exe——这对**单文件发布**是完整的（发布形态就是单个 exe），
    /// 但框架依赖构建（exe 只是个 apphost，旁边还有 L-Mechrevo.dll）只换 exe 会让程序起不来
    /// （真机实测：更新器进程因缺 dll 静默退出，用户看到"点了没反应"）。
    /// 这种情况明确拒绝自更新，让用户走下载页手动替换。
    /// </summary>
    internal static bool CanSelfInstall(out string reason) =>
        CanSelfInstall(CurrentExePath, File.Exists, out reason);

    internal static bool CanSelfInstall(string exePath, Func<string, bool> fileExists, out string reason)
    {
        string? directory = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(directory))
        {
            reason = "找不到程序目录";
            return false;
        }
        string appHostCompanion = Path.Combine(directory, Path.GetFileNameWithoutExtension(exePath) + ".dll");
        if (fileExists(appHostCompanion))
        {
            reason = "当前是框架依赖构建（程序目录里有同名 dll），自更新会破坏安装，请用下载页手动替换";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>
    /// 启动更新器：把自己复制到临时目录，以 <c>--apply-update</c> 模式运行，然后由调用方退出主程序。
    /// 更新器等本进程退出后才替换文件，所以不会遇到"文件被占用"。
    /// </summary>
    internal static bool StartUpdater(string newExePath, string targetExePath)
    {
        try
        {
            string updaterDirectory = Path.Combine(TempRoot, "updater");
            Directory.CreateDirectory(updaterDirectory);
            string updaterPath = Path.Combine(updaterDirectory, Path.GetFileName(CurrentExePath) ?? "L-Mechrevo.exe");
            File.Copy(CurrentExePath, updaterPath, overwrite: true);

            var startInfo = new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = updaterDirectory,
            };
            startInfo.ArgumentList.Add("--apply-update");
            startInfo.ArgumentList.Add(newExePath);
            startInfo.ArgumentList.Add(targetExePath);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());

            Process? process = Process.Start(startInfo);
            if (process is null) return false;
            Logger.WriteLine($"更新器已启动（pid {process.Id}），准备替换 {targetExePath}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("更新器启动失败：" + ex.Message);
            return false;
        }
    }

    /// <summary>
    /// 更新器模式（<c>--apply-update &lt;新 exe&gt; &lt;目标 exe&gt; &lt;等待的 pid&gt;</c>）。
    /// 返回进程退出码：0 成功、2 失败（已尽量回滚）。
    /// </summary>
    internal static int RunUpdater(string[] args)
    {
        if (args.Length < 3) return 2;
        string newExe = args[0];
        string targetExe = args[1];
        int waitFor = int.TryParse(args[2], out int pid) ? pid : -1;

        try
        {
            WaitForExit(waitFor, TimeSpan.FromSeconds(90));
            if (!File.Exists(newExe)) return 2;

            string backup = targetExe + ".bak";
            TryDelete(backup);
            if (File.Exists(targetExe)) File.Move(targetExe, backup);

            try
            {
                File.Copy(newExe, targetExe, overwrite: true);
                if (new FileInfo(newExe).Length != new FileInfo(targetExe).Length)
                    throw new IOException("替换后大小不一致");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("替换 exe 失败，尝试回滚：" + ex.Message);
                TryDelete(targetExe);
                if (File.Exists(backup)) File.Move(backup, targetExe);
                Restart(targetExe);
                return 2;
            }

            Logger.WriteLine("更新完成：" + targetExe);
            Restart(targetExe);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("更新器异常：" + ex.Message);
            return 2;
        }
    }

    static void WaitForExit(int pid, TimeSpan timeout)
    {
        if (pid <= 0) return;
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                Logger.WriteLine("等待原进程退出超时，仍继续替换");
        }
        catch (ArgumentException)
        {
            // 进程已经不存在了 —— 正常情况。
        }
        // 再给文件句柄一点释放时间（Windows 上刚退出的进程偶尔还占着映像）。
        Thread.Sleep(1200);
    }

    static void Restart(string exePath)
    {
        try
        {
            if (File.Exists(exePath))
                Process.Start(new ProcessStartInfo { FileName = exePath, UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exePath) ?? "" });
        }
        catch (Exception ex)
        {
            Logger.WriteLine("更新后重启失败：" + ex.Message);
        }
    }

    internal static string ComputeSha256(string filePath)
    {
        using FileStream stream = File.OpenRead(filePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    /// <summary>
    /// 清理历史下载残留：其他版本的目录、当前版本目录里除本次尝试之外的内容。保留
    /// <paramref name="activeAttemptDirectory"/>（正在写入）与 <c>TempRoot\updater</c>
    /// （更新器进程正在使用）。删除是 best-effort，但删不掉的会逐条记录，绝不静默吞掉。
    /// </summary>
    internal static void CleanStaleTempArtifacts(string activeAttemptDirectory)
    {
        if (!Directory.Exists(TempRoot)) return;
        string active = FullPathNoTrailingSeparator(activeAttemptDirectory);
        string? activeVersionDirectory = Path.GetDirectoryName(active);
        string updaterDirectory = Path.Combine(TempRoot, "updater");

        foreach (string versionDirectory in GetDirectoriesOrEmpty(TempRoot))
        {
            if (IsSamePath(versionDirectory, updaterDirectory)) continue;

            if (activeVersionDirectory is null || !IsSamePath(versionDirectory, activeVersionDirectory))
            {
                DeleteDirectoryLogged(versionDirectory);
                continue;
            }

            // 当前版本的目录：本次尝试目录之外的内容都已过期（含老实现的固定路径包）。
            foreach (string child in GetDirectoriesOrEmpty(versionDirectory))
                if (!IsSamePath(child, active)) DeleteDirectoryLogged(child);
            foreach (string file in GetFilesOrEmpty(versionDirectory))
                DeleteFileLogged(file);
        }

        foreach (string file in GetFilesOrEmpty(TempRoot))
            DeleteFileLogged(file);
    }

    static string FullPathNoTrailingSeparator(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    static bool IsSamePath(string left, string right) =>
        string.Equals(FullPathNoTrailingSeparator(left), FullPathNoTrailingSeparator(right), StringComparison.OrdinalIgnoreCase);

    static string[] GetDirectoriesOrEmpty(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch (Exception ex)
        {
            Logger.WriteLine($"更新临时目录清理：无法枚举 {root} —— {ex.GetType().Name} {ex.Message}");
            return Array.Empty<string>();
        }
    }

    static string[] GetFilesOrEmpty(string root)
    {
        try { return Directory.GetFiles(root); }
        catch (Exception ex)
        {
            Logger.WriteLine($"更新临时目录清理：无法枚举 {root} —— {ex.GetType().Name} {ex.Message}");
            return Array.Empty<string>();
        }
    }

    static string[] GetFilesRecursiveOrEmpty(string root)
    {
        try { return Directory.GetFiles(root, "*", SearchOption.AllDirectories); }
        catch (Exception ex)
        {
            Logger.WriteLine($"更新临时目录清理：无法枚举 {root} —— {ex.GetType().Name} {ex.Message}");
            return Array.Empty<string>();
        }
    }

    static void DeleteDirectoryLogged(string directory)
    {
        foreach (string file in GetFilesRecursiveOrEmpty(directory))
            DeleteFileLogged(file);
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"更新临时目录清理：无法删除 {directory} —— {ex.GetType().Name} {ex.Message}");
        }
    }

    static void DeleteFileLogged(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex)
        {
            Logger.WriteLine($"临时文件删除失败：{path} —— {ex.GetType().Name} {ex.Message}");
        }
    }

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex)
        {
            Logger.WriteLine($"临时文件删除失败：{path} —— {ex.GetType().Name} {ex.Message}");
        }
    }
}
