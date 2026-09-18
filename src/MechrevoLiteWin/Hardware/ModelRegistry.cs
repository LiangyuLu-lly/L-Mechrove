using Probe;

namespace MechrevoLite.Hardware;

/// <summary>身份来源。<see cref="Ec"/> 表示从 EC 读出并解析出厂商枚举名；其余一律 fail-closed 成 <see cref="Unknown"/>。</summary>
public enum ModelSource
{
    Unknown,
    Ec,
}

/// <summary>
/// 机型身份（**轴 1**，平台/机箱代号）。
/// <para><see cref="ProjectId"/> 是 <c>Enum.GetName(ProjectID, GetProject2ExID(GetProjectIdFromEC()))</c> 的结果，
/// 风扇表目录名即此字符串。</para>
/// <para><see cref="BiosProjectId"/> 是 EC 1868 经 6 位/4 位掩码解出的 <c>BIOS_PROJECT_ID</c> 名，
/// **只作佐证，绝不参与机型支持判定**（它与 <c>ProjectId</c> 是两套 ID，服务写入注册表的那份同名值亦同）。</para>
/// </summary>
public sealed record ModelIdentity(
    string ProjectId,
    int RawProjectByte,
    string BiosProjectId,
    ModelSource Source)
{
    public const string UnknownName = "Unknown";

    /// <summary>一切读不到 / 解析不了的身份都收敛到这里；消费方不得据"看起来像默认值"放行。</summary>
    public static readonly ModelIdentity Unknown = new(UnknownName, -1, UnknownName, ModelSource.Unknown);

    /// <summary>身份是否已解析成厂商枚举名。<c>false</c> 即 F3 的 <c>Unparsable</c>。</summary>
    public bool IsParsed => Source == ModelSource.Ec;
}

/// <summary>
/// 机型识别的**单一真源**（轴 1）。
///
/// <para>与厂商 <c>MyEcCtrl</c> 逐条等价：身份来自 EC 1856（project byte）与 EC 1868（OEM service
/// project byte），7 号/24 号族再经 <c>GetProject2ExID</c> 展开。所有 EC 访问都只经
/// <see cref="IEcReadTransport"/>——本类不含任何 IOCTL、DllImport 或写路径，构造上碰不到硬件。</para>
///
/// <para><b>它不是轴 2</b>：dGPU 代际由运行时探测（GPU 营销名 / PCI device-id 高字节）决定，
/// 绝不从平台代号推导。</para>
/// </summary>
public static class ModelRegistry
{
    /// <summary>project byte（<c>GetProjectIdFromEC</c>）。</summary>
    public const int ProjectByteAddress = 1856;

    /// <summary>OEM service project byte（<c>GetBiosProjctID</c>）。</summary>
    public const int BiosProjectByteAddress = 1868;

    // GetProject2ExID 的辅助字节；仅在 project byte 为 23 / 24（两个 PHx 族）时读取。
    const int SystemIdAddress = 1110;
    const int GpuModuleIdAddress = 2002;
    const int ModuleIdAddress = 2003;
    const int RomIdAddress = 1905;
    const int RomId2Address = 1906;
    const int AdapterWattAddress = 1183;
    const int SixBitIdAddress = 1994;

    /// <summary>ProjectID 枚举名（_decompiled/GCUService/GCUService.decompiled.cs；docs/hardware/project-ids.json）。</summary>
    static readonly IReadOnlyDictionary<int, string> ProjectIdNames = new Dictionary<int, string>
    {
        [0] = "None", [1] = "GI", [2] = "GJ", [3] = "GK", [4] = "GICN", [5] = "GJCN",
        [6] = "GK5CN_X", [7] = "GK7CN_S", [8] = "GK7CPCS_GK5CQ7Z", [9] = "PF",
        [10] = "GK5CP_4X_5X_6X", [11] = "IDP", [12] = "IDY_6Y", [13] = "IDY_7Y",
        [14] = "PF4MU_PF4MN_PF5MU", [15] = "CML_Gaming", [16] = "GK7NXXR", [17] = "GM5MU1Y",
        [18] = "PH4TRX1", [19] = "PH4TUX1", [20] = "PH4TQx1", [21] = "PH6TRX1", [22] = "PH6TQxx",
        [23] = "PHxAxxx", [24] = "PHxPxxx",
        [5889] = "PH4ARxx", [5890] = "PH4AUxx", [5891] = "PH4AXxx", [5892] = "PH6AQxx",
        [5893] = "PH6ARxx", [5894] = "PH6AGxx", [5895] = "PH4AUxf",
        [6145] = "PH4PRxx", [6146] = "PH4PUxx", [6147] = "PH4PGx1", [6148] = "PH4PGx2",
        [6149] = "PH6PRxx", [6150] = "PH6PGEx", [6151] = "PH6PG0x", [6152] = "PH6PG3x",
        [6153] = "PH6PG7x", [6154] = "PH4AQE3", [6155] = "PH6PG0x150W", [6156] = "PH6PG3x150W",
        [6157] = "PH6PG7x150W",
    };

    /// <summary>
    /// EC 1868 掩码后的值 -> <c>BIOS_PROJECT_ID</c> 名，逐行照抄厂商 switch
    /// （掩码 0 -&gt; IDR，1 -&gt; IDX，…，16 -&gt; ID2；switch 之外 -> NA）。
    /// 读不到 1868 本身是 <c>Unknown</c>，与"读得到但不在表内"的 NA 区分开。
    /// </summary>
    static readonly IReadOnlyDictionary<int, string> BiosProjectByteNames = new Dictionary<int, string>
    {
        [0] = "IDR", [1] = "IDX", [2] = "IDV", [3] = "IDO", [4] = "IDP", [5] = "IDS",
        [6] = "IDY", [7] = "IDW", [8] = "IDM", [9] = "IDE", [10] = "IDN", [11] = "IDZ",
        [12] = "IDXQ", [13] = "IDQ", [14] = "IDO3", [15] = "ID1", [16] = "ID2",
    };

    /// <summary>
    /// 读一次身份。project byte 读不到或解析不出厂商枚举名 -> <see cref="ModelIdentity.Unknown"/>。
    /// 传输层异常（驱动卡死/句柄失效）在此边界被记录并收敛，绝不上抛。
    /// </summary>
    public static ModelIdentity Read(IEcReadTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        try
        {
            int rawProject = transport.ReadByte(ProjectByteAddress);
            if (rawProject < 0) return ModelIdentity.Unknown;

            int projectId = rawProject & 0xFF;
            string biosName = ReadBiosProjectIdName(transport);
            int expanded = ExpandProjectId(projectId, transport);

            if (!ProjectIdNames.TryGetValue(expanded, out string? name))
                return new ModelIdentity(ModelIdentity.UnknownName, projectId, biosName, ModelSource.Unknown);

            return new ModelIdentity(name, projectId, biosName, ModelSource.Ec);
        }
        catch (Exception ex)
        {
            // EC 传输是外部边界：单次读的故障（超时、句柄失效、SEH）只作废本次身份，不上抛给 UI。
            Logger.WriteLine("ModelRegistry read failed: " + ex.Message);
            return ModelIdentity.Unknown;
        }
    }

    /// <summary>
    /// 这个名字是否是厂商 <c>ProjectID</c> 枚举的成员（注册表数据的一致性校验用）。
    /// <c>PH4AQxx</c> 有风扇表目录但不是枚举成员，故返回 <c>false</c>。
    /// </summary>
    internal static bool IsKnownProjectName(string name) => ProjectIdNames.Values.Contains(name, StringComparer.Ordinal);

    static string ReadBiosProjectIdName(IEcReadTransport transport)
    {
        int raw = transport.ReadByte(BiosProjectByteAddress);
        if (raw < 0) return ModelIdentity.UnknownName;

        int mask = (ReadByte(transport, SixBitIdAddress) & 0x01) != 0 ? 63 : 15;
        return BiosProjectByteNames.TryGetValue(raw & mask, out string? name) ? name : "NA";
    }

    /// <summary>
    /// 厂商 <c>GetProject2ExID</c> 的等价实现。非 23/24 的项目代号原样返回；
    /// 两个族各自按 1110/2002/2003/1905/1906/1183 的位组合展开。
    /// </summary>
    static int ExpandProjectId(int projectId, IEcReadTransport transport)
    {
        if (projectId != 23 && projectId != 24) return projectId;

        int systemId = ReadByte(transport, SystemIdAddress) & 0x80;
        int gpuModule = ReadByte(transport, GpuModuleIdAddress) & 0x1F;
        int moduleId = ReadByte(transport, ModuleIdAddress) & 0x01;
        int module2 = ReadByte(transport, ModuleIdAddress) & 0xF0;
        int romId = ReadByte(transport, RomIdAddress) & 0x01;
        int romId2 = ReadByte(transport, RomId2Address) & 0xFF;
        int adapterWatt = AdapterWattFor(ReadByte(transport, AdapterWattAddress));

        if (projectId == 23)
        {
            if (systemId == 0 && romId == 0)
            {
                if (moduleId == 0) return 5889;
                if (moduleId == 1) return romId2 == 3 ? 5895 : 5890;
            }
            else if (systemId == 128 && romId == 0) return 5891;
            else if (systemId == 0 && romId == 1)
            {
                if (moduleId == 0) return 5893;
            }
            else if (systemId == 128 && romId == 1)
            {
                return module2 != 48 && module2 != 112 && module2 != 160 ? 5892 : 5894;
            }
        }
        else
        {
            if (systemId == 0 && romId == 0)
            {
                if (moduleId == 0) return 6145;
                if (moduleId == 1) return 6146;
            }
            else if (systemId == 128 && romId == 0)
            {
                return romId2 switch { 1 => 6147, 4 => 6154, _ => 6148 };
            }
            else if (systemId == 0 && romId == 1) return 6149;
            else if (systemId == 128 && romId == 1)
            {
                switch (gpuModule)
                {
                    case 8: return 6150;
                    case 16:
                    case 17:
                    case 21: return adapterWatt != 150 ? 6151 : 6155;
                    case 12: return adapterWatt != 150 ? 6152 : 6156;
                    case 18: return adapterWatt != 150 ? 6153 : 6157;
                }
            }
        }
        return projectId;
    }

    /// <summary>厂商 <c>GetAdapterWattFromEC</c>：1183 &amp; 0x78 查表，表外默认 150 W。</summary>
    static int AdapterWattFor(int raw) => (raw & 0x78) switch
    {
        0 => 330,
        8 => 230,
        16 => 180,
        24 => 150,
        32 => 120,
        40 => 90,
        48 => 65,
        56 => 40,
        64 => 280,
        _ => 150,
    };

    /// <summary>读不到时按厂商语义返回 0（<c>ref byte</c> 保持 0），而不是让负数参与位运算。</summary>
    static int ReadByte(IEcReadTransport transport, int address)
    {
        int value = transport.ReadByte(address);
        return value < 0 ? 0 : value & 0xFF;
    }
}
