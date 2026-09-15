using MechrevoLite.Helpers;

namespace MechrevoLite.Update;

/// <summary>
/// 更新链路自测（<c>--updatetest [baseUrl]</c>，诊断模式，供真机验证与排查）。
///
/// 走的是与更新窗口按钮**完全相同**的四个函数：CheckAsync → DownloadAsync → Verify →
/// ExtractPackage → StartUpdater，只是没有那几个按钮和确认框，便于反复跑：
/// <code>
/// L-Mechrevo.exe --updatetest                      # 打真实服务器，只看检测结果
/// L-Mechrevo.exe --updatetest http://127.0.0.1:8899  # 打本地桩服务器，跑完整安装链
/// </code>
/// 退出码：0 正常（含"已是最新"）、2 校验/下载失败、3 没有可用下载地址。
/// 每一步都会打印"接受/拒绝 + 原因"，发布者可用它自查静态 JSON 是否合格。
/// </summary>
internal static class UpdateSelfTest
{
    internal static int Run(string[] args)
    {
        string? baseUrl = args.Length > 0 ? args[0] : null;
        // 诊断模式必须动真格：解压 zip 前要能打开压缩库，且不走 UI。
        if (!string.IsNullOrWhiteSpace(baseUrl)) UpdateChecker.BaseUrlOverride = baseUrl;

        Logger.WriteLine(
            $"===== 更新自测开始：基址={UpdateChecker.BaseUrl} 本机版本={Program.ReleaseVersion} 自动检测开关={UpdateChecker.AutoCheckEnabled}");

        UpdateInfo? info = UpdateChecker.CheckAsync(force: true).GetAwaiter().GetResult();
        if (info is null)
        {
            Logger.WriteLine("更新自测：决定=拒绝（检测失败：网络或响应不合法）");
            Console.WriteLine("check failed");
            return 2;
        }

        Console.WriteLine($"current={info.CurrentVersion} latest={info.LatestVersion} available={info.UpdateAvailable}");
        Logger.WriteLine(
            $"更新自测：本机={info.CurrentVersion} 服务端={info.LatestVersion ?? "(无)"} " +
            $"服务端上报 current_version={info.ServerCurrentVersion ?? "(无)"} 有更新={info.UpdateAvailable}");
        if (!info.UpdateAvailable)
        {
            // 无更新时**不要求** sha256：静态 JSON 可以不带校验值，只要不实际下载就不算错。
            Logger.WriteLine("更新自测：决定=无更新（已是最新，或服务端未提供比本机更新的版本）");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            Logger.WriteLine("更新自测：决定=不自动安装（服务端没有 download_url，引导下载页）");
            return 3;
        }

        // fail-closed：协议 + host 白名单 + 必备 sha256。
        if (!UpdatePolicy.TryAcceptOffer(info, out string reason))
        {
            Logger.WriteLine($"更新自测：决定=拒绝下载（{reason}）");
            Console.WriteLine($"refused: {reason}");
            return 2;
        }

        Logger.WriteLine("更新自测：决定=接受，开始下载并强校验");
        string? package = UpdateInstaller.DownloadAsync(info).GetAwaiter().GetResult();
        if (package is null)
        {
            Logger.WriteLine("更新自测：决定=拒绝（下载失败）");
            return 2;
        }

        PackageVerification verification = UpdateInstaller.Verify(package, info);
        Logger.WriteLine($"更新自测：校验 {(verification.Ok ? "通过" : "失败")} —— {verification.Reason}");
        if (!verification.Ok)
        {
            UpdateInstaller.DiscardPackage(package);
            Logger.WriteLine("更新自测：决定=拒绝（校验失败，临时包已删除）");
            return 2;
        }

        string? newExe = UpdateInstaller.ExtractPackage(package, info);
        if (newExe is null)
        {
            Logger.WriteLine("更新自测：决定=拒绝（解压失败）");
            return 2;
        }

        string target = UpdateInstaller.CurrentExePath;
        if (!UpdateInstaller.StartUpdater(newExe, target))
        {
            Logger.WriteLine("更新自测：决定=拒绝（更新器启动失败）");
            return 2;
        }

        Logger.WriteLine($"更新自测：决定=接受并安装（{newExe} -> {target}），本进程退出");
        Logger.Close();
        return 0;
    }
}
