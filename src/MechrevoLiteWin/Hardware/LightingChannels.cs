namespace MechrevoLite.Hardware;

/// <summary>键盘灯的硬件形态（与官方 GCU 服务 RGBKB_Type 分型同一口径）。</summary>
internal enum KeyboardLightKind
{
    /// <summary>还没有任何证据（服务未连、注册表无值、HID 未扫描）。不显示入口。</summary>
    Unknown,
    /// <summary>证据齐全但本机没有可控的键盘灯（服务报 Normal/UNKNOWN，HID 也没有键盘接口）。</summary>
    None,
    /// <summary>逐键 RGB（MEZone 2nd/2p1nd/2p2nd/3nd/3p1nd，UsagePage 0xFF03）。软件灯效与官方全量固件效果都可用。</summary>
    PerKey,
    /// <summary>第一代逐键（MEZone_1st，UsagePage 0xFF02）。官方服务不为它创建控制器，只能走软件灯效。</summary>
    PerKeyLegacy,
    /// <summary>四区 RGB（UsagePage 0xFF12）。软件逐键协议不适用，只走官方固件效果。</summary>
    FourZone,
    /// <summary>四区单色（ProjectID 17）。官方服务强制单色常亮，只能调亮度/开关。</summary>
    FourZoneSingleColor,
    /// <summary>EC 单区 RGB（EC 0x766 bit2）。只走官方固件效果（单色/彩虹）。</summary>
    SingleZone,
    /// <summary>单色背光（Customize/Info KeyboardType="2"）。只有开关（Setting/Control）。</summary>
    SingleColorBacklight,
    /// <summary>
    /// 有键盘灯证据但服务没给分型（旧服务的 Keyboard/Status 不带 type、或只有 KeyboardSupport 画像）。
    /// 沿用逐键全表并交给 HID 探测决定软件路径；一旦服务报出 type（含 Normal）就以 type 为准。
    /// </summary>
    Unclassified,
}

/// <summary>我方 HID 扫描到的 ITE 键盘接口形态（官方判据：VID 048D + 键盘 PID + Usage 1 + UsagePage）。</summary>
internal enum HidKeyboardInterfaceKind
{
    NotScanned,
    None,
    PerKey,
    PerKeyLegacy,
    FourZone,
}

/// <summary>HID 主灯条代际（官方 HidLightbar/Status 的 type 字段）。</summary>
/// <summary>某条灯光通道最近一帧 Status：电源态、亮度档（0..4，-1 = 无）与帧序号（判「下发后是否来了新帧」）。</summary>
internal readonly record struct LightStatusSnapshot(string? PowerStatus, int BrightnessLevel, long Version);

internal enum LightbarGeneration
{
    None,
    Lighbar1,
    Lighbar2,
    Lighbar3,
    Lighbar4,
    /// <summary>服务报了灯条内容，但 type 不是已知四代之一（新固件）。只开放四代共有的单色/呼吸。</summary>
    OtherHid,
}

/// <summary>Logo 灯的官方界面形态：决定效果清单与参数。</summary>
internal enum LogoFlavor
{
    None,
    /// <summary>LogoSupport（A2 探测）：单色/呼吸。</summary>
    Standard,
    /// <summary>NewlogoSupport（IDA/IDB 单线 Logo）：单色/呼吸/波浪/冲击/流星/彩色跑马灯。</summary>
    OneWire,
    /// <summary>Support\MBALogo / MBlogoSupport（A 面 Logo）：单色/呼吸/混合，无速度无方向。</summary>
    MbaLogo,
}

/// <summary>识别输入：服务上报 + 注册表 + 我方 HID 扫描。全部可空，缺什么就少推什么，绝不猜。</summary>
internal sealed record LightingEvidence
{
    /// <summary>Keyboard/Status 的 type（RGBKB_Type 名），例如 MEZone_3p1nd_101。</summary>
    public string? KeyboardStatusType { get; init; }
    /// <summary>ItemSupport\KeyboardType（服务写入的 RGBKB_Type 序号）；-1 = 无值。</summary>
    public int RegistryKeyboardType { get; init; } = -1;
    /// <summary>Customize/Info 的 KeyboardType（"0"/"1"/"2"；"2" = 单色背光项目且 EC 报有背光）。</summary>
    public string? CustomizeKeyboardType { get; init; }
    public HidKeyboardInterfaceKind HidKeyboard { get; init; } = HidKeyboardInterfaceKind.NotScanned;
    /// <summary>HidLightbar/Status 的 type（服务只有在灯条设备存在时才回 GETSTATUS）。</summary>
    public string? LightbarStatusType { get; init; }
    /// <summary>HidLightbar/Status 带了可识别内容（type 或 powerStatus 非空）。</summary>
    public bool LightbarStatusContent { get; init; }
    public string? BiosProjectId { get; init; }
    public bool? LogoSupport { get; init; }
    public bool? HingeSupport { get; init; }
    public bool? BaseSupport { get; init; }
    public bool? NewlogoSupport { get; init; }
    /// <summary>HidLightbar/Status 的 MBlogoSupport（1.2 版服务新增字段）。</summary>
    public bool? MbLogoSupport { get; init; }
    /// <summary>HKLM\SOFTWARE\OEM\GamingCenter2\Support\MBALogo（CCUWinUI 用它决定 A 面 Logo 页）。</summary>
    public bool MbaLogoRegistry { get; init; }
    public string? LogoStatusType { get; init; }
    public string? SyncStatusType { get; init; }
    /// <summary>收到过 MyRgbLightbar/Status（服务只在 LightbarType="2" 时启用 EC 灯带并回状态）。</summary>
    public bool EcLightbarStatusSeen { get; init; }
    /// <summary>不带分型的键盘证据：收到过不含 type 的 Keyboard/Status，或注册表只有 KeyboardSupport 位。</summary>
    public bool UntypedKeyboardEvidence { get; init; }
}

/// <summary>识别结果：本机真实存在且可控的灯光通道。</summary>
internal sealed record LightingChannelSet(
    KeyboardLightKind Keyboard,
    LightbarGeneration LightbarGeneration,
    bool MainLightbar,
    LogoFlavor Logo,
    bool Hinge,
    bool Sync,
    bool EcLightbar)
{
    internal static readonly LightingChannelSet Empty =
        new(KeyboardLightKind.Unknown, LightbarGeneration.None, false, LogoFlavor.None, false, false, false);

    /// <summary>键盘灯是否有任何可用的控制路径（逐键一代只能靠软件路径，由调用方结合 HID 判定）。</summary>
    internal bool KeyboardPresent => Keyboard is not (KeyboardLightKind.Unknown or KeyboardLightKind.None);

    /// <summary>官方 GCU 能否接管这把键盘（逐键一代官方没有控制器；单色背光走 Setting/Control 开关）。</summary>
    internal bool KeyboardGcuControllable => Keyboard is KeyboardLightKind.PerKey or KeyboardLightKind.FourZone
        or KeyboardLightKind.FourZoneSingleColor or KeyboardLightKind.SingleZone or KeyboardLightKind.Unclassified;

    /// <summary>我方软件（HID 逐键帧）路径是否适用于这把键盘。</summary>
    internal bool KeyboardSoftwareCapable => Keyboard is KeyboardLightKind.PerKey or KeyboardLightKind.PerKeyLegacy
        or KeyboardLightKind.Unclassified;

    internal bool LogoPresent => Logo != LogoFlavor.None;
}

/// <summary>
/// 灯光通道识别：复刻官方 GCU 1.0.2.70 服务的存在判定与 CCUWinUI 5.56 的页面显隐，
/// 细节与出处见 docs/hardware/lighting-channels.md。纯函数，无 I/O。
/// </summary>
internal static class LightingChannelDetector
{
    internal const ushort IteVendorId = 0x048D;

    /// <summary>官方键盘 PID 表（ITE_SPEC.cs:9，按官方遍历顺序）。</summary>
    internal static readonly ushort[] KeyboardProductIds =
        { 0xCE00, 0x6000, 0x6001, 0x6002, 0x6003, 0x6004, 0x6006, 0x6007, 0x600A, 0x600B };

    /// <summary>官方灯条 PID 表（ITE_SPEC.cs:11）。0x7000/0x7001 = Lighbar4（Usage 2），其余 Usage 1。</summary>
    internal static readonly ushort[] LightbarProductIds = { 0x7001, 0x7000, 0x6005, 0x6008, 0x6010 };

    internal const ushort UsagePagePerKey = 0xFF03;
    internal const ushort UsagePagePerKeyLegacy = 0xFF02;
    internal const ushort UsagePageFourZone = 0xFF12;

    /// <summary>服务端会用 0xA2 探测分区能力位的 BIOS（LM_ITE_RGB.GetDeviceSupport）。其余 BIOS 的能力位恒为 false。</summary>
    static readonly string[] ZoneProbedBios = { "IDA", "IDB", "IDX", "IDZ" };

    static readonly string[] PerKeyTypes =
    {
        "MEZone_2nd_101", "MEZone_2nd_102",
        "MEZone_2p1nd_85", "MEZone_2p1nd_86", "MEZone_2p1nd_87", "MEZone_2p1nd_88",
        "MEZone_2p2nd_97", "MEZone_2p2nd_98", "MEZone_2p2nd_99", "MEZone_2p2nd_100",
        "MEZone_3nd_98", "MEZone_3nd_99",
        "MEZone_3p1nd_101", "MEZone_3p1nd_102", "MEZone_3p1nd_103", "MEZone_3p1nd_104",
    };

    /// <summary>官方 RGBKB_Type 枚举顺序（gcu40 LightingModel/RGBKB_Type.cs），用于解读 ItemSupport\KeyboardType 序号。</summary>
    static readonly string[] RgbkbTypeOrdinals =
    {
        "Normal", "SingleZone", "FourZone", "FourZoneSingleColor", "MEZone_1st",
        "MEZone_2nd_101", "MEZone_2nd_102",
        "MEZone_3p1nd_101", "MEZone_3p1nd_102", "MEZone_3p1nd_103", "MEZone_3p1nd_104",
        "MEZone_2p1nd_85", "MEZone_2p1nd_86", "MEZone_2p1nd_87", "MEZone_2p1nd_88",
        "MEZone_2p2nd", "MEZone_Lighbar",
        "MEZone_2p2nd_97", "MEZone_2p2nd_98", "MEZone_2p2nd_99", "MEZone_2p2nd_100",
        "MEZone_3nd_98", "MEZone_3nd_99",
        "MEZone_Lighbar2", "MEZone_Lighbar3", "MEZone_Lighbar4",
        "MEZone_Lighbar4_logo", "MEZone_Lighbar4_hinge", "MEZone_Lighbar4_sync",
    };

    internal static string? TypeNameFromOrdinal(int ordinal) =>
        ordinal >= 0 && ordinal < RgbkbTypeOrdinals.Length ? RgbkbTypeOrdinals[ordinal] : null;

    /// <summary>
    /// 服务/注册表报的 RGBKB_Type 名 → 键盘形态。null = 这个名字不是键盘类型（空、Normal、灯条等）。
    /// </summary>
    internal static KeyboardLightKind? KindFromTypeName(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName)) return null;
        string name = typeName.Trim();
        if (Array.Exists(PerKeyTypes, t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase)))
            return KeyboardLightKind.PerKey;
        return name.ToUpperInvariant() switch
        {
            "FOURZONE" => KeyboardLightKind.FourZone,
            "FOURZONESINGLECOLOR" => KeyboardLightKind.FourZoneSingleColor,
            "SINGLEZONE" => KeyboardLightKind.SingleZone,
            "MEZONE_1ST" => KeyboardLightKind.PerKeyLegacy,
            _ => null,
        };
    }

    /// <summary>HID 接口是否是官方意义上的键盘灯接口（VID/PID 表 + Usage 1）。</summary>
    internal static bool IsKeyboardInterface(ushort vendorId, ushort productId, ushort usage) =>
        vendorId == IteVendorId && usage == 1 && Array.IndexOf(KeyboardProductIds, productId) >= 0;

    /// <summary>官方分型第一步：按 UsagePage 区分四区/逐键一代/逐键（LM_ITE_RGB.cs:3788-3924）。</summary>
    internal static HidKeyboardInterfaceKind ClassifyKeyboardInterface(ushort vendorId, ushort productId, ushort usage, ushort usagePage)
    {
        if (!IsKeyboardInterface(vendorId, productId, usage)) return HidKeyboardInterfaceKind.None;
        return usagePage switch
        {
            UsagePagePerKey => HidKeyboardInterfaceKind.PerKey,
            UsagePagePerKeyLegacy => HidKeyboardInterfaceKind.PerKeyLegacy,
            UsagePageFourZone => HidKeyboardInterfaceKind.FourZone,
            _ => HidKeyboardInterfaceKind.None,   // 官方遇到未知 UsagePage 直接判「无键盘」
        };
    }

    /// <summary>HID 接口是否是官方意义上的灯条接口（Lighbar4 = 0x7000/0x7001 + Usage 2；旧灯条 Usage 1；均 0xFF03）。</summary>
    internal static bool IsLightbarInterface(ushort vendorId, ushort productId, ushort usage, ushort usagePage)
    {
        if (vendorId != IteVendorId || usagePage != UsagePagePerKey) return false;
        if (productId is 0x7000 or 0x7001) return usage == 2;
        return usage == 1 && productId is 0x6005 or 0x6008 or 0x6010;
    }

    internal static LightbarGeneration GenerationFromTypeName(string? typeName) =>
        (typeName ?? "").Trim().ToUpperInvariant() switch
        {
            "MEZONE_LIGHBAR" or "MEZONE_LIGHTBAR" => LightbarGeneration.Lighbar1,
            "MEZONE_LIGHBAR2" or "MEZONE_LIGHTBAR2" => LightbarGeneration.Lighbar2,
            "MEZONE_LIGHBAR3" or "MEZONE_LIGHTBAR3" => LightbarGeneration.Lighbar3,
            "MEZONE_LIGHBAR4" or "MEZONE_LIGHTBAR4" => LightbarGeneration.Lighbar4,
            _ => LightbarGeneration.None,
        };

    static bool IsZoneProbedBios(string? bios) =>
        Array.Exists(ZoneProbedBios, b => string.Equals(b, bios?.Trim(), StringComparison.OrdinalIgnoreCase));

    static bool HasTypeContent(string? type) =>
        !string.IsNullOrWhiteSpace(type)
        && !string.Equals(type.Trim(), "Normal", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(type.Trim(), "UNKNOWN", StringComparison.OrdinalIgnoreCase);

    internal static KeyboardLightKind DetectKeyboard(LightingEvidence e)
    {
        // 1) 服务实时上报的分型（官方界面同样只认 Keyboard/Status.type）。
        KeyboardLightKind? kind = KindFromTypeName(e.KeyboardStatusType);
        // 2) 服务写入注册表的分型序号——只在服务还没实时回报时当离线证据；
        //    实时状态说 Normal 时不能被上次开机留下的注册表值推翻。
        if (e.KeyboardStatusType is null)
            kind ??= KindFromTypeName(TypeNameFromOrdinal(e.RegistryKeyboardType));
        if (kind is { } rgb && rgb != KeyboardLightKind.PerKeyLegacy) return rgb;

        // 3) 单色背光：Customize/Info KeyboardType="2" 且不是单区（CCUWinUI LightViewModel.cs:2188-2191）。
        if (string.Equals(e.CustomizeKeyboardType?.Trim(), "2", StringComparison.Ordinal))
            return KeyboardLightKind.SingleColorBacklight;

        // 4) 我方 HID 扫描（服务不在时也能判出逐键/四区）。
        switch (e.HidKeyboard)
        {
            case HidKeyboardInterfaceKind.PerKey: return KeyboardLightKind.PerKey;
            case HidKeyboardInterfaceKind.FourZone: return KeyboardLightKind.FourZone;
            case HidKeyboardInterfaceKind.PerKeyLegacy: return KeyboardLightKind.PerKeyLegacy;
        }
        if (kind == KeyboardLightKind.PerKeyLegacy) return KeyboardLightKind.PerKeyLegacy;

        // 5) 服务明确报了 Normal/UNKNOWN → 没有 RGB 键盘（这正是旧口径造假入口的场景）。
        bool serviceSaidNone = e.KeyboardStatusType is not null && !HasTypeContent(e.KeyboardStatusType);
        if (serviceSaidNone) return KeyboardLightKind.None;
        // 服务报了一个不认识的分型名（新固件）：有键盘控制器，但形态未知 → 按逐键全表处理，交 HID 探测。
        if (HasTypeContent(e.KeyboardStatusType)) return KeyboardLightKind.Unclassified;
        // 6) 只有不带分型的证据（旧服务 / 画像位）：保留入口，由 HID 探测与服务回读决定能否控制。
        if (e.UntypedKeyboardEvidence) return KeyboardLightKind.Unclassified;
        bool registrySaidNone = e.RegistryKeyboardType == 0;
        bool hidSaidNone = e.HidKeyboard == HidKeyboardInterfaceKind.None;
        return registrySaidNone && hidSaidNone ? KeyboardLightKind.None : KeyboardLightKind.Unknown;
    }

    internal static LightingChannelSet Detect(LightingEvidence e)
    {
        KeyboardLightKind keyboard = DetectKeyboard(e);

        LightbarGeneration generation = GenerationFromTypeName(e.LightbarStatusType);
        // 服务只在灯条设备存在时才回 HidLightbar/Status（设备为空时 GETSTATUS 抛异常不发布）。
        // type 不在已知四代里但有内容：仍是真实 HID 灯条（新固件），只开放四代共有的效果。
        if (generation == LightbarGeneration.None && e.LightbarStatusContent && HasTypeContent(e.LightbarStatusType))
            generation = LightbarGeneration.OtherHid;
        bool hidLightbar = generation != LightbarGeneration.None;
        bool lighbar4 = generation == LightbarGeneration.Lighbar4;
        bool zoneProbed = lighbar4 && IsZoneProbedBios(e.BiosProjectId);

        // 主灯条：做过 A2 分区探测的 BIOS 以 BaseSupport 为准（没有 Base 分区就没有主灯条灯珠）；
        // 其余机型官方走「普通灯条」页（LightViewModel.cs:2466-2470）。
        bool mainLightbar = hidLightbar && (!zoneProbed || e.BaseSupport == true);

        // Logo：服务的 Logo 通道只在 Lighbar4 上创建（HIDLightbar_logo.cs:197-199），
        // 是否有灯珠看 A2 能力位 / 单线 Logo 位 / A 面 Logo 标志（LightViewModel.cs:2425-2472、2514-2532）。
        bool logoChannel = lighbar4 || GenerationFromTypeName(e.LogoStatusType) == LightbarGeneration.Lighbar4;
        LogoFlavor logo = LogoFlavor.None;
        if (logoChannel)
        {
            bool idaOrIdb = e.BiosProjectId?.Trim().ToUpperInvariant() is "IDA" or "IDB";
            if (idaOrIdb && e.NewlogoSupport == true) logo = LogoFlavor.OneWire;
            else if (e.MbaLogoRegistry || e.MbLogoSupport == true) logo = LogoFlavor.MbaLogo;
            else if (zoneProbed && e.LogoSupport == true) logo = LogoFlavor.Standard;
        }

        // 铰链：只有 A2 探测过且报了 HingeSupport 才有灯珠；服务对所有 Lighbar4 都会回一个空壳状态。
        bool hinge = zoneProbed && e.HingeSupport == true;

        // 同步灯带：服务只在 BIOS=IDZ 的 Lighbar4 上初始化（HIDLightbar_sync.cs:197-207），且状态带内容。
        bool sync = lighbar4
            && string.Equals(e.BiosProjectId?.Trim(), "IDZ", StringComparison.OrdinalIgnoreCase)
            && HasTypeContent(e.SyncStatusType);

        // EC 灯带：服务只在 LightbarType="2" 时启用并回 MyRgbLightbar/Status；
        // 有 HID 灯条时官方界面隐藏 EC 灯带页（LightViewModel.cs:2471-2472）。
        bool ecLightbar = e.EcLightbarStatusSeen && !hidLightbar;

        return new LightingChannelSet(keyboard, generation, mainLightbar, logo, hinge, sync, ecLightbar);
    }
}
