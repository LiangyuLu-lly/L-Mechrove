using MechrevoLite.Helpers;
using System.Diagnostics;
using System.IO.Compression;
using System.Windows.Forms;
using System.Security.Cryptography;

namespace MechrevoLite.Update;

/// <summary>更新包校验结果。<see cref="Ok"/> 为 false 时 <see cref="Reason"/> 是给用户看的原因。</summary>
internal sealed record PackageVerification(bool Ok, string Reason);

/// <summary>
/// 更新包的下载、校验、解压与替换安装。
///
/// 安全边界（2026-09-11 与服务端现状对齐）：
/// <list type="bullet">
/// <item>服务端目前**不给 sha256**（网盘发布），所以"校验"只能做到：HTTPS、体积上限、
/// 是不是合法 zip、包内有没有预期的 exe。**这些都不能证明来源可信**；</item>
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
    /// 下载更新包到临时目录，边下边算 SHA-256。失败返回 null（调用方提示改用下载页）。
    /// </summary>
    internal static async Task<string?> DownloadAsync(UpdateInfo info, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl)) return null;
        if (!Uri.TryCreate(info.DownloadUrl, UriKind.Absolute, out Uri? uri)) return null;
        // 只接受 https（本地联调桩服务器允许回环 http）。
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            Logger.WriteLine($"更新包下载被拒：非 HTTPS 地址 {uri.Scheme}");
            return null;
        }

        string directory = Path.Combine(TempRoot, info.LatestVersion ?? "unknown");
        Directory.CreateDirectory(directory);
        string filePath = Path.Combine(directory, PackageFileName(info));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(UpdateHttp.DownloadTimeout);
        try
        {
            using var response = await UpdateHttp.Download
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            long? contentLength = response.Content.Headers.ContentLength;
            if (contentLength is long declared && declared > UpdateChecker.MaxPackageBytes)
            {
                Logger.WriteLine($"更新包过大（声明 {declared} 字节），已放弃");
                return null;
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using var target = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
            byte[] buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > UpdateChecker.MaxPackageBytes)
                {
                    Logger.WriteLine("更新包超过体积上限，已中止下载");
                    target.Close();
                    TryDelete(filePath);
                    return null;
                }
                await target.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                if (contentLength is long length && length > 0)
                    progress?.Report((int)Math.Clamp(total * 100 / length, 0, 100));
            }
            progress?.Report(100);
            Logger.WriteLine($"更新包已下载：{filePath}（{total} 字节）");
            return filePath;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"更新包下载失败：{ex.GetType().Name} {ex.Message}");
            TryDelete(filePath);
            return null;
        }
    }

    /// <summary>能验的都验：sha256（服务端给了就强校验）、大小合理性、zip 合法性、包内含预期 exe。</summary>
    internal static PackageVerification Verify(string filePath, UpdateInfo info)
    {
        if (!File.Exists(filePath)) return new PackageVerification(false, "下载文件不存在");

        long size = new FileInfo(filePath).Length;
        if (size <= 0) return new PackageVerification(false, "下载文件为空");
        if (info.Size is long declaredSize && declaredSize > 0 && declaredSize != size)
            return new PackageVerification(false, $"文件大小与服务器声明不符（{size} ≠ {declaredSize}）");

        if (!string.IsNullOrWhiteSpace(info.Sha256))
        {
            string actual = ComputeSha256(filePath);
            if (!string.Equals(actual, info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Logger.WriteLine($"更新包 SHA-256 不匹配：期望 {info.Sha256} 实际 {actual}");
                return new PackageVerification(false, "更新包校验失败（SHA-256 不匹配）");
            }
        }
        else
        {
            // 没有哈希时不做任何"看起来通过"的假校验，只记日志，由调用方提示用户。
            Logger.WriteLine("更新包未提供 SHA-256（服务端网盘发布），只能做结构校验");
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

        return new PackageVerification(true, string.IsNullOrWhiteSpace(info.Sha256)
            ? "未提供校验值，仅通过结构检查"
            : "SHA-256 校验通过");
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

    static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
