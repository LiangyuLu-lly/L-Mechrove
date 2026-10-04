namespace MechrevoLite;

using MechrevoLite.Helpers;
using MechrevoLite.Mode;
using Microsoft.Win32;
using System.Management;
using System.Text.Json;
using System.Text.RegularExpressions;

public static class AppConfig
{

    private static string configFile;
    private static string fallbackConfigFile;
    // 环境旁路生效时置位：机器级回退文件（ProgramData）既不回写、也不读取。
    private static bool configFileOverridden;

    private static Dictionary<string, object> config = new Dictionary<string, object>();
    private static System.Timers.Timer timer = new System.Timers.Timer(2000) { AutoReset = false };
    private static readonly object configLock = new();
    private static readonly object writeLock = new();

    /// <summary>Flush() 实际落盘的次数（测试接缝）：档位切换要求每次成功切换恰好一次落盘。</summary>
    internal static int FlushCount;

    private static readonly JsonSerializerOptions LenientOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private static readonly Regex KeyValueRegex = new(
        @"""((?:\\.|[^""\\])*)""\s*:\s*(""(?:\\.|[^""\\])*""|-?\d+(?:\.\d+)?|true|false|null)");

    static AppConfig()
    {
        string configName = "config.json";
        string appPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite");
        string startupConfig = Path.Combine(Application.StartupPath.Trim('\\'), configName);

        fallbackConfigFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MechrevoLite", configName);

        // 测试/诊断旁路：指向临时配置文件，避免测试套件改动真实用户配置
        //（与 LightingSettingsStore 的 LMECHREVO_LIGHT_CFG_DIR 同一先例）。
        string? configOverride = Environment.GetEnvironmentVariable("LMECHREVO_CONFIG_FILE");
        configFile = string.IsNullOrWhiteSpace(configOverride)
            ? ResolveConfigPath(
                startupConfig,
                fallbackConfigFile,
                Path.Combine(appPath, configName),
                ProcessHelper.IsRunningAsSystem(),
                File.Exists)
            : configOverride!;

        configFileOverridden = !string.IsNullOrWhiteSpace(configOverride);
        Directory.CreateDirectory(Path.GetDirectoryName(configFile) ?? appPath);
        Directory.CreateDirectory(appPath);

        // 旁路下也不从机器级回退文件读：它是真实用户配置的镜像（含统计安装编号），临时配置坏掉时
        // 退回它，测试 / 诊断进程就会带着真实用户的设置与身份运行。
        if (!TryLoadConfig(configFile) && !TryRecoverConfig(configFile) && !TryLoadConfig(configFile + ".bak") &&
            (configFileOverridden || !TryLoadConfig(fallbackConfigFile))) Init();

        timer.Elapsed += Timer_Elapsed;
    }

    /// <summary>
    /// 选择配置文件位置，优先级：
    /// 1. 程序目录旁的 config.json —— 便携部署（本项目主要的分发方式）；
    /// 2. SYSTEM 身份运行时的 ProgramData 副本 —— 开机计划任务读不到用户的 AppData；
    /// 3. 用户 AppData —— 常规安装。
    /// 抽成纯函数是为了让这条优先级可测：走错分支会导致「设置改了但重启后丢」这类
    /// 只有在特定部署形态下才复现的问题。
    /// </summary>
    internal static string ResolveConfigPath(
        string startupConfig,
        string fallbackConfig,
        string appDataConfig,
        bool runningAsSystem,
        Func<string, bool> fileExists)
    {
        if (fileExists(startupConfig)) return startupConfig;
        if (runningAsSystem && fileExists(fallbackConfig)) return fallbackConfig;
        return appDataConfig;
    }

    /// <summary>
    /// 导出诊断包用：当前生效/可用的配置文件（真实路径 + zip 内文件名 + 一句话说明）。
    /// 走环境变量旁路（测试/诊断）时不返回 ProgramData 回退文件，避免泄漏它机配置。
    /// </summary>
    internal static IReadOnlyList<(string SourcePath, string FileName, string Description)> ExportableConfigFiles()
    {
        var files = new List<(string, string, string)>();
        void Add(string path, string fileName, string description)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (files.Any(file => string.Equals(file.Item1, path, StringComparison.OrdinalIgnoreCase))) return;
            files.Add((path, fileName, description));
        }

        Add(configFile, Path.GetFileName(configFile),
            "主配置：性能/显卡模式、灯效、快捷开关、窗口外观等设置项");
        Add(configFile + ".bak", Path.GetFileName(configFile) + ".bak",
            "主配置上一次成功写入的备份（原子替换留下）");
        if (!configFileOverridden)
            Add(fallbackConfigFile, "config.fallback.json",
                "机器级回退配置（ProgramData，供 SYSTEM/计划任务身份读取）");
        return files;
    }

    /// <summary>
    /// 宽松反序列化。返回 null 表示这份内容不可用（空、字面量 null、语法错误）。
    /// 过去调用方直接把结果赋给 config 字段，内容为字面量 "null" 时后续每一次
    /// config.TryGetValue 都会抛 NullReferenceException。
    /// </summary>
    internal static Dictionary<string, object>? DeserializeConfig(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, object>>(json, LenientOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 从损坏的 JSON 里正则抢救「"键": 标量」对。用于文件被截断或尾部写坏的情况——
    /// 整体反序列化失败时，前面完好的键值仍然值得保留。
    /// 后出现的同名键覆盖先出现的，与 JSON 的后者优先语义一致。
    /// </summary>
    internal static Dictionary<string, string> RecoverKeyValuePairs(string? json)
    {
        var pairs = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(json)) return pairs;
        foreach (Match m in KeyValueRegex.Matches(json))
            pairs["\"" + m.Groups[1].Value + "\""] = m.Groups[2].Value;
        return pairs;
    }

    /// <summary>把抢救出来的键值对拼回一个合法 JSON 对象。</summary>
    internal static string RebuildJson(IDictionary<string, string> pairs) =>
        "{" + string.Join(",", pairs.Select(p => p.Key + ":" + p.Value)) + "}";

    /// <summary>抢救流程的纯函数版本：损坏文本 -> 可用字典，抢救不到任何东西时返回 null。</summary>
    internal static Dictionary<string, object>? RecoverConfig(string? json)
    {
        var pairs = RecoverKeyValuePairs(json);
        if (pairs.Count == 0) return null;
        return DeserializeConfig(RebuildJson(pairs));
    }

    private static bool TryLoadConfig(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var loaded = DeserializeConfig(File.ReadAllText(path));
            if (loaded is null)
            {
                Logger.WriteLine($"Config at {path} deserialized to null; treating it as unreadable.");
                return false;
            }
            config = loaded;
            Logger.WriteLine($"Config loaded from {path}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Broken config {path}: {ex.Message}");
            return false;
        }
    }

    private static bool TryRecoverConfig(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var recovered = RecoverConfig(File.ReadAllText(path));
            if (recovered is null) return false;
            config = recovered;
            Logger.WriteLine($"Recovered {recovered.Count} values from broken config {path}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Config recovery failed {path}: {ex.Message}");
            return false;
        }
    }

    private static void Timer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
    {
        timer.Stop();
        Persist();
    }

    private static bool Persist()
    {
        lock (writeLock)
        {
            try
            {
                string jsonString;
                lock (configLock) jsonString = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
                WriteAtomic(configFile, jsonString);
                SyncFallbackConfig();
                return true;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Config write failed: " + ex.Message);
                return false;
            }
        }
    }

    public static void Flush()
        => TryFlush();

    internal static bool TryFlush()
    {
        FlushCount++;
        timer.Stop();
        return Persist();
    }

    public static void Shutdown()
    {
        Flush();
        timer.Dispose();
    }

    /// <summary>
    /// 原子写：先落 .tmp 并 flush 到盘，再用 File.Replace 换入，旧内容留成 .bak。
    /// 这样任何时刻断电都至少有一份完好文件（目标文件或 .bak），配合
    /// <see cref="TryLoadConfig"/> 的 .bak 兜底构成完整的降级链。
    /// </summary>
    internal static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.Write))
            fs.Flush(flushToDisk: true);
        if (File.Exists(path))
            File.Replace(tmp, path, path + ".bak");
        else
            File.Move(tmp, path);
    }

    /// <summary>
    /// 是否需要把主配置复制到 ProgramData 副本。
    /// 目标为空、与主配置同路径、或没有可创建的父目录（例如 "C:\"）时都不做。
    /// 最后一条是必要的：Path.GetDirectoryName 对根路径返回 null，
    /// Directory.CreateDirectory(null) 会抛 ArgumentNullException。
    /// </summary>
    internal static bool ShouldSyncFallback(string? fallbackConfig, string? currentConfig)
    {
        if (string.IsNullOrEmpty(fallbackConfig)) return false;
        if (string.Equals(fallbackConfig, currentConfig, StringComparison.OrdinalIgnoreCase)) return false;
        return !string.IsNullOrEmpty(Path.GetDirectoryName(fallbackConfig));
    }

    private static void SyncFallbackConfig()
    {
        if (configFileOverridden) return;   // 测试/诊断旁路：绝不触碰机器级回退配置
        if (!ShouldSyncFallback(fallbackConfigFile, configFile)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fallbackConfigFile)!);
            File.Copy(configFile, fallbackConfigFile, overwrite: true);
        }
        catch (Exception)
        {
            //Logger.WriteLine("Can't sync fallback config: " + ex.Message);
        }
    }

    // Model Detection Routine

    private static readonly Lazy<string> _model =
        new Lazy<string>(LoadModel, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<(string Bios, string ModelShort)> _biosData =
        new Lazy<(string, string)>(LoadBios, LazyThreadSafetyMode.ExecutionAndPublication);

    private static string LoadModel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("Select * from Win32_ComputerSystem");
            foreach (var obj in searcher.Get())
            {
                using (obj) return obj["Model"]?.ToString() ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.Message);
        }
        return string.Empty;
    }

    /// <summary>
    /// 解析 Win32_BIOS.SMBIOSBIOSVersion。
    /// ASUS 的约定是「机型代号.BIOS 版本号」（GX650PY.317），g-helper 原样按 '.' 切分后
    /// 取 parts[0] 当机型、parts[1] 当 BIOS 版本。机械革命的 BIOS 串是 "N.1.32MRO60"
    /// 这类三段式，旧逻辑会退化成 (Bios="1", ModelShort="N") —— 两个都是假值。
    /// 现在只在确实符合「恰好两段 + 首段含字母且够长」时才拆分；否则整串原样作为
    /// BIOS 版本返回、机型留空。调用方拿到的要么是真值要么是空，不会是似真而假的值。
    /// </summary>
    internal static (string Bios, string ModelShort) ParseBiosVersion(string? raw)
    {
        string text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) return (string.Empty, string.Empty);

        string[] parts = text.Split('.');
        if (parts.Length != 2) return (text, string.Empty);

        string model = parts[0].Trim();
        string bios = parts[1].Trim();

        // 机型代号必须含字母且有一定长度，排除 "1.32" 这类纯版本号被误当成机型。
        if (bios.Length == 0 || model.Length < 4 || !model.Any(char.IsLetter)) return (text, string.Empty);

        return (bios, model);
    }

    private static (string Bios, string ModelShort) LoadBios()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_BIOS");
            foreach (var obj in searcher.Get())
            {
                using (obj) return ParseBiosVersion(obj["SMBIOSBIOSVersion"]?.ToString());
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine(ex.Message);
        }
        return (string.Empty, string.Empty);
    }

    public static string GetModel() => _model.Value;

    public static (string, string) GetBiosAndModel() => (_biosData.Value.Bios, _biosData.Value.ModelShort);

    public static bool ContainsModel(string contains)
        => _model.Value.Contains(contains, StringComparison.OrdinalIgnoreCase);

    private static void Init()
    {
        config = new Dictionary<string, object>();
        config["performance_mode"] = 0;
        config["log_level"] = "off";   // 日志级别：off（默认，零磁盘磨损）/ error / all
        string jsonString = JsonSerializer.Serialize(config);
        File.WriteAllText(configFile, jsonString);
    }

    public static bool Exists(string name)
    {
        lock (configLock) return config.ContainsKey(name);
    }

    public static int Get(string name, int empty = -1)
    {
        lock (configLock)
            return config.TryGetValue(name, out var val) && int.TryParse(val?.ToString(), out int result)
            ? result : empty;
    }

    public static bool Is(string name)
    {
        return Get(name) == 1;
    }

    public static bool IsNotFalse(string name)
    {
        return Get(name) != 0;
    }

    public static string? GetString(string name, string? empty = null)
    {
        lock (configLock)
            return config.TryGetValue(name, out var val) ? val?.ToString() : empty;
    }

    private static void Write()
    {
        timer.Stop();
        timer.Start();
    }

    public static void Set(string name, int value)
    {
        lock (configLock) config[name] = value;
        Write();
    }

    public static void Set(string name, string value)
    {
        lock (configLock) config[name] = value;
        Write();
    }

    public static void Remove(string name)
    {
        lock (configLock) config.Remove(name);
        Write();
    }

    /// <summary>
    /// 当前配置的只读快照（机型作用域一次性迁移与测试用）。返回副本，不暴露可变字典。
    /// </summary>
    internal static IReadOnlyDictionary<string, object> Snapshot()
    {
        lock (configLock) return new Dictionary<string, object>(config);
    }

    public static void RemoveMode(string name)
    {
        Remove(name + "_" + Modes.GetCurrent());
    }

    public static string GgetParamName(AsusFan device, string paramName = "fan_profile")
    {
        int mode = Modes.GetCurrent();
        string name;

        switch (device)
        {
            case AsusFan.GPU:
                name = "gpu";
                break;
            case AsusFan.Mid:
                name = "mid";
                break;
            default:
                name = "cpu";
                break;
        }

        return paramName + "_" + name + "_" + mode;
    }

    public static byte[] StringToBytes(string str)
    {
        String[] arr = str.Split('-');
        byte[] array = new byte[arr.Length];
        for (int i = 0; i < arr.Length; i++) array[i] = Convert.ToByte(arr[i], 16);
        return array;
    }

    public static string? GetModeString(string name)
    {
        return GetString(name + "_" + Modes.GetCurrent());
    }

    public static int GetMode(string name, int empty = -1)
    {
        return Get(name + "_" + Modes.GetCurrent(), empty);
    }

    public static bool IsMode(string name)
    {
        return Get(name + "_" + Modes.GetCurrent()) == 1;
    }

    public static void SetMode(string name, int value)
    {
        Set(name + "_" + Modes.GetCurrent(), value);
    }

    public static void SetMode(string name, string value)
    {
        Set(name + "_" + Modes.GetCurrent(), value);
    }

    // 这里曾经有一整片按 ASUS 机型串做子串匹配的谓词，全部删除：
    //   IsAlly()（RC7，ROG Ally 掌机）、IsTUF()、IsVivoZenPro()、IsDUO()、
    //   IsSlash() / IsSlashLong()（GA403/GU605 那批 Slash 灯条机型）、
    //   IsZ13() / HasRearLight()（Z13 的机背灯）、IsChargeLimit6080()
    //
    // 它们的判据是 ContainsModel("TUF") 之类，对机械革命机器一律为假；
    // 消费点也随之全部删除（ROG Ally 面板、Slash 点阵屏、Splendid 色域通路、机背灯）。
    //
    // **留着不只是没用，还有正确性风险**：IsChargeLimit6080() 的候选里有 "H760"，
    // 万一某台机械革命的机型串恰好命中，它就会在写入充电上限之前先按 ASUS 那套
    // （>85→100 / >=80→80 / <60→60）改一遍 limit，得到与预期不同的值。
    // 现在充电上限走的是 EC 直写（BatteryControl.SetBatteryChargeLimit → EcChargeLimit），
    // 不做任何机型串判定的档位对齐。

    /// <summary>
    /// 屏幕是不是 OLED。
    ///
    /// 原实现是「ASUS 机型串命中一张 30 多项的清单」或「ASUS OLEDCare 注册表里
    /// EnablePixelRefresh 非零」，两条判据在机械革命上都不成立，于是恒 false。
    /// 机械革命确实有 OLED 屏机型，但目前**还没有可靠的判据来源**：
    /// GCU 的状态帧里没有面板类型字段，注册表里也没有对应的能力位。
    ///
    /// 所以这里只保留手动开关：拿到真实判据之前，由用户在配置里显式声明。
    /// 唯一的消费者是 AmdDisplay.IsOledPowerOptimization()（AMD 驱动侧的 OLED 省电优化）。
    /// </summary>
    public static bool IsOLED()
    {
        return Is("oled");
    }

    public static bool IsNoOverdrive()
    {
        return Is("no_overdrive");
    }

    public static bool IsApplyPower() => IsMode("auto_apply_power");
    public static bool IsApplyFans() => IsMode("auto_apply");
    public static bool IsApplyUV() => IsMode("auto_uv");

    /// <summary>
    /// T29：ASUS 机型串已删。原判据「applyPower 且（manual_mode 或 G733 机型）」在机械革命机型上恒假；
    /// 现在只认配置开关 <c>manual_mode</c>，保留原有逃生口。
    /// </summary>
    public static bool IsManualModeRequired()
    {
        if (!IsApplyPower()) return false;
        return Is("manual_mode");
    }

    /// <summary>T29：ASUS 机型串已删，只认配置开关（原先恒假）。</summary>
    public static bool IsFanRequired() => Is("fan_required");

    /// <summary>T29：ASUS 机型串已删，只认配置开关。</summary>
    public static bool IsModeReapplyRequired() => Is("mode_reapply");

    /// <summary>T29：ASUS 机型串已删；保留 <c>shutdown_gpu</c> 配置语义。</summary>
    public static bool IsStandardModeFix() => Is("shutdown_gpu");

    public static bool IsNVPlatform()
    {
        return Is("nv_platform");
    }

    /// <summary>
    /// 强制 GPU 模式只认配置开关。原先还有一条裸子串机型分支（ASUS 残留）：任何机型名命中该子串
    /// 就会在写入前先按 ASUS 那套改一遍 GPU 模式。真正的机型门控由消费点
    /// <c>GPUModeControl.AutoGPUMode</c> 依赖矩阵能力位（SupportsDgpuDirect/SupportsIgpuOnly）承担。
    /// </summary>
    public static bool IsForceSetGPUMode() => Is("gpu_mode_force_set");

    // IsAMDiGPU() 已删除：唯一调用点在 SettingsForm.VisualizeXGM 里，
    // 随 XG Mobile 外置显卡坞那一族一起删掉了。它的判据同样是 ASUS 机型串。

    /// <summary>
    /// T29：ASUS 机型串（UX540/M560/GZ302/FA401EA/HN7306EA）已删。语义 = 用户显式声明本机无独显；
    /// 屏幕面板可见性不再挂在这个谓词上（见 <c>UI\ScreenPanelVisibility</c>）。
    /// </summary>
    public static bool NoGpu() => Is("no_gpu");

    public static bool IsOverlay()
    {
        return Is("overlay");
    }

    public static bool IsOverlayGameOnly()
    {
        return Is("overlay_game_only");
    }

    /// <summary>
    /// Windows 动态照明（Dynamic Lighting）。
    ///
    /// 原判据是 IsSlash() || IsIntelHX() || IsTUF() || IsZ13()——「2024 款 ASUS 机型」，
    /// 在机械革命上恒假。动态照明本身是 Windows 11 的通用特性，不是 ASUS 专有，
    /// 所以保留这个入口、只把判据换成配置开关，等拿到机械革命侧的判据再接。
    /// 消费者：SettingsForm.LabelBacklight_Click 与 _VisualiseAura。
    /// </summary>
    public static bool IsDynamicLighting()
    {
        return Is("dynamic_lighting");
    }

    public static bool IsDynamicLightingOnly()
    {
        return Is("dynamic_lighting_only");
    }

    public static bool IsDynamicLightingInit()
    {
        return Is("lighting_init");
    }

    public static bool IsAutoASPM()
    {
        return IsNotFalse("aspm");
    }

}
