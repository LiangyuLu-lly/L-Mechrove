using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace MechrevoLite.Hardware;

/// <summary>服务落盘的「上次灯效」（HIDKeyboard.SaveEffectData → SettingsStorageExtensions.SaveAsync）。</summary>
internal readonly record struct GcuLightEffectRecord(int Mode, int Effect, int Light, int Speed, int Direction);

/// <summary>
/// 官方服务在执行完一次灯效（RunEffct 写完 HID）后，把 _EffectData 以 JSON 写到
/// HKLM\SOFTWARE\OEM\GamingCenter2\RGBKeyboard\&lt;类型&gt;\&lt;ProjectID&gt;_LastEffect（REG_SZ）。
/// 电源关着时 RunEffct 提前返回、不会落盘——所以记录与下发一致 = 服务确实执行了这次灯效。
/// 只读。
/// </summary>
internal static class GcuLightingReadback
{
    const string Root = @"SOFTWARE\OEM\GamingCenter2";

    internal static bool TryParseLastEffect(string? json, out GcuLightEffectRecord record)
    {
        record = default;
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            JObject o = JObject.Parse(json);
            int Int(string key) => o.GetValue(key, StringComparison.OrdinalIgnoreCase)?.Type is JTokenType.Integer or JTokenType.Float
                ? o.GetValue(key, StringComparison.OrdinalIgnoreCase)!.Value<int>()
                : int.TryParse(o.GetValue(key, StringComparison.OrdinalIgnoreCase)?.ToString(), out int v) ? v : -1;
            record = new GcuLightEffectRecord(Int("save_mode"), Int("save_effect"), Int("save_light"),
                Int("save_speed"), Int("save_direction"));
            return record.Effect >= 0;
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return false;
        }
    }

    internal static bool TryReadLastEffect(string typeKey, out GcuLightEffectRecord record)
    {
        record = default;
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? root = baseKey.OpenSubKey(Root);
            object? projectId = root?.GetValue("ProjectID");
            if (projectId is null) return false;
            using RegistryKey? typeNode = baseKey.OpenSubKey($@"{Root}\RGBKeyboard\{typeKey}");
            string? json = typeNode?.GetValue($"{Convert.ToInt64(projectId, System.Globalization.CultureInfo.InvariantCulture)}_LastEffect") as string;
            return TryParseLastEffect(json, out record);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"GCU lighting record read failed ({typeKey}): {ex.Message}");
            return false;
        }
    }
}
