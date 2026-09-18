using System.Text;
using MQTTnet;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Probe;

/// <summary>
/// 一次快照同时采到的 live MQTT 锚点值。协议里没有的字段（如充电上限）保持 -1，
/// 由快照字节单独说明，不假装有来源。
/// </summary>
public sealed record EcAnchors(
    int Pl1, int Pl2, int Pl4, int Tgp, int TccOffset,
    int CpuFanRpm, int GpuFanRpm, int CpuTemp, int GpuTemp, int BatteryPercent)
{
    public static EcAnchors FromFrames(IEnumerable<KeyValuePair<string, string>> frames)
    {
        var fields = new Dictionary<string, JToken>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> frame in frames)
        {
            try
            {
                if (JToken.Parse(frame.Value) is not JObject o) continue;
                foreach (JProperty p in o.Properties()) fields[p.Name] = p.Value;
            }
            catch (JsonException) { /* 非 JSON 帧：忽略，原始帧仍会写进 dump 文件 */ }
        }

        return new EcAnchors(
            Pick(fields, true, "CPU_PL1", "CPU_AmdSPL"),
            Pick(fields, true, "CPU_PL2", "CPU_AmdSPPT"),
            Pick(fields, true, "PL4", "CPU_AmdFPPT"),
            Pick(fields, true, "GPU_ConfigurableTGPTarget"),
            Pick(fields, false, "CPU_TccOffset"),
            Pick(fields, true, "CpuFanRpm"),
            Pick(fields, true, "GpuFanRpm"),
            Pick(fields, false, "CpuTemperature"),
            Pick(fields, false, "GpuTemperature"),
            Pick(fields, false, "BatteryLifePercent"));
    }

    public string Describe() =>
        $"PL1={Pl1} PL2={Pl2} PL4={Pl4} TGP={Tgp} Tcc={TccOffset} " +
        $"CpuFanRpm={CpuFanRpm} GpuFanRpm={GpuFanRpm} CpuTemp={CpuTemp} GpuTemp={GpuTemp} Battery={BatteryPercent}";

    /// <summary>power/rpm 只接受 &gt; 0（0 表示 GCU 关闭该项，不是有效设定）；温度/TCC/电量接受 0。</summary>
    static int Pick(IReadOnlyDictionary<string, JToken> fields, bool positiveOnly, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (!fields.TryGetValue(key, out JToken? token)) continue;
            if (!int.TryParse(token.ToString(), out int value)) continue;
            if (positiveOnly ? value > 0 : value >= 0) return value;
        }
        return -1;
    }
}

/// <summary>一条「寄存器 vs live 值」的锚点核对行。</summary>
public sealed record AnchorCheck(string Register, string Meaning, string Observed, string Anchor, string Verdict);

/// <summary>快照的报告/锚点分析，以及 CLI 入口。解析是纯函数，可对假传输与假帧做测试。</summary>
public static class EcSnapshotReport
{
    static string Hex(int address) => $"0x{address:X3}";

    /// <summary>
    /// 对 docs 明确「寄存器镜像已知值」的地址做一致性核对；其余歧义地址只给原始字节，
    /// 判定留给报告（读只读快照无法单独消歧，见 docs/hardware/README.md「15 个地址」）。
    /// </summary>
    public static IReadOnlyList<AnchorCheck> AnalyzeAnchors(EcSnapshotResult result, EcAnchors anchors)
    {
        var rows = new List<AnchorCheck>();
        byte? At(int address) => result.Bytes.TryGetValue(address, out int v) ? (byte)v : null;

        void Mirror(int address, string meaning, int live, string liveName)
        {
            byte? b = At(address);
            if (b is null) { rows.Add(new(Hex(address), meaning, "read error", $"{liveName}={live}", "unreadable")); return; }
            string verdict = live < 0 ? "no live anchor"
                : b == live ? "consistent"
                : $"MISMATCH (live {liveName}={live})";
            rows.Add(new(Hex(address), meaning, $"0x{b:X2} ({b})", $"{liveName}={live}", verdict));
        }

        Mirror(0x783, "ADDR_PL1_SETTING_VALUE", anchors.Pl1, "PL1");
        Mirror(0x784, "ADDR_PL2_SETTING_VALUE", anchors.Pl2, "PL2");
        Mirror(0x785, "ADDR_PL4_SETTING_VALUE", anchors.Pl4, "PL4");
        Mirror(0x4AB, "ecBt1RSOC", anchors.BatteryPercent, "BatteryLifePercent");

        byte? lo = At(0x464), hi = At(0x465);
        if (lo is null || hi is null)
            rows.Add(new("0x464/0x465", "main fan RPM", "read error", $"CpuFanRpm={anchors.CpuFanRpm}", "unreadable"));
        else
        {
            int bigEndian = (lo.Value << 8) | hi.Value;
            int littleEndian = (hi.Value << 8) | lo.Value;
            // docs/hardware/README.md 的实测样例 0x464/0x465 = 0x09/0x44 → 2372 = 0x0944，即高字节在低地址。
            string verdict = anchors.CpuFanRpm < 0 ? "no live anchor"
                : bigEndian == anchors.CpuFanRpm ? "consistent (0x464 = high byte)"
                : littleEndian == anchors.CpuFanRpm ? "consistent (0x464 = low byte)"
                : $"MISMATCH (live CpuFanRpm={anchors.CpuFanRpm})";
            rows.Add(new("0x464/0x465", "main fan RPM",
                $"0x{lo:X2}/0x{hi:X2} be={bigEndian} le={littleEndian}", $"CpuFanRpm={anchors.CpuFanRpm}", verdict));
        }

        byte? tgpByte = At(0x744);
        if (tgpByte is null)
            rows.Add(new("0x744", "ADDR_ConfigurableTGP_VALUE vs ADDR_MYFAN2_L2_PWM", "read error", $"TGP={anchors.Tgp}", "unreadable"));
        else
        {
            string verdict = anchors.Tgp < 0 ? "no live TGP anchor"
                : tgpByte == anchors.Tgp ? "consistent with cTGP"
                : $"NOT TGP (live TGP={anchors.Tgp}) — 与 docs/hardware/README.md 的 IDY 现场一致";
            rows.Add(new("0x744", "ADDR_ConfigurableTGP_VALUE vs ADDR_MYFAN2_L2_PWM",
                $"0x{tgpByte:X2} ({tgpByte})", $"TGP={anchors.Tgp}", verdict));
        }

        byte? up = At(0x7B9), down = At(0x7D0);
        string mapped = up is null ? "-" : up == 0 ? "100% (固件 no-limit)" : $"{up}%";
        rows.Add(new("0x7B9/0x7D0", "charge limit up/down",
            up is null || down is null ? "read error" : $"up=0x{up:X2} down=0x{down:X2}",
            $"app maps up -> {mapped}",
            up is null ? "unreadable" : "raw only (协议无 MQTT 充电上限字段, docs §5.3)"));

        byte? projectAp = At(0x740), projectOem = At(0x74C);
        rows.Add(new("0x740/0x74C", "ADDR_PROJECT_ID_BYTE / ADDR_OEMSERVICE_PROJECT_ID_BYTE",
            projectAp is null || projectOem is null ? "read error" : $"0x{projectAp:X2}/0x{projectOem:X2}",
            "BIOS_PROJECT_ID (注册表)", projectAp is null ? "unreadable" : "raw only — 值映射在声明里不可恢复"));

        return rows;
    }

    /// <summary>渲染可存盘的完整报告（UTF-8 无 BOM 由调用方写出）。</summary>
    public static string Render(
        EcSnapshotResult result, EcAnchors anchors,
        IEnumerable<KeyValuePair<string, string>> frames,
        IReadOnlyList<EcRange> ranges)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# EC snapshot (read-only, W4)");
        sb.AppendLine($"taken_utc: {result.TakenUtc:O}");
        sb.AppendLine($"transport: \\.\\ACPIDriver, IOCTL 0x9C40A488, [u32 addr] 4 B in / 16 B out (EcChargeLimit shape)");
        sb.AppendLine($"ranges: {string.Join(", ", ranges.Select(r => $"0x{r.Start:X3}-0x{r.EndInclusive:X3}"))}");
        sb.AppendLine($"bytes_read: {result.Count}, read_errors: {result.ErrorCount}");
        sb.AppendLine($"anchors: {anchors.Describe()}");
        sb.AppendLine();

        sb.AppendLine("## raw MQTT frames");
        foreach (KeyValuePair<string, string> frame in frames)
            sb.AppendLine($"{frame.Key}: {frame.Value}");
        sb.AppendLine();

        sb.AppendLine("## hex dump");
        sb.Append(EcSnapshot.FormatHexDump(result, ranges));
        sb.AppendLine();

        sb.AppendLine("## breadcrumbs outside the ranges");
        sb.Append(EcSnapshot.FormatLooseBreadcrumbs(result, ranges));
        sb.AppendLine();

        sb.AppendLine("## anchor checks");
        foreach (AnchorCheck row in AnalyzeAnchors(result, anchors))
            sb.AppendLine($"{row.Register} {row.Meaning}: {row.Observed} | {row.Anchor} => {row.Verdict}");
        sb.AppendLine();

        sb.AppendLine("## read errors");
        sb.AppendLine(result.ErrorCount == 0 ? "(none)" : string.Join(" ", result.Errors.Select(a => $"0x{a:X3}")));

        return sb.ToString();
    }

    /// <summary>CLI：<c>probe snapshot [outFile] [--ranges a,b] [--mqtt-seconds N] [--delay-ms N]</c>。</summary>
    public static async Task RunAsync(string[] args)
    {
        string? outFile = null;
        var positional = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--ranges": i++; continue;
                case "--mqtt-seconds": i++; continue;
                case "--delay-ms": i++; continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) positional.Add(args[i]);
        }
        outFile = positional.FirstOrDefault();

        IReadOnlyList<EcRange> ranges = ArgValue(args, "--ranges") is { } spec
            ? spec.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(EcRange.Parse).ToArray()
            : EcSnapshot.DocumentedRanges;
        int mqttSeconds = int.TryParse(ArgValue(args, "--mqtt-seconds"), out int ms) ? ms : 6;
        int delayMs = int.TryParse(ArgValue(args, "--delay-ms"), out int dm) ? dm : 4;

        Console.WriteLine("== EC snapshot (read-only, W4) ==");
        if (!AcpiDriverReadTransport.TryOpen(out AcpiDriverReadTransport? transport, out string openError))
        {
            Console.WriteLine("STOP: " + openError);
            Console.WriteLine("\\\\.\\ACPIDriver could not be opened; no driver/service was installed, started or modified.");
            Environment.ExitCode = 2;
            return;
        }

        using (transport)
        {
            var frames = await CollectMqttAsync(mqttSeconds);
            Console.WriteLine($">> MQTT frames: {frames.Count}");

            int total = ranges.Sum(r => r.Length);
            Console.WriteLine($">> dumping {total} in-range addresses (+ breadcrumbs), {delayMs} ms apart...");
            EcSnapshotResult result = EcSnapshot.Capture(transport!, ranges, EcSnapshot.DocumentedBreadcrumbs, delayMs);
            EcAnchors anchors = EcAnchors.FromFrames(frames);
            Console.WriteLine(">> anchors: " + anchors.Describe());

            string path = outFile ?? DefaultOutputPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, Render(result, anchors, frames, ranges), new UTF8Encoding(false));
            Console.WriteLine(">> dump: " + path);

            Console.WriteLine(">> anchor checks:");
            foreach (AnchorCheck row in AnalyzeAnchors(result, anchors))
                Console.WriteLine($"   {row.Register} {row.Meaning}: {row.Observed} | {row.Anchor} => {row.Verdict}");
            Console.WriteLine($">> read errors: {result.ErrorCount}");
        }
    }

    static async Task<List<KeyValuePair<string, string>>> CollectMqttAsync(int seconds)
    {
        var frames = new List<KeyValuePair<string, string>>();
        try
        {
            // 服务端把 client id 绑到凭据：UWPClient_User_5 只接受 client id UWPClient_5，
            // 其余一律 CONNACK=ClientIdentifierNotValid（与 MqttProbe 一致）。
            await using var transport = await MqttProbe.Connect("UWPClient_5");
            transport.MessageReceived += (topic, payload) =>
            {
                lock (frames) frames.Add(new(topic, payload));
                Console.WriteLine($"[mqtt] {topic}: {payload}");
            };
            await MqttProbe.Subscribe(transport, new[]
            {
                "Fan/Status", "System/FanInfo", "System/CpuInfo", "System/GpuInfo",
                "System/BatteryInfo", "System/BatteryProtection", "Setting/Status",
            });
            IMqttClient client = transport.Client ?? throw new InvalidOperationException("MQTT 未连接");
            await MqttProbe.Publish(client, "System/Control", new { Action = "System_ON" });
            await MqttProbe.Publish(client, "Fan/Control", new { Action = "GETSTATUS" });
            await MqttProbe.Publish(client, "Setting/Control", new { Action = "GETSTATUS" });
            await MqttProbe.Publish(client, "BatteryProtection/Control", new { Report = "GET" });
            await Task.Delay(TimeSpan.FromSeconds(seconds));
        }
        catch (Exception ex)
        {
            Console.WriteLine($">> MQTT unavailable: {ex.GetType().Name}: {ex.Message}");
        }
        return frames;
    }

    static string DefaultOutputPath()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MechrevoLite.slnx"))) dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "src", "Probe", "artifacts", $"ec-snapshot-{stamp}.txt");
    }

    static string? ArgValue(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
