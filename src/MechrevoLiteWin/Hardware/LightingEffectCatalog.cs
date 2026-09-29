using MechrevoLite.Properties;

namespace MechrevoLite.Hardware;

/// <summary>
/// 官方固件效果的参数规格：每个效果真正会被固件/服务采用的参数。界面只显示这里为 true 的控件，
/// 于是「能点的每一项都真的生效」。来源：CCUWinUI 各灯效页的 ChangeEffectColorCount 与服务端 RunEffct，
/// 见 docs/hardware/lighting-channels.md §3。
/// </summary>
/// <param name="Id">线上 effect 名（RGBKB_Effect 枚举名，服务按名字解析）。</param>
/// <param name="ColorSlots">0 = 固件自带配色（不显示颜色）；1 = 单色；&gt;1 = 多色（七彩调色板或单色铺满）。</param>
/// <param name="Speed">速度是否生效。</param>
/// <param name="Brightness">亮度是否生效。</param>
/// <param name="Directions">可选方向（RGBKB_Direction 名）；长度 ≤1 时不显示方向控件。</param>
/// <param name="PresetColors">只接受这几种颜色（A 面 Logo 呼吸）；非空时颜色控件换成色板。</param>
internal sealed record LightEffectSpec(
    string Id,
    string Label,
    int ColorSlots,
    bool Speed,
    bool Brightness = true,
    string[]? Directions = null,
    Color[]? PresetColors = null)
{
    internal bool UsesColor => ColorSlots > 0 || PresetColors is { Length: > 0 };
    internal bool UsesDirection => Directions is { Length: > 1 };
    internal bool MultiColor => ColorSlots > 1 && PresetColors is null;
}

/// <summary>下发结果的三态：只有拿到回读证据才算「已确认」。</summary>
internal enum LightApplyOutcome
{
    Failed,
    Sent,
    Confirmed,
}

/// <summary>回读证据来源：设备 HID 回读（0x88）或官方服务回读（Status / 服务落盘的上次灯效）。</summary>
internal enum LightReadbackSource
{
    None,
    Device,
    Service,
}

internal static class LightingEffectCatalog
{
    internal const string DirNone = "None";
    internal const string DirLeftRight = "LeftRight";
    internal const string DirRightLeft = "RightLeft";
    internal const string DirDownUp = "DownUp";
    internal const string DirUpDown = "UpDown";
    internal const string DirOnKeyPressed = "OnKeyPressed";

    static readonly string[] FourDirections = { DirLeftRight, DirRightLeft, DirDownUp, DirUpDown };
    static readonly string[] TwoDirections = { DirLeftRight, DirRightLeft };
    static readonly string[] KeyPressDirections = { DirNone, DirOnKeyPressed };

    /// <summary>七彩调色板（本项目 GCU 载荷一直使用的 7 色块：红橙黄绿青蓝紫红）。</summary>
    internal static readonly Color[] DefaultPalette =
    {
        Color.FromArgb(255, 0, 0), Color.FromArgb(255, 165, 0), Color.FromArgb(255, 255, 0),
        Color.FromArgb(0, 255, 0), Color.FromArgb(0, 255, 255), Color.FromArgb(0, 0, 255),
        Color.FromArgb(255, 0, 255),
    };

    /// <summary>A 面 Logo 呼吸只接受的六种颜色（SmartLightbarIDZ_Logo_View.ChangeEffectColorCount）。</summary>
    internal static readonly Color[] MbaLogoBreathingColors =
    {
        Color.FromArgb(255, 0, 0), Color.FromArgb(255, 255, 0), Color.FromArgb(0, 255, 0),
        Color.FromArgb(0, 255, 255), Color.FromArgb(0, 0, 255), Color.FromArgb(136, 0, 255),
    };

    /// <summary>官方 UI 每次都发满 7 个色块（ColorBlocks = 色块控件数）。</summary>
    internal const int PayloadColorBlocks = 7;

    // ---- 键盘 ----

    /// <summary>逐键：CCUWinUI MyKeyboardTypeInfo 的 else 分支 11 项（RgbKeyboardView.ChangeEffectColorCount 参数）。</summary>
    internal static LightEffectSpec[] PerKeyKeyboard() => new LightEffectSpec[]
    {
        new("Single", "单色", 1, Speed: false),
        new("Breathing", "呼吸", 7, Speed: true),
        new("Wave", "波浪", 0, Speed: true, Directions: FourDirections),
        new("Reactive", "按键反应", 7, Speed: true, Directions: KeyPressDirections),
        new("Rainbow", "彩虹", 0, Speed: false),
        new("Ripple", "涟漪", 7, Speed: true, Directions: KeyPressDirections),
        new("Raindrop", "雨滴", 7, Speed: true),
        new("Marquee", "跑马灯", 7, Speed: true),
        new("Spark", "火花", 7, Speed: true, Directions: KeyPressDirections),
        new("Aurora", "极光", 7, Speed: true, Directions: KeyPressDirections),
        new("Gaming", "游戏", 4, Speed: false),
    };

    /// <summary>四区：MyKeyboardTypeInfo FourZone 分支 6 项；Single 的 4 个色块对应 4 个分区。</summary>
    internal static LightEffectSpec[] FourZoneKeyboard() => new LightEffectSpec[]
    {
        new("Single", "单色", 4, Speed: false),
        new("Breathing", "呼吸", 7, Speed: true),
        new("Wave", "波浪", 0, Speed: true, Directions: TwoDirections),
        new("Rainbow", "彩虹", 0, Speed: false),
        new("Mix", "混合", 7, Speed: true),
        new("Flash", Strings.LightEffectFlash, 7, Speed: true),
    };

    /// <summary>四区单色：服务端 RunEffct 强制 Single + 固定颜色（FourZoneSingleColorKeyboard.cs），只剩亮度。</summary>
    internal static LightEffectSpec[] FourZoneSingleColorKeyboard() => new LightEffectSpec[]
    {
        new("Single", "单色", 0, Speed: false),
    };

    /// <summary>EC 单区：官方页面 Single/Rainbow/Manual；Manual（6 色轮换 + 间隔）未移植，不列出。</summary>
    internal static LightEffectSpec[] SingleZoneKeyboard() => new LightEffectSpec[]
    {
        new("Single", "单色", 1, Speed: false),
        new("Rainbow", "彩虹", 0, Speed: false),
    };

    internal static LightEffectSpec[] Keyboard(KeyboardLightKind kind) => kind switch
    {
        KeyboardLightKind.FourZone => FourZoneKeyboard(),
        KeyboardLightKind.FourZoneSingleColor => FourZoneSingleColorKeyboard(),
        KeyboardLightKind.SingleZone => SingleZoneKeyboard(),
        KeyboardLightKind.PerKey or KeyboardLightKind.Unclassified => PerKeyKeyboard(),
        _ => Array.Empty<LightEffectSpec>(),
    };

    // ---- 灯条 / 铰链 / Logo / 同步 ----

    /// <summary>Lighbar4 主灯条与铰链（MyHidLightbarTypeInfo Lighbar4 分支；ID5 去掉冲击/流星、波浪无速度）。</summary>
    internal static LightEffectSpec[] Lighbar4(string? biosProjectId)
    {
        bool id5 = string.Equals(biosProjectId?.Trim(), "ID5", StringComparison.OrdinalIgnoreCase);
        var list = new List<LightEffectSpec>
        {
            new("Single", "单色", 1, Speed: false),
            new("Breathing", "呼吸", 7, Speed: true),
            new("Wave", "波浪", 0, Speed: !id5),
        };
        if (!id5)
        {
            list.Add(new("Impact", "冲击", 7, Speed: true));
            list.Add(new("Raindrop", "流星", 1, Speed: true));
        }
        return list.ToArray();
    }

    /// <summary>
    /// 旧 HID 灯条（Lighbar/2/3）。Devour 在 40 系服务的效果枚举里不存在（按名字解析会失败），不列出。
    /// </summary>
    internal static LightEffectSpec[] OlderLightbar(LightbarGeneration generation) => generation switch
    {
        LightbarGeneration.Lighbar1 => new LightEffectSpec[]
        {
            new("Single", "单色", 1, Speed: false),
            new("Breathing", "呼吸", 7, Speed: true),
            new("Wave", "波浪", 0, Speed: true),
            new("Rainbow", "彩虹", 0, Speed: false),
            new("Mix", "混合", 7, Speed: true),
            new("Thinking", Strings.LightEffectThinking, 7, Speed: true),
            new("Raindrop", "流星", 1, Speed: true),
            new("Music", Strings.LightEffectMusic, 7, Speed: false),
        },
        LightbarGeneration.Lighbar2 => new LightEffectSpec[]
        {
            new("Single", "单色", 1, Speed: false),
            new("Breathing", "呼吸", 7, Speed: true),
            new("Wave", "波浪", 0, Speed: true),
            new("Rainbow", "彩虹", 0, Speed: false),
            new("Mix", "混合", 7, Speed: true),
            new("Thinking", Strings.LightEffectThinking, 7, Speed: true),
            new("Music", Strings.LightEffectMusic, 7, Speed: false),
            new("BatteryPercent", Strings.LightEffectBatteryPercent, 0, Speed: false),
        },
        LightbarGeneration.Lighbar3 => new LightEffectSpec[]
        {
            new("Single", "单色", 1, Speed: false),
            new("Breathing", "呼吸", 7, Speed: true),
            new("Flash", Strings.LightEffectFlash, 7, Speed: true),
        },
        _ => new LightEffectSpec[]
        {
            new("Single", "单色", 1, Speed: false),
            new("Breathing", "呼吸", 7, Speed: true),
        },
    };

    internal static LightEffectSpec[] MainLightbar(LightbarGeneration generation, string? biosProjectId) =>
        generation == LightbarGeneration.Lighbar4 ? Lighbar4(biosProjectId) : OlderLightbar(generation);

    /// <summary>Logo：普通 Logo（单色/呼吸）、单线 Logo（6 项）、A 面 Logo（单色/呼吸/混合，无速度无方向）。</summary>
    internal static LightEffectSpec[] Logo(LogoFlavor flavor) => flavor switch
    {
        LogoFlavor.OneWire => new LightEffectSpec[]
        {
            new("Single", "单色", 1, Speed: false),
            new("Breathing", "呼吸", 7, Speed: true),
            new("Wave", "波浪", 0, Speed: true),
            new("Impact", "冲击", 0, Speed: true),
            new("Raindrop", "流星", 1, Speed: true),
            new("ColorMarquee", Strings.LightEffectColorMarquee, 0, Speed: true),
        },
        LogoFlavor.MbaLogo => new LightEffectSpec[]
        {
            new("Single", "单色", 1, Speed: false),
            new("Breathing", "呼吸", 0, Speed: false, Brightness: false, PresetColors: MbaLogoBreathingColors),
            new("Mix", "混合", 0, Speed: false, Brightness: false),
        },
        LogoFlavor.Standard => new LightEffectSpec[]
        {
            new("Single", "单色", 1, Speed: false),
            new("Breathing", "呼吸", 7, Speed: true),
        },
        _ => Array.Empty<LightEffectSpec>(),
    };

    /// <summary>同步灯带：GetLB4SyncList = Single/Dawn（服务把 Dawn 换成固件预设七彩，颜色不生效）。</summary>
    internal static LightEffectSpec[] Sync() => new LightEffectSpec[]
    {
        new("Single", "单色", 1, Speed: false),
        new("Dawn", Strings.LightEffectDawn, 0, Speed: true),
    };

    internal static LightEffectSpec? Find(IReadOnlyList<LightEffectSpec> catalog, string? id) =>
        catalog.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>某条官方通道（Ctrl 主题）在当前识别结果下的效果目录。灯条代际未知时按最常见的 Lighbar4 给。</summary>
    internal static LightEffectSpec[] ForTopic(string topic, LightingChannelSet set, string? biosProjectId) => topic switch
    {
        MqttTopics.KeyboardCtrl => Keyboard(set.Keyboard),
        MqttTopics.LightbarCtrl => set.LightbarGeneration is LightbarGeneration.None or LightbarGeneration.Lighbar4
            ? Lighbar4(biosProjectId)
            : OlderLightbar(set.LightbarGeneration),
        MqttTopics.LogoLightCtrl => Logo(set.Logo == LogoFlavor.None ? LogoFlavor.MbaLogo : set.Logo),
        MqttTopics.HingeLightCtrl => Lighbar4(biosProjectId),
        MqttTopics.SyncLightCtrl => Sync(),
        _ => Array.Empty<LightEffectSpec>(),
    };

    /// <summary>EC 单区的 30 色调色板（SingleZone.cs m_color_cell，序号 1..30）。</summary>
    static readonly (int Index, int R, int G, int B)[] SingleZonePalette =
    {
        (1, 255, 0, 0), (2, 255, 50, 0), (3, 255, 80, 0), (4, 145, 60, 0), (5, 255, 102, 0), (6, 255, 128, 0),
        (7, 255, 180, 0), (8, 150, 128, 2), (9, 255, 204, 0), (10, 204, 225, 0), (11, 120, 255, 0), (12, 60, 115, 18),
        (13, 0, 255, 0), (14, 0, 255, 80), (15, 0, 255, 180), (16, 60, 125, 135), (17, 0, 255, 255), (18, 0, 180, 255),
        (19, 0, 80, 255), (20, 0, 35, 102), (21, 0, 0, 255), (22, 80, 0, 255), (23, 180, 0, 255), (24, 110, 45, 100),
        (25, 255, 0, 255), (26, 255, 0, 180), (27, 255, 0, 80), (28, 180, 5, 0), (29, 0, 0, 0), (30, 255, 255, 255),
    };

    /// <summary>EC 单区键盘单色：用户颜色 → 最近的调色板序号（服务按 MonochromeIndex 查表写 EC）。</summary>
    internal static int NearestSingleZoneIndex(Color color)
    {
        int best = 30, bestDistance = int.MaxValue;
        foreach (var (index, r, g, b) in SingleZonePalette)
        {
            int dr = r - color.R, dg = g - color.G, db = b - color.B;
            int distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance) { bestDistance = distance; best = index; }
        }
        return best;
    }

    /// <summary>
    /// EC 单区 SetEffectALL 必带字段（SingleZone.SetEffect 逐个读取、缺一个就整条丢弃，SingleZone.cs:282-330）。
    /// Manual/Breathing 相关字段给官方界面的默认值（本项目不开放这两种效果，但字段必须在）。
    /// </summary>
    internal static Dictionary<string, object> SingleZoneFields(Color color) => new()
    {
        ["MonochromeIndex"] = NearestSingleZoneIndex(color).ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["ManualIndex1"] = "1", ["ManualIndex2"] = "5", ["ManualIndex3"] = "9",
        ["ManualIndex4"] = "13", ["ManualIndex5"] = "17", ["ManualIndex6"] = "21",
        ["ManualInterval"] = "10",
        ["BreathingIndex"] = "1",
    };

    // ---- 与服务端同一张换算表（HIDKeyboard.Translate_ITE_*），用于回读比对 ----

    /// <summary>effect 名 → 固件效果 id（HIDKeyboard.cs Translate_ITE_EffectIndex）；未知返回 -1。</summary>
    internal static int FirmwareEffectId(string? effect) => (effect ?? "").Trim().ToUpperInvariant() switch
    {
        "SINGLE" => 1, "BREATHING" => 2, "WAVE" => 3, "REACTIVE" => 4, "RAINBOW" => 5, "RIPPLE" => 6,
        "DAWN" => 7, "MARQUEE" or "COLORMARQUEE" => 9, "RAINDROP" or "TWINKLING" => 10, "STACK" => 12,
        "IMPACT" => 13, "AURORA" => 14, "NEON" => 15, "SPARK" => 17, "FLASH" => 18, "MIX" => 19,
        "GAMING" => 21, "RIPPLEO" => 22, "ALPHABET" => 23, "STARSPARK" => 24, "STARHITTING" => 25,
        "THINKING" => 33, "MUSIC" => 34, "BATTERYPERCENT" => 35, "USERMODE" => 51, "COLORFULWAVE" => 52,
        "MANUAL" => 64,
        _ => -1,
    };

    /// <summary>0..4 档 → 固件亮度：键盘 0/8/22/36/50；Lighbar3/4 家族 0/25/50/75/100。</summary>
    internal static int FirmwareLight(int level, bool percentScale)
    {
        int l = Math.Clamp(level, 0, 4);
        return percentScale ? l * 25 : l switch { 0 => 0, 1 => 8, 2 => 22, 3 => 36, _ => 50 };
    }

    /// <summary>0..4 档 → 固件速度 10/7/5/3/1（数值越小越快）。</summary>
    internal static int FirmwareSpeed(int level) => Math.Clamp(level, 0, 4) switch
    {
        0 => 10, 1 => 7, 2 => 5, 3 => 3, _ => 1,
    };

    /// <summary>
    /// 组装 7 个色块（官方 UI 同款）：单色效果与「单色铺满」把用户颜色写满全部色块，
    /// 于是不论固件取第 1 块还是轮转全部色块，显示的都是用户选的颜色；七彩用官方默认调色板。
    /// </summary>
    internal static Color[] BuildColorSlots(LightEffectSpec? spec, Color userColor, bool usePalette)
    {
        var slots = new Color[PayloadColorBlocks];
        bool palette = spec is null || spec.ColorSlots == 0 && spec.PresetColors is null || spec.MultiColor && usePalette;
        for (int i = 0; i < slots.Length; i++)
            slots[i] = palette ? DefaultPalette[i % DefaultPalette.Length] : userColor;
        return slots;
    }

    /// <summary>A 面 Logo 呼吸只接受预设色；其他颜色官方会重置成默认色，这里取最近的预设色。</summary>
    internal static Color NearestPreset(Color color, IReadOnlyList<Color> presets)
    {
        Color best = presets[0];
        int bestDistance = int.MaxValue;
        foreach (Color p in presets)
        {
            int dr = p.R - color.R, dg = p.G - color.G, db = p.B - color.B;
            int distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance) { bestDistance = distance; best = p; }
        }
        return best;
    }

    /// <summary>
    /// 三态判定：发布失败 → 失败；发布成功但没有任何回读证据 → 已下发；回读证据与下发值一致 → 已确认。
    /// 回读证据不一致（设备/服务报的不是我们发的）也只能算已下发，绝不升级为已确认。
    /// </summary>
    internal static LightApplyOutcome ResolveOutcome(bool published, bool? readbackMatched) =>
        !published ? LightApplyOutcome.Failed
        : readbackMatched == true ? LightApplyOutcome.Confirmed
        : LightApplyOutcome.Sent;

    /// <summary>方向名 → 界面文案。</summary>
    internal static string DirectionLabel(string direction) => direction switch
    {
        DirLeftRight => Strings.LightDirLeftRight,
        DirRightLeft => Strings.LightDirRightLeft,
        DirDownUp => Strings.LightDirDownUp,
        DirUpDown => Strings.LightDirUpDown,
        DirOnKeyPressed => Strings.LightDirOnKeyPressed,
        _ => Strings.LightDirNone,
    };

    /// <summary>结果的中文文案（界面状态行）。</summary>
    internal static string OutcomeText(LightApplyOutcome outcome, LightReadbackSource source) => outcome switch
    {
        LightApplyOutcome.Confirmed => source == LightReadbackSource.Device
            ? Strings.LightApplyConfirmedDevice
            : Strings.LightApplyConfirmedService,
        LightApplyOutcome.Sent => Strings.LightApplySent,
        _ => Strings.LightApplyFailed,
    };
}
