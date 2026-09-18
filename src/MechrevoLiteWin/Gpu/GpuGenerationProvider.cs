using Microsoft.Win32;

namespace MechrevoLite.Gpu;

/// <summary>代际解析结果的持久化接缝（生产走 config.json，测试走内存实现）。</summary>
public interface IDgpuGenerationStorage
{
    string? Get(string key);
    void Set(string key, string value);
}

/// <summary>默认存储：既有原子写配置（<c>AppConfig</c>）。只写配置文件，绝不写固件/NVRAM。</summary>
public sealed class AppConfigDgpuGenerationStorage : IDgpuGenerationStorage
{
    public string? Get(string key) => AppConfig.GetString(key);
    public void Set(string key, string value) => AppConfig.Set(key, value);
}

/// <summary>
/// 代际解析结果的持久化。**代际只在硬件变更时重解析**：指纹（显卡标识串）不变即复用上一次结果，
/// 避免每次启动都重新探测（探测含注册表读取，且真实机器上开关独显会抖动枚举）。
/// </summary>
public static class DgpuGenerationStore
{
    public const string GenerationKey = "dgpu_generation";
    public const string SourceKey = "dgpu_generation_source";
    public const string HasDgpuKey = "dgpu_has_dgpu";
    public const string FingerprintKey = "dgpu_generation_fingerprint";

    /// <summary>显卡集合的稳定指纹：排序后拼接名称/厂商/device-id。</summary>
    public static string Fingerprint(IReadOnlyList<GpuAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        return string.Join(";", adapters
            .Select(adapter => $"{adapter.Name}|{adapter.VendorId}|{adapter.DeviceId}")
            .OrderBy(entry => entry, StringComparer.Ordinal));
    }

    /// <summary>指纹缺失或与当前不一致时才重解析。</summary>
    public static bool ShouldReResolve(string? storedFingerprint, string currentFingerprint) =>
        !string.Equals(storedFingerprint ?? "", currentFingerprint ?? "", StringComparison.Ordinal);

    /// <summary>
    /// 读回持久化的代际；缺失/不可解析/不是具体代际（Unknown/NoDgpu）返回 <c>null</c>——
    /// 绝不返回"默认代际"。
    /// </summary>
    public static DgpuIdentity? Load(IDgpuGenerationStorage storage)
    {
        ArgumentNullException.ThrowIfNull(storage);

        string? raw = storage.Get(GenerationKey);
        if (!Enum.TryParse(raw, out DgpuGenerationKind generation)) return null;
        if (generation is not (DgpuGenerationKind.Gen30 or DgpuGenerationKind.Gen40 or DgpuGenerationKind.Gen50))
            return null;

        DgpuProbeSource source = Enum.TryParse(storage.Get(SourceKey), out DgpuProbeSource parsed)
            ? parsed
            : DgpuProbeSource.None;
        bool hasDgpu = storage.Get(HasDgpuKey) is "1" or "true" or "True";
        return new DgpuIdentity(generation, source, hasDgpu, null, null);
    }

    /// <summary>持久化代际与指纹。</summary>
    public static void Save(IDgpuGenerationStorage storage, DgpuIdentity identity, string fingerprint)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(identity);

        storage.Set(GenerationKey, identity.Generation.ToString());
        storage.Set(SourceKey, identity.Source.ToString());
        storage.Set(HasDgpuKey, identity.HasDgpu ? "1" : "0");
        storage.Set(FingerprintKey, fingerprint ?? "");
    }
}

/// <summary>
/// 从注册表的显示类键读显卡标识（<c>DriverDesc</c> 营销名 + <c>MatchingDeviceId</c> 的 PCI vendor/device）。
/// 枚举失败时 <paramref name="enumerationAvailable"/> 为 <c>false</c>——调用方必须据此返回
/// <see cref="DgpuIdentity.Unknown"/> 而不是"无独显"。
/// </summary>
public static class GpuAdapterReader
{
    /// <summary>显示适配器类 GUID。</summary>
    public const string DisplayClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static IReadOnlyList<GpuAdapter> ReadFromRegistry(out bool enumerationAvailable)
    {
        var adapters = new List<GpuAdapter>();
        enumerationAvailable = false;
        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(DisplayClassPath);
            if (classKey is null) return adapters;

            // 键存在即算枚举可用：即使一个显示适配器都没有，也是"确认无独显"而非"读不到"。
            enumerationAvailable = true;
            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                if (!subKeyName.All(char.IsAsciiDigit)) continue;
                using RegistryKey? adapterKey = classKey.OpenSubKey(subKeyName);
                if (adapterKey is null) continue;

                string name = adapterKey.GetValue("DriverDesc")?.ToString() ?? "";
                string? hardwareId = ReadHardwareId(adapterKey);
                TryParsePciIds(hardwareId, out string? vendorId, out string? deviceId);
                if (name.Length == 0 && hardwareId is null) continue;
                adapters.Add(new GpuAdapter(name, vendorId, deviceId));
            }
        }
        catch (Exception ex)
        {
            Logger.WriteLine("GPU adapter enumeration failed: " + ex.Message);
            enumerationAvailable = false;
            return new List<GpuAdapter>();
        }
        return adapters;
    }

    static string? ReadHardwareId(RegistryKey adapterKey)
    {
        if (adapterKey.GetValue("MatchingDeviceId")?.ToString() is { Length: > 0 } matching) return matching;
        if (adapterKey.GetValue("HardwareID") is string[] { Length: > 0 } hardwareIds) return hardwareIds[0];
        return null;
    }

    /// <summary>从 <c>PCI\VEN_10DE&amp;DEV_2882&amp;...</c> 解析 vendor/device（大写十六进制，无前缀）。</summary>
    public static bool TryParsePciIds(string? hardwareId, out string? vendorId, out string? deviceId)
    {
        vendorId = ExtractToken(hardwareId, "VEN_");
        deviceId = ExtractToken(hardwareId, "DEV_");
        return vendorId is not null && deviceId is not null;
    }

    static string? ExtractToken(string? text, string prefix)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        int at = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;

        at += prefix.Length;
        int end = at;
        while (end < text.Length && Uri.IsHexDigit(text[end])) end++;
        if (end - at < 4) return null;
        return text.Substring(at, 4).ToUpperInvariant();
    }
}

/// <summary>
/// 进程级代际提供者：探测 + 持久化 + 缓存。消费方（门控）只读 <see cref="Current"/>。
///
/// <para><see cref="Override"/> / <see cref="AdapterOverride"/> 是测试接缝，生产代码从不设置。</para>
/// </summary>
public static class GpuGenerationProvider
{
    static readonly object Sync = new();
    static DgpuIdentity? _current;

    internal static Func<DgpuIdentity>? Override { get; set; }

    internal static Func<IReadOnlyList<GpuAdapter>>? AdapterOverride { get; set; }

    internal static IDgpuGenerationStorage Storage { get; set; } = new AppConfigDgpuGenerationStorage();

    /// <summary>当前代际。硬件指纹未变时复用持久化结果。</summary>
    public static DgpuIdentity Current()
    {
        lock (Sync)
        {
            if (Override is { } factory) return factory();
            return _current ??= Resolve();
        }
    }

    public static void Invalidate()
    {
        lock (Sync) _current = null;
    }

    static DgpuIdentity Resolve()
    {
        bool enumerationAvailable;
        IReadOnlyList<GpuAdapter> adapters;
        if (AdapterOverride is { } factory)
        {
            adapters = factory();
            enumerationAvailable = true;
        }
        else
        {
            adapters = GpuAdapterReader.ReadFromRegistry(out enumerationAvailable);
        }

        string fingerprint = DgpuGenerationStore.Fingerprint(adapters);
        if (DgpuGenerationStore.Load(Storage) is { } stored &&
            !DgpuGenerationStore.ShouldReResolve(Storage.Get(DgpuGenerationStore.FingerprintKey), fingerprint))
        {
            return stored;
        }

        DgpuIdentity resolved = DgpuGenerationProbe.Resolve(adapters, enumerationAvailable);
        DgpuGenerationStore.Save(Storage, resolved, fingerprint);
        Logger.WriteLine($"dGPU generation resolved: {resolved.Generation} source={resolved.Source} hasDgpu={resolved.HasDgpu}");
        return resolved;
    }
}
