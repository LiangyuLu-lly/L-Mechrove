using System.Text.Json;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;

namespace MechrevoLite.Mode;

/// <summary>
/// 内置模式的**厂商出厂参数**，用作「把内置模式改成自定义档承载」时的种子值。
///
/// <para>来源（均为只读）：</para>
/// <list type="bullet">
/// <item>EC 出厂功耗墙：<see cref="PlDefaults"/>（平衡 <c>0x730</c>、静音 <c>0x734</c>、狂暴 <c>0x7A7</c>）；</item>
/// <item>服务端档位存档 <c>UserPofiles\Mode{1,2,3}_Profile{n}.json</c>：温度墙、TGP、Dynamic Boost、
///       显卡超频、风扇转换灵敏度；</item>
/// <item>官方默认风扇表 <c>UserFanTables\DefaultFanTable_{Gaming,Office,Turbo}.json</c>（16 点占空比）。</item>
/// </list>
///
/// <para>为什么要种子：内置模式一旦被改了固件门控项（PL/TGP/DB/风扇曲线），就只能跑在固件自定义档上
/// （见 <c>docs/hardware/gcu-modes-and-profiles.md</c> §2），而自定义档里原来装的是别的参数。
/// 用户只改了 PL1，其余项必须仍是这个模式自己的出厂值，否则「平衡」会突然带上别的档的 TGP 和风扇曲线。</para>
/// </summary>
internal static class VendorModeDefaults
{
    /// <summary>测试接缝：代替真实的厂商数据目录。</summary>
    internal static Func<string?>? DataRootOverride { get; set; }

    /// <summary>
    /// 厂商服务的数据目录（<c>…\AiStoneService\MyControlCenter</c>）。从 GCUBridge 服务的镜像路径推出，
    /// 退回安装器的默认位置；都不存在时返回 null（调用方改用运行时回读值）。
    /// </summary>
    internal static string? DataRoot()
    {
        if (DataRootOverride is { } seam) return seam();
        try
        {
            string? image = GcuCoexistence.ReadServiceImagePath(GcuCoexistence.VendorServiceName);
            string? bridgeDir = image is null ? null : Path.GetDirectoryName(image.Trim().Trim('"'));
            if (bridgeDir is not null)
            {
                string candidate = Path.Combine(bridgeDir, "MyControlCenter");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("VendorModeDefaults: service image path unavailable: " + ex.Message);
        }
        string fallback = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "L-Mechrevo", "GCU", "AiStoneService", "MyControlCenter");
        return Directory.Exists(fallback) ? fallback : null;
    }

    /// <summary>厂商档位文件的模式号：1=平衡 2=静音 3=狂暴（静音狂暴共用狂暴那一组）。</summary>
    internal static int VendorModeNumber(PerfModeKind kind) => kind switch
    {
        PerfModeKind.Balanced => 1,
        PerfModeKind.Silent => 2,
        PerfModeKind.Turbo or PerfModeKind.SilentTurbo => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "custom modes have no vendor defaults"),
    };

    internal static string DefaultFanTableName(PerfModeKind kind) => kind switch
    {
        PerfModeKind.Balanced => "DefaultFanTable_Gaming",
        PerfModeKind.Silent => "DefaultFanTable_Office",
        PerfModeKind.Turbo or PerfModeKind.SilentTurbo => "DefaultFanTable_Turbo",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "custom modes have no vendor defaults"),
    };

    internal static PlDefaultMode PlModeFor(PerfModeKind kind) => kind switch
    {
        PerfModeKind.Balanced => PlDefaultMode.Gaming,
        PerfModeKind.Silent => PlDefaultMode.Office,
        _ => PlDefaultMode.Turbo,
    };

    /// <summary>设备能力与换算：从 <see cref="MechrevoHw"/> 取，测试直接构造。</summary>
    internal sealed record SeedContext(
        bool Amd,
        int Pl4Scale,
        Func<int, int> TccRawToTarget,
        bool TgpAdjustable,
        int TgpMaximum,
        bool DbAdjustable,
        int DbMaximum,
        bool TccAdjustable,
        bool GpuOverclockSupported,
        bool FanSwitchSpeedSupported,
        bool FanCurveSupported)
    {
        public static SeedContext From(MechrevoHw hw) => new(
            hw.UsesAmdPowerFields,
            hw.UsesAmdPowerFields ? 1 : hw.Pl4Scale,
            hw.TccTargetFromRaw,
            hw.GpuTgpAdjustable,
            hw.GpuTgpMaximum,
            hw.GpuDynamicBoostAdjustable,
            hw.GpuDbMaximum,
            hw.TccAdjustable,
            hw.SupportsGpuOverclock,
            hw.SupportsFanSwitchSpeed,
            hw.FanCurveSeen);
    }

    /// <summary>
    /// 该内置模式的完整固件级种子。读不到的项保持 null（调用方再用运行时回读兜底）。
    /// </summary>
    internal static PerfModeSettings Seed(PerfModeKind kind, MechrevoHw hw)
    {
        ArgumentNullException.ThrowIfNull(hw);
        if (kind == PerfModeKind.Custom) return PerfModeSettings.Default;

        SeedContext ctx = SeedContext.From(hw);
        string? root = DataRoot();
        string? profileJson = null;
        string? fanJson = null;
        if (root is not null)
        {
            profileJson = TryRead(Path.Combine(root, "UserPofiles",
                $"Mode{VendorModeNumber(kind)}_Profile{ProfileIndexFor(kind, root) + 1}.json"));
            fanJson = TryRead(Path.Combine(root, "UserFanTables", DefaultFanTableName(kind) + ".json"));
        }

        PlDefaultsResult pl = MechrevoService.ReadPlDefaults(PerfModeMapping.ToServiceMode(kind));
        return Compose(kind, ctx, profileJson, fanJson, pl.Editable ? pl.Values : null);
    }

    /// <summary>纯函数：把三路来源合成种子，便于单测。</summary>
    internal static PerfModeSettings Compose(
        PerfModeKind kind, SeedContext ctx, string? profileJson, string? fanTableJson, PlDefaultSet? ecDefaults)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        VendorProfile? profile = ParseProfile(profileJson);
        bool turbo = kind is PerfModeKind.Turbo or PerfModeKind.SilentTurbo;

        // 功耗墙：EC 出厂值优先（厂商 RestoreCurrentProfile 就是从这里装载的），档位存档兜底。
        int? pl1 = ecDefaults?.Pl1 ?? (ctx.Amd ? profile?.AmdSpl : profile?.Pl1);
        int? pl2 = ecDefaults?.Pl2 ?? (ctx.Amd ? profile?.AmdSppt : profile?.Pl2);
        int? pl4Wire = ecDefaults?.Pl4 ?? (ctx.Amd ? profile?.AmdFppt : profile?.Pl4);
        int? pl4 = pl4Wire is { } w ? (ctx.Amd ? w : w * Math.Max(1, ctx.Pl4Scale)) : null;

        bool? tccOn = null;
        int? tccTarget = null;
        if (ctx.TccAdjustable && profile is not null)
        {
            tccOn = profile.TccSwitch;
            int? raw = ctx.Amd ? profile.AmdTccTarget : profile.TccOffset;
            if (raw is { } r) tccTarget = ctx.Amd ? r : ctx.TccRawToTarget(r);
        }

        // TGP：内置模式结束时厂商会关掉 cTGP 控制位——开关为 0 的档实际跑的是满额 TGP。
        int? tgp = null;
        if (ctx.TgpAdjustable)
            tgp = profile is { TgpSwitch: true, TgpTarget: > 0 } ? profile.TgpTarget : ctx.TgpMaximum;

        // Dynamic Boost：狂暴模式厂商强制开到最大（SetDynamicBoostforTurboMode）。
        bool? dbOn = null;
        int? db = null;
        if (ctx.DbAdjustable)
        {
            if (turbo) { dbOn = true; db = ctx.DbMaximum; }
            else if (profile is not null) { dbOn = profile.DbSwitch; db = profile.Db; }
        }

        bool? ocOn = null;
        int? core = null;
        int? mem = null;
        if (ctx.GpuOverclockSupported && profile is not null)
        {
            // 狂暴档存档里记着官方自动超频的偏移（+105/+500），但总闸由主页开关另发。
            bool on = profile.OverClockingEnabled
                || (kind == PerfModeKind.Turbo && (profile.CoreOffset != 0 || profile.MemoryOffset != 0));
            ocOn = on;
            core = on ? profile.CoreOffset : 0;
            mem = on ? profile.MemoryOffset : 0;
        }

        bool? fssOn = null;
        int? fss = null;
        if (ctx.FanSwitchSpeedSupported && profile is not null)
        {
            fssOn = profile.FanSwitchSpeedEnabled;
            if (profile.FanSwitchSpeed > 0) fss = profile.FanSwitchSpeed;
        }

        int[]? cpuFan = null;
        int[]? gpuFan = null;
        if (ctx.FanCurveSupported)
        {
            cpuFan = ParseFanDuty(fanTableJson, cpu: true);
            gpuFan = ParseFanDuty(fanTableJson, cpu: false);
        }

        return new PerfModeSettings
        {
            Pl1 = pl1,
            Pl2 = pl2,
            Pl4 = pl4,
            TccOn = tccOn,
            TccTarget = tccTarget,
            GpuTgp = tgp,
            GpuDynamicBoostOn = dbOn,
            GpuDynamicBoost = db,
            GpuOverclockOn = ocOn,
            GpuCoreOffset = core,
            GpuMemoryOffset = mem,
            FanSwitchSpeedOn = fssOn,
            FanSwitchSpeedMs = fss,
            CpuFanDuty = cpuFan,
            GpuFanDuty = gpuFan,
        };
    }

    internal sealed record VendorProfile(
        int? Pl1, int? Pl2, int? Pl4,
        int? AmdSpl, int? AmdSppt, int? AmdFppt,
        bool TccSwitch, int? TccOffset, int? AmdTccTarget,
        bool TgpSwitch, int TgpTarget,
        bool DbSwitch, int? Db,
        bool OverClockingEnabled, int CoreOffset, int MemoryOffset,
        bool FanSwitchSpeedEnabled, int FanSwitchSpeed);

    internal static VendorProfile? ParseProfile(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            JsonElement cpu = Child(root, "CPU");
            JsonElement gpu = Child(root, "GPU");
            JsonElement fan = Child(root, "FAN");
            return new VendorProfile(
                Int(cpu, "PL1"), Int(cpu, "PL2"), Int(cpu, "PL4"),
                Int(cpu, "AmdSPL"), Int(cpu, "AmdSPPT"), Int(cpu, "AmdFPPT"),
                Int(cpu, "TccOffsetSwitch") == 1, Int(cpu, "TccOffset"), Int(cpu, "AmdTccTarget"),
                Int(gpu, "ConfigurableTGPSwitch") == 1, Int(gpu, "ConfigurableTGPTarget") ?? 0,
                Int(gpu, "DynamicBoostSwitch") == 1, Int(gpu, "DynamicBoost"),
                Int(root, "OverClockingEnabled") == 1, Int(gpu, "CoreClockOffsetOC") ?? 0, Int(gpu, "MemoryClockOffsetOC") ?? 0,
                Int(fan, "FanSwitchSpeedEnabled") == 1, Int(fan, "FanSwitchSpeed") ?? 0);
        }
        catch (JsonException ex)
        {
            Logger.WriteLine("VendorModeDefaults: profile JSON unreadable: " + ex.Message);
            return null;
        }
    }

    /// <summary>16 点占空比（0-100）。不是 16 点或读不到返回 null。</summary>
    internal static int[]? ParseFanDuty(string? json, bool cpu)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(cpu ? "CPU" : "GPU", out JsonElement points)
                || points.ValueKind != JsonValueKind.Array
                || points.GetArrayLength() != 16)
                return null;
            var duty = new int[16];
            int i = 0;
            foreach (JsonElement point in points.EnumerateArray())
            {
                if (!point.TryGetProperty("Duty", out JsonElement d) || !d.TryGetInt32(out int v)) return null;
                duty[i++] = Math.Clamp(v, 0, 100);
            }
            return duty;
        }
        catch (JsonException ex)
        {
            Logger.WriteLine("VendorModeDefaults: fan table JSON unreadable: " + ex.Message);
            return null;
        }
    }

    /// <summary>该模式当前用的是第几档（<c>MainOption.json</c>）；读不到按第 1 档。</summary>
    static int ProfileIndexFor(PerfModeKind kind, string root)
    {
        string? main = TryRead(Path.Combine(root, "UserPofiles", "MainOption.json"));
        if (main is null) return 0;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(main);
            string key = kind switch
            {
                PerfModeKind.Balanced => "GamingProfileIndex",
                PerfModeKind.Silent => "OfficeProfileIndex",
                _ => "TurboProfileIndex",
            };
            return Int(doc.RootElement, key) is { } v ? Math.Clamp(v, 0, 1) : 0;
        }
        catch (JsonException) { return 0; }
    }

    static string? TryRead(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex)
        {
            Logger.WriteLine($"VendorModeDefaults: cannot read {path}: {ex.Message}");
            return null;
        }
    }

    static JsonElement Child(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement child)
            ? child
            : default;

    static int? Int(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetInt32(out int n) => n,
            JsonValueKind.True => 1,
            JsonValueKind.False => 0,
            JsonValueKind.String when int.TryParse(v.GetString(), out int s) => s,
            _ => null,
        };
    }
}
