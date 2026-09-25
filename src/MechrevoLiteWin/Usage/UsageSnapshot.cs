using System.Text.RegularExpressions;
using MechrevoLite.Gpu;
using MechrevoLite.Hardware;
using MechrevoLite.Update;

namespace MechrevoLite.Usage;

/// <summary>匿名使用快照。不含主机名、用户名、MAC、磁盘序列号。</summary>
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
    bool Lightbar)
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
        };
    }

    internal static string GpuGenToken(DgpuGenerationKind kind) => kind switch
    {
        DgpuGenerationKind.Gen30 => "30",
        DgpuGenerationKind.Gen40 => "40",
        DgpuGenerationKind.Gen50 => "50",
        DgpuGenerationKind.NoDgpu => "none",
        _ => "unknown",
    };
}
