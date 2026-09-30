using System.Text.RegularExpressions;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;
using MechrevoLite.Update;

namespace MechrevoLite.Usage;

/// <summary>
/// 匿名使用快照。不含主机名、用户名、MAC、磁盘序列号。beta21 起多带运行环境与问题信号：
/// 是否以管理员运行、GCU 服务档位与端口、Windows 版本号、上一次内更新的结果、本次运行的失败行数。
/// </summary>
internal sealed record UsageSnapshot(
    string Id,
    string Ver,
    string Event,
    string Model,
    string Project,
    string Bios,
    string Gpu,
    string GpuName,
    string Cpu,
    bool Gcu,
    bool Keyboard,
    bool Lightbar,
    bool Elevated = false,
    string Tier = "",
    int Port = 0,
    string Os = "",
    string Update = "",
    int Errors = 0)
{
    internal const int MaxFieldChars = 80;
    internal const string EventStart = "start";
    internal const string EventBeat = "beat";
    internal const string EventStop = "stop";

    static readonly Regex SafeToken = new(@"[^A-Za-z0-9 ._\-+#()]", RegexOptions.Compiled);

    internal static bool IsAllowedEvent(string? value) =>
        value is EventStart or EventBeat or EventStop;

    internal static string Clip(string? value)
    {
        string trimmed = (value ?? "").Trim();
        if (trimmed.Length > MaxFieldChars) trimmed = trimmed[..MaxFieldChars];
        return SafeToken.Replace(trimmed, "").Trim();
    }

    internal static UsageSnapshot Sanitize(UsageSnapshot raw)
    {
        string evt = IsAllowedEvent(raw.Event) ? raw.Event : EventBeat;
        string id = Clip(raw.Id);
        if (id.Length is < 8 or > 64) id = "";
        return raw with
        {
            Id = id,
            Ver = Clip(raw.Ver),
            Event = evt,
            Model = Clip(raw.Model),
            Project = Clip(raw.Project),
            Bios = Clip(raw.Bios),
            Gpu = Clip(raw.Gpu),
            GpuName = Clip(raw.GpuName),
            Cpu = Clip(raw.Cpu),
            Tier = Clip(raw.Tier),
            Port = raw.Port is > 0 and <= 65535 ? raw.Port : 0,
            Os = Clip(raw.Os),
            Update = Clip(raw.Update),
            Errors = Math.Clamp(raw.Errors, 0, 1_000_000),
        };
    }

    /// <summary>服务档位 → 心跳串（统计页按它分组）。</summary>
    internal static string TierToken(GcuServiceTier tier) => tier switch
    {
        GcuServiceTier.Modern12 => "modern12",
        GcuServiceTier.Legacy1020 => "legacy1020",
        GcuServiceTier.Foreign => "foreign",
        _ => "unknown",
    };

    internal static string GpuGenToken(DgpuGenerationKind kind) => kind switch
    {
        DgpuGenerationKind.Gen1020 => "1020",
        DgpuGenerationKind.Gen30 => "30",
        DgpuGenerationKind.Gen40 => "40",
        DgpuGenerationKind.Gen50 => "50",
        DgpuGenerationKind.NoDgpu => "none",
        _ => "unknown",
    };
}
