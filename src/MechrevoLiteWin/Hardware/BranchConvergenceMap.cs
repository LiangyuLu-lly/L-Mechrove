namespace MechrevoLite.Hardware;

/// <summary>一个分支点所属的勘查分区（口径见 <c>console-coupling.md:3</c>）。</summary>
public enum BranchArea
{
    /// <summary>A 区：机器/项目身份的判定来源。</summary>
    IdentitySources,

    /// <summary>B 区：按机型身份条件化的功能门。</summary>
    FeatureGates,

    /// <summary>C 区：硬编码机型假设。</summary>
    Hardcodes,

    /// <summary>D 区：缓存/持久化，换机型后可能残留旧值。</summary>
    Persistence,

    /// <summary>E 区：未按机型门控的消费点。</summary>
    UngatedConsumers,
}

/// <summary>一个分支点的收敛结果。</summary>
public enum BranchDisposition
{
    /// <summary>已收敛到矩阵/能力位（<see cref="FeatureMatrix"/>，缺失按各自策略）。</summary>
    MatrixGated,

    /// <summary>已收敛到识别层（<see cref="ModelRegistry"/> / 身份或代际探测）。</summary>
    IdentityLayer,

    /// <summary>保留：设备协议常量或刻意分歧，已逐条注明理由。</summary>
    KeptWithReason,
}

/// <summary>一条分支点记录。<see cref="Owner"/> 是唯一负责收敛它的 todo（Metis 唯一指派）。</summary>
public sealed record BranchSite(
    string Id,
    BranchArea Area,
    string Location,
    BranchDisposition Disposition,
    string Owner,
    string Note);

/// <summary>台账缺项、重复或越界。绝不静默放行。</summary>
public sealed class BranchConvergenceException : Exception
{
    public BranchConvergenceException(string message) : base(message) { }
}

/// <summary>
/// T27：把 84 个机型相关分支点收敛到矩阵/识别层的**唯一对照表**。
///
/// <para>口径 = <c>console-coupling.md:3</c>：A 8 + B 41 + C 22 + D 5 + E 8 = 84。
/// C 区（22 处硬编码）直接从 <see cref="HardcodeDispositions"/> 派生，保证与 T9 同源；
/// E 区（8 个未门控消费点）与 T8/T29 的证据表一一对应。每个站点只有一个处置、一个负责 todo。</para>
/// </summary>
public static class BranchConvergenceMap
{
    public const int ExpectedTotal = 84;

    public static readonly IReadOnlyDictionary<BranchArea, int> ExpectedAreaCounts =
        new Dictionary<BranchArea, int>
        {
            [BranchArea.IdentitySources] = 8,
            [BranchArea.FeatureGates] = 41,
            [BranchArea.Hardcodes] = 22,
            [BranchArea.Persistence] = 5,
            [BranchArea.UngatedConsumers] = 8,
        };

    /// <summary>参与收敛的 todo 集合；台账里的 owner 必须落在这里。</summary>
    public static readonly IReadOnlySet<string> KnownOwners = new HashSet<string>(StringComparer.Ordinal)
    {
        "T1", "T3", "T5", "T6", "T7", "T8", "T9", "T20", "T21", "T28", "T29", "T34",
    };

    public static IReadOnlyList<BranchSite> Sites { get; }

    // 静态构造器在全部字段初始化（含 HardcodeLocations/HardcodeOwners）之后才跑 Build()。
    static BranchConvergenceMap() => Sites = Build();

    /// <summary>校验一份台账；任何缺项、重复、未知 owner/分区、空字段或不支持处置都抛异常。</summary>
    public static void Validate(IReadOnlyList<BranchSite> sites)
    {
        ArgumentNullException.ThrowIfNull(sites);
        if (sites.Count != ExpectedTotal)
            throw new BranchConvergenceException($"convergence ledger must cover {ExpectedTotal} sites, got {sites.Count}");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var perArea = new Dictionary<BranchArea, int>();
        foreach (BranchSite site in sites)
        {
            if (string.IsNullOrWhiteSpace(site.Id))
                throw new BranchConvergenceException("a branch site has no id");
            if (!seen.Add(site.Id))
                throw new BranchConvergenceException($"duplicate branch site id '{site.Id}'");
            if (!Enum.IsDefined(site.Area))
                throw new BranchConvergenceException($"{site.Id} has an undefined area");
            if (!Enum.IsDefined(site.Disposition))
                throw new BranchConvergenceException($"{site.Id} has an undefined disposition");
            if (!KnownOwners.Contains(site.Owner))
                throw new BranchConvergenceException($"{site.Id} has unknown owner '{site.Owner}'");
            if (string.IsNullOrWhiteSpace(site.Location))
                throw new BranchConvergenceException($"{site.Id} has no location");
            if (string.IsNullOrWhiteSpace(site.Note))
                throw new BranchConvergenceException($"{site.Id} has no disposition note");

            perArea[site.Area] = perArea.TryGetValue(site.Area, out int count) ? count + 1 : 1;
        }

        foreach ((BranchArea area, int expected) in ExpectedAreaCounts)
        {
            perArea.TryGetValue(area, out int actual);
            if (actual != expected)
                throw new BranchConvergenceException($"area {area} must have {expected} sites, got {actual}");
        }
    }

    static IReadOnlyList<BranchSite> Build()
    {
        var sites = new List<BranchSite>();
        sites.AddRange(IdentitySites());
        sites.AddRange(FeatureGateSites());
        sites.AddRange(HardcodeSites());
        sites.AddRange(PersistenceSites());
        sites.AddRange(UngatedSites());
        return sites;
    }

    static IEnumerable<BranchSite> IdentitySites()
    {
        yield return new("A1", BranchArea.IdentitySources, "MechrevoDeviceCapabilities.cs:151-203", BranchDisposition.IdentityLayer, "T6",
            "服务写入的 ItemSupport 是唯一画像来源；只读、不重推。");
        yield return new("A2", BranchArea.IdentitySources, "MechrevoDeviceCapabilities.cs:334-347", BranchDisposition.IdentityLayer, "T1",
            "SMBIOS 身份（厂牌/型号/家族/SKU/board/BIOS）经注册表读取，供识别与诊断。");
        yield return new("A3", BranchArea.IdentitySources, "MechrevoDeviceCapabilities.cs:256", BranchDisposition.IdentityLayer, "T3",
            "BIOS_PROJECT_ID 仅作佐证，绝不参与 F3 支持判定（身份只由 EC 决定）。");
        yield return new("A4", BranchArea.IdentitySources, "AppConfig.cs:279-294", BranchDisposition.IdentityLayer, "T1",
            "WMI Win32_ComputerSystem.Model 用于兼容旧谓词；不作为矩阵判据。");
        yield return new("A5", BranchArea.IdentitySources, "AppConfig.cs:304-336", BranchDisposition.IdentityLayer, "T1",
            "WMI BIOS 版本解析；Mechrevo 三段串刻意不拆。");
        yield return new("A6", BranchArea.IdentitySources, "MechrevoDeviceCapabilities.cs:349-351", BranchDisposition.IdentityLayer, "T1",
            "品牌门 IsMechrevo 仅诊断用途，无启动门控消费点。");
        yield return new("A7", BranchArea.IdentitySources, "MechrevoDeviceCapabilities.cs:303-332", BranchDisposition.IdentityLayer, "T1",
            "Logo light 注册表布局探测（诊断/能力佐证）。");
        yield return new("A8", BranchArea.IdentitySources, "EcChargeLimit.cs:46-48,166-198", BranchDisposition.IdentityLayer, "T7",
            "EC 读取探针（唯一获批 EC 写路径的同句柄事务读回）；不是身份判据。");
    }

    /// <summary>
    /// 41 个功能门站点 = 25 个功能族（身份条件化门控）+ 16 个族的独立子门。
    /// 每条都收敛到 <see cref="FeatureMatrix"/> / 运行时 Seen 位。
    /// </summary>
    static IEnumerable<BranchSite> FeatureGateSites()
    {
        yield return new("B1", BranchArea.FeatureGates, "MechrevoDeviceCapabilities.cs:285", BranchDisposition.MatrixGated, "T6", "TurboMode capability（TurboModeSupport）。");
        yield return new("B2", BranchArea.FeatureGates, "Settings.cs:3539,4828", BranchDisposition.MatrixGated, "T6", "Turbo 入口按能力位显隐。");
        yield return new("B3", BranchArea.FeatureGates, "MechrevoDeviceCapabilities.cs:71-74", BranchDisposition.MatrixGated, "T6", "静音狂暴三态（Supported/Unsupported/Unknown）。");
        yield return new("B4", BranchArea.FeatureGates, "MechrevoDeviceCapabilities.cs:289", BranchDisposition.MatrixGated, "T6", "IsTurboSubModeSupport 注册表位。");
        yield return new("B5", BranchArea.FeatureGates, "Settings.cs:3529,3544,4847", BranchDisposition.MatrixGated, "T6", "静音狂暴 UI 三处消费。");
        yield return new("B6", BranchArea.FeatureGates, "GPUModeControl.cs:26-41", BranchDisposition.MatrixGated, "T6", "GPU 模式面板可见性 = ProfileAvailable/SettingStatusSeen + Supports*。");
        yield return new("B7", BranchArea.FeatureGates, "MechrevoHw.cs:448", BranchDisposition.MatrixGated, "T6", "dGPU-direct 运行时状态优先于静态能力位。");
        yield return new("B8", BranchArea.FeatureGates, "MechrevoHw.cs:232", BranchDisposition.MatrixGated, "T6", "dGPU-direct 静态能力位（DGpuDirectConnectionSupport/MuxSwitchSupport）。");
        yield return new("B9", BranchArea.FeatureGates, "MechrevoService.cs:598-600", BranchDisposition.MatrixGated, "T6", "dGPU-direct 服务侧门控。");
        yield return new("B10", BranchArea.FeatureGates, "MechrevoHw.cs:449", BranchDisposition.MatrixGated, "T6", "iGPU-only 运行时状态。");
        yield return new("B11", BranchArea.FeatureGates, "MechrevoHw.cs:210", BranchDisposition.MatrixGated, "T6", "iGPU-only 静态能力位（iGPUModeOnlySupport）。");
        yield return new("B12", BranchArea.FeatureGates, "MechrevoDeviceCapabilities.cs:293-295", BranchDisposition.MatrixGated, "T9", "热切换门 = 两服务值（缺一不提供）；APVersionCheck 代理已删。");
        yield return new("B13", BranchArea.FeatureGates, "MechrevoService.cs:1070", BranchDisposition.MatrixGated, "T9", "热切换服务侧判定。");
        yield return new("B14", BranchArea.FeatureGates, "MechrevoHw.cs:450", BranchDisposition.MatrixGated, "T9", "热切换运行时消费。");
        yield return new("B15", BranchArea.FeatureGates, "GPUModeControl.cs:136", BranchDisposition.MatrixGated, "T9", "热切换 UI 预检（标准→核显）。");
        yield return new("B16", BranchArea.FeatureGates, "MechrevoHw.cs:451", BranchDisposition.MatrixGated, "T6", "键盘页运行时门（Keyboard/KeyboardStatusSeen）。");
        yield return new("B17", BranchArea.FeatureGates, "MechrevoDeviceCapabilities.cs:265-266", BranchDisposition.MatrixGated, "T6", "键盘注册表能力（KeyboardSupport/KeyboardType）。");
        yield return new("B18", BranchArea.FeatureGates, "Settings.cs:2829", BranchDisposition.MatrixGated, "T6", "键盘 UI 入口。");
        yield return new("B19", BranchArea.FeatureGates, "MechrevoService.cs:1580,2265", BranchDisposition.MatrixGated, "T6", "键盘服务侧门控。");
        yield return new("B20", BranchArea.FeatureGates, "KeyboardLightPathPolicy.cs:10-12", BranchDisposition.MatrixGated, "T6", "键盘→GCU 回退仅在 HID 确定不支持且服务在线。");
        yield return new("B21", BranchArea.FeatureGates, "MechrevoHw.cs:452-453", BranchDisposition.MatrixGated, "T6", "灯带/Logo 灯运行时门。");
        yield return new("B22", BranchArea.FeatureGates, "MechrevoHw.cs:283-290", BranchDisposition.MatrixGated, "T6", "灯带/Logo 自报位（HidLightbar/Status）。");
        yield return new("B23", BranchArea.FeatureGates, "Settings.cs:2830-2831; Program.cs:1034-1035", BranchDisposition.MatrixGated, "T6", "灯带/Logo UI 与启动恢复消费。");
        yield return new("B24", BranchArea.FeatureGates, "MechrevoHw.cs:454,747-748", BranchDisposition.MatrixGated, "T6", "液冷门（LiquidCooling/LcStatusSeen）。");
        yield return new("B25", BranchArea.FeatureGates, "MechrevoService.cs:1765,1941,2010", BranchDisposition.MatrixGated, "T6", "液冷服务侧第二门（LcGcuControllable）。");
        yield return new("B26", BranchArea.FeatureGates, "MechrevoHw.cs:455", BranchDisposition.MatrixGated, "T6", "刷新率能力门。");
        yield return new("B27", BranchArea.FeatureGates, "MechrevoService.cs:1314", BranchDisposition.MatrixGated, "T6", "刷新率运行时（GpuDeviceStatusSeen + HzList）。");
        yield return new("B28", BranchArea.FeatureGates, "MechrevoHw.cs:456", BranchDisposition.MatrixGated, "T34", "色彩校准运行时门。");
        yield return new("B29", BranchArea.FeatureGates, "MechrevoService.cs:618", BranchDisposition.MatrixGated, "T34", "色彩校准服务侧切换门。");
        yield return new("B30", BranchArea.FeatureGates, "MechrevoHw.cs:460-463", BranchDisposition.MatrixGated, "T6", "局部调光/LCD overdrive：registry OR Seen，且 *Support != false 否决。");
        yield return new("B31", BranchArea.FeatureGates, "AsusACPI.cs:158,169-170", BranchDisposition.MatrixGated, "T29", "局部调光 ASUS 适配层残留，改由能力位驱动。");
        yield return new("B32", BranchArea.FeatureGates, "MechrevoService.cs:1287,1301", BranchDisposition.MatrixGated, "T6", "局部调光服务侧门控。");
        yield return new("B33", BranchArea.FeatureGates, "MechrevoHw.cs:464", BranchDisposition.MatrixGated, "T6", "风扇增压运行时门。");
        yield return new("B34", BranchArea.FeatureGates, "MechrevoService.cs:2238; Settings.cs:1870", BranchDisposition.MatrixGated, "T21", "风扇增压服务与 UI 门。");
        yield return new("B35", BranchArea.FeatureGates, "MechrevoHw.cs:1178-1210", BranchDisposition.MatrixGated, "T8", "内置曲线仅在服务未显式否掉风扇设置时套用。");
        yield return new("B36", BranchArea.FeatureGates, "MechrevoHw.cs:518-521,563-566", BranchDisposition.MatrixGated, "T6", "GPU 超频运行时报告（LchwocSupportReported）。");
        yield return new("B37", BranchArea.FeatureGates, "MechrevoService.cs:516-520; CustomModeForm.cs:982", BranchDisposition.MatrixGated, "T6", "GPU 超频能力/可调偏移。");
        yield return new("B38", BranchArea.FeatureGates, "MechrevoHw.cs:714,724-726", BranchDisposition.MatrixGated, "T6", "CPU 高级性能菜单门。");
        yield return new("B39", BranchArea.FeatureGates, "MechrevoHw.cs:677-717", BranchDisposition.MatrixGated, "T6", "快捷开关表（多为 Seen，ac 恢复走注册表位）。");
        yield return new("B40", BranchArea.FeatureGates, "MechrevoHw.cs:466-468,483-485", BranchDisposition.MatrixGated, "T21", "PL/TGP/DB 可调范围由运行时 Fan/Status 探针决定。");
        yield return new("B41", BranchArea.FeatureGates, "MechrevoHw.cs:473", BranchDisposition.MatrixGated, "T21", "风扇切换灵敏度只认 FanSwitchSpeedSeen。");
    }

    static readonly IReadOnlyDictionary<string, string> HardcodeLocations = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["C1"] = "EcChargeLimit.cs:91-100", ["C2"] = "KeyboardRgb.cs:12-13", ["C3"] = "KeyboardRgb.cs:184-193",
        ["C4"] = "KeyboardRgb.cs:20-50", ["C5"] = "AsusACPI.cs:55-56", ["C6"] = "MechrevoDeviceCapabilities.cs:293-295",
        ["C7"] = "AppConfig.cs:508-511", ["C8"] = "AppConfig.cs:517-521", ["C9"] = "AppConfig.cs:523-526",
        ["C10"] = "AppConfig.cs:528-531", ["C11"] = "AppConfig.cs:533-536", ["C12"] = "AppConfig.cs:538-541",
        ["C13"] = "AppConfig.cs:543-546", ["C14"] = "NvidiaSmi.cs:9-11", ["C15"] = "NvidiaSmi.cs:9-12",
        ["C16"] = "NvidiaSmi.cs:9-14", ["C17"] = "WaterCoolerBle.cs:79-81", ["C18"] = "WaterCoolerBle.cs:28-35,578-584",
        ["C19"] = "MechrevoHw.cs:474-482", ["C20"] = "MechrevoHw.cs:1178-1210", ["C21"] = "MechrevoHw.cs:283-290",
        ["C22"] = "MechrevoDeviceCapabilities.cs:222,307-309",
    };

    static readonly IReadOnlyDictionary<string, string> HardcodeOwners = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["C1"] = "T7", ["C14"] = "T8", ["C15"] = "T8", ["C16"] = "T8", ["C19"] = "T21", ["C20"] = "T8",
    };

    static IEnumerable<BranchSite> HardcodeSites()
    {
        foreach (HardcodeEntry entry in HardcodeDispositions.Items)
        {
            BranchDisposition disposition = entry.Disposition == HardcodeDisposition.MatrixGated
                ? BranchDisposition.MatrixGated
                : BranchDisposition.KeptWithReason;
            string owner = HardcodeOwners.TryGetValue(entry.Item, out string? mapped) ? mapped : "T9";
            string location = HardcodeLocations.TryGetValue(entry.Item, out string? loc) ? loc : "AppConfig.cs";
            yield return new BranchSite(entry.Item, BranchArea.Hardcodes, location, disposition, owner, entry.Note);
        }
    }

    static IEnumerable<BranchSite> PersistenceSites()
    {
        yield return new("D1", BranchArea.Persistence, "AppConfig.cs:37-59,231-269", BranchDisposition.IdentityLayer, "T5",
            "config.json 机器级键改为按机型作用域；一次性迁移。");
        yield return new("D2", BranchArea.Persistence, "KeyboardRgb.cs:1085,1140-1165", BranchDisposition.KeptWithReason, "T5",
            "rgb.cfg 是设备态，运行时探测，跨机型无害。");
        yield return new("D3", BranchArea.Persistence, "LightingSettingsStore.cs:116-126", BranchDisposition.KeptWithReason, "T5",
            "灯带/Logo 配置按主题存储，运行时设备校验。");
        yield return new("D4", BranchArea.Persistence, "MechrevoDeviceCapabilities.cs:88-120", BranchDisposition.MatrixGated, "T28",
            "进程内能力快照生命周期（Invalidate 无调用者）在 T28 明确定义并接线。");
        yield return new("D5", BranchArea.Persistence, "AppConfig.cs:41,256-269", BranchDisposition.IdentityLayer, "T5",
            "ProgramData 回退配置最易跨机型残留，随 D1 一同按机型作用域化。");
    }

    static IEnumerable<BranchSite> UngatedSites()
    {
        yield return new("E1", BranchArea.UngatedConsumers, "AppConfig.cs:558", BranchDisposition.MatrixGated, "T8",
            "IsForceSetGPUMode 的裸子串已删，改配置开关；消费点走矩阵能力位。");
        yield return new("E2", BranchArea.UngatedConsumers, "GPUModeControl.cs:70,314,585; ModeControl.cs:193,282", BranchDisposition.MatrixGated, "T29",
            "ASUS 残留谓词在活跃路径的清理（T29 取证后 CORRECTED/KEPT_WITH_REASON）。");
        yield return new("E3", BranchArea.UngatedConsumers, "Settings.cs:4156", BranchDisposition.MatrixGated, "T29",
            "NoGpu() 反转可见性修正（T29 取证后 CORRECTED）。");
        yield return new("E4", BranchArea.UngatedConsumers, "NvidiaSmi.cs:9-15", BranchDisposition.MatrixGated, "T8",
            "175W 兜底删除；默认功耗透传运行时 GpuTgpMaximum，无报告 = -1。");
        yield return new("E5", BranchArea.UngatedConsumers, "MechrevoHw.cs:474-482", BranchDisposition.MatrixGated, "T21",
            "风扇切换范围逐机型门控归 T21（支撑判据保持运行时）。");
        yield return new("E6", BranchArea.UngatedConsumers, "MechrevoHw.cs:1178-1210", BranchDisposition.MatrixGated, "T8",
            "一刀切曲线按 FanSettingsSupport 门控；逐机型表归 Wave C。");
        yield return new("E7", BranchArea.UngatedConsumers, "AppConfig.cs:498-501,596-599", BranchDisposition.MatrixGated, "T8",
            "OLED/动态照明假探测器删除，改显式配置开关。");
        yield return new("E8", BranchArea.UngatedConsumers, "GpuSwitchPolicy.cs:23-36; MechrevoHw.cs:2444", BranchDisposition.MatrixGated, "T8",
            "策略恒 Restart 经证实为刻意产品决策（T8 证据 E8），保留并有测试锁定。");
    }
}
