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
/// </summary>
internal static class UpdateSelfTest
{
    internal static int Run(string[] args)
    {
        string? baseUrl = args.Length > 0 ? args[0] : null;
        // 诊断模式必须动真格：解压 zip 前要能打开压缩库，且不走 UI。
        if (!string.IsNullOrWhiteSpace(baseUrl)) UpdateChecker.BaseUrlOverride = baseUrl;

        Logger.WriteLine($"===== 更新自测开始：基址={UpdateChecker.BaseUrl} 本地版本={Program.ReleaseLabel} 自动检测开关={UpdateChecker.AutoCheckEnabled}");

        UpdateInfo? info = UpdateChecker.CheckAsync(force: true).GetAwaiter().GetResult();
        if (info is null)
        {
            Logger.WriteLine("更新自测：检测失败（网络或响应异常）");
            Console.WriteLine("check failed");
            return 2;
        }

        Console.WriteLine($"current={info.CurrentVersion} latest={info.LatestVersion} available={info.UpdateAvailable}");
        Logger.WriteLine($"更新自测：服务端={info.LatestVersion} 有更新={info.UpdateAvailable} 说明长度={info.Notes?.Length ?? 0}");
        if (!info.UpdateAvailable)
        {
            Logger.WriteLine("更新自测：已是最新，结束");
            return 0;
        }
        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            Logger.WriteLine("更新自测：服务端没有可下载地址（应引导到下载页）");
            return 3;
        }

        string? package = UpdateInstaller.DownloadAsync(info).GetAwaiter().GetResult();
        if (package is null)
        {
            Logger.WriteLine("更新自测：下载失败");
            return 2;
        }

        PackageVerification verification = UpdateInstaller.Verify(package, info);
        Logger.WriteLine($"更新自测：校验 {(verification.Ok ? "通过" : "失败")} —— {verification.Reason}");
        if (!verification.Ok) return 2;

        string? newExe = UpdateInstaller.ExtractPackage(package, info);
        if (newExe is null)
        {
            Logger.WriteLine("更新自测：解压失败");
            return 2;
        }

        string target = UpdateInstaller.CurrentExePath;
        if (!UpdateInstaller.StartUpdater(newExe, target))
        {
            Logger.WriteLine("更新自测：更新器启动失败");
            return 2;
        }

        Logger.WriteLine($"更新自测：更新器已启动（{newExe} -> {target}），本进程退出");
        Logger.Close();
        return 0;
    }
}
