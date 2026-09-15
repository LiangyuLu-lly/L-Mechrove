using MechrevoLite;
using PawnIO;
using System.Diagnostics;
using System.Text.RegularExpressions;

public static class NvidiaSmi
{

    public static int GetDefaultMaxGPUPower()
    {
        if (AppConfig.ContainsModel("GU605") || AppConfig.ContainsModel("GA605")) return 125;
        if (AppConfig.ContainsModel("GA403")) return 90;
        if (AppConfig.ContainsModel("FA607")) return 140;
        else return 175;
    }

    /// <summary>
    /// 独显上的计算/CUDA 上下文清单（pid + 进程名）。NVAPI 的活跃图形应用列表
    /// 看不到纯计算上下文（实测 QQ 在独显挂 35MiB CUDA 上下文、NVAPI 列表为空、
    /// 任务管理器却显示独显活跃），核显切换的占用预检必须两路合并才完整。
    /// nvidia-smi 不存在或查询失败返回 null（调用方降级为只用 NVAPI）。
    /// </summary>
    internal static IReadOnlyList<(int Pid, string Name)>? QueryComputeApplications()
    {
        string output = RunNvidiaSmiCommand("--query-compute-apps=pid,process_name --format=csv,noheader");
        if (string.IsNullOrWhiteSpace(output)) return [];

        var apps = new List<(int Pid, string Name)>();
        foreach (string line in output.Split('\n'))
        {
            string text = line.Trim();
            if (text.Length == 0) continue;
            int separator = text.IndexOf(", ", StringComparison.Ordinal);
            if (separator <= 0) continue;
            if (!int.TryParse(text.AsSpan(0, separator).Trim(), out int pid)) continue;
            string name = text[(separator + 2)..].Trim();
            if (name.Length > 0) apps.Add((pid, name));
        }
        return apps;
    }

    private static string RunNvidiaSmiCommand(string arguments = "-i 0 -q")
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = "nvidia-smi",
            Arguments = arguments,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output;
        }
        catch (Exception ex)
        {
            //return File.ReadAllText(@"smi.txt");
            Debug.WriteLine(ex.Message);
        }

        return "";

    }
}
