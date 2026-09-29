using System.Drawing;
using System.Globalization;
using System.Text;

namespace MechrevoLite.Hardware;

/// <param name="Direction">官方方向名（RGBKB_Direction）；只有效果规格列出多个方向时才会被下发为非 None。</param>
/// <param name="UsePalette">多色效果用七彩调色板（true）还是把用户颜色铺满全部色块（false）。</param>
internal readonly record struct LightChannelSettings(
    string Effect,
    int Light,
    int Speed,
    int ColorArgb,
    bool PowerOn,
    string Direction = LightingEffectCatalog.DirNone,
    bool UsePalette = false);

internal static class LightingSettingsStore
{
    internal static bool TryLoad(string topic, out LightChannelSettings settings)
    {
        settings = default;
        string path = GetPath(topic);
        if (!File.Exists(path)) return false;
        try
        {
            settings = ParseLines(File.ReadLines(path), DefaultEffect(topic));
            return true;
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Lighting settings read failed ({topic}): {ex.Message}");
            return false;
        }
    }

    internal static LightChannelSettings Load(string topic, string defaultEffect)
    {
        return TryLoad(topic, out LightChannelSettings settings)
            ? settings
            : new LightChannelSettings(defaultEffect, 4, 1, Color.White.ToArgb(), true);
    }

    internal static void Save(string topic, LightChannelSettings settings)
    {
        string path = GetPath(topic);
        string temporaryPath = path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporaryPath, Serialize(settings), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            Logger.WriteLine($"Lighting settings write failed ({topic}): {ex.Message}");
        }
    }

    internal static void SavePower(string topic, bool powerOn)
    {
        LightChannelSettings settings = Load(topic, DefaultEffect(topic));
        Save(topic, settings with { PowerOn = powerOn });
    }

    internal static LightChannelSettings ParseLines(IEnumerable<string> lines, string defaultEffect)
    {
        string effect = defaultEffect;
        int light = 4;
        int speed = 1;
        int colorArgb = Color.White.ToArgb();
        bool powerOn = true;
        string direction = LightingEffectCatalog.DirNone;
        bool usePalette = false;
        foreach (string line in lines)
        {
            int separator = line.IndexOf('=');
            if (separator <= 0) continue;
            string key = line[..separator];
            string value = line[(separator + 1)..];
            switch (key)
            {
                case "effect" when !string.IsNullOrWhiteSpace(value): effect = value; break;
                case "light" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLight):
                    light = Math.Clamp(parsedLight, 0, 4);
                    break;
                case "speed" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedSpeed):
                    speed = Math.Clamp(parsedSpeed, 1, 3);
                    break;
                case "color" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedColor):
                    colorArgb = parsedColor;
                    break;
                case "power" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPower):
                    powerOn = parsedPower != 0;
                    break;
                case "direction" when !string.IsNullOrWhiteSpace(value): direction = value.Trim(); break;
                case "palette" when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedPalette):
                    usePalette = parsedPalette != 0;
                    break;
            }
        }
        return new LightChannelSettings(effect, light, speed, colorArgb, powerOn, direction, usePalette);
    }

    static string Serialize(LightChannelSettings settings)
    {
        var lines = new List<string>
        {
            "effect=" + settings.Effect,
            "light=" + settings.Light.ToString(CultureInfo.InvariantCulture),
            "speed=" + settings.Speed.ToString(CultureInfo.InvariantCulture),
            "color=" + settings.ColorArgb.ToString(CultureInfo.InvariantCulture),
            "power=" + (settings.PowerOn ? "1" : "0"),
        };
        // 新字段只在非缺省时落盘：旧版本读到未知键会忽略，缺省配置的文件内容保持不变。
        if (!string.IsNullOrEmpty(settings.Direction) && settings.Direction != LightingEffectCatalog.DirNone)
            lines.Add("direction=" + settings.Direction);
        if (settings.UsePalette) lines.Add("palette=1");
        lines.Add(string.Empty);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// 按效果规格给出要下发的颜色：null = 七彩调色板（固件自带配色的效果、或用户选了七彩）；
    /// 否则是用户颜色（A 面 Logo 呼吸吸附到最近的预设色）。规格未知时退回旧的按名字判断。
    /// </summary>
    internal static Color? ColorForSpec(LightEffectSpec? spec, LightChannelSettings settings)
    {
        if (spec is null) return ColorForEffect(settings.Effect, settings.ColorArgb);
        Color user = Color.FromArgb(settings.ColorArgb);
        if (spec.PresetColors is { Length: > 0 } presets) return LightingEffectCatalog.NearestPreset(user, presets);
        if (spec.ColorSlots == 0) return null;
        if (spec.MultiColor && settings.UsePalette) return null;
        return user;
    }

    /// <summary>方向只在规格允许的集合里取值；不在集合里就用集合首项（没有方向的效果恒为 None）。</summary>
    internal static string DirectionForSpec(LightEffectSpec? spec, LightChannelSettings settings)
    {
        if (spec?.Directions is not { Length: > 0 } directions) return LightingEffectCatalog.DirNone;
        return Array.Exists(directions, d => d == settings.Direction) ? settings.Direction : directions[0];
    }

    static string DefaultEffect(string topic) => topic.Contains("Logo", StringComparison.OrdinalIgnoreCase)
        ? "Single"
        : "Single";

    /// <summary>
    /// 固件用单色 ColorBuffer（ColorBlocks=1）的效果（含 Mix）。Wave/Raindrop 走 7 色默认盘。
    /// 冲击（Impact）也吃这一色；只给 Single 传颜色时冲击会停在默认红。
    /// </summary>
    internal static bool EffectUsesSingleColor(string effect) =>
        effect is "Single" or "Breathing" or "Impact" or "Mix";

    internal static Color? ColorForEffect(string effect, Color color) =>
        EffectUsesSingleColor(effect) ? color : null;

    internal static Color? ColorForEffect(string effect, int colorArgb) =>
        ColorForEffect(effect, Color.FromArgb(colorArgb));

    /// <summary>
    /// 灯光配置目录重定向开关（环境变量），与 Logger.LogDirectoryOverrideVariable 同源。
    /// 测试宿主用假硬件驱动灯行时，Save/SavePower 必须落到临时目录，
    /// 否则会覆盖用户真实的 lightbar.cfg / logolight.cfg / keyboard.cfg。
    /// </summary>
    internal const string ConfigDirectoryOverrideVariable = "LMECHREVO_LIGHT_CFG_DIR";

    static string GetPath(string topic)
    {
        // 每条通道一个文件：铰链/同步/EC 灯带过去会全部落到 lightbar.cfg，互相覆盖。
        string fileName = topic.Contains("Logo", StringComparison.OrdinalIgnoreCase) ? "logolight.cfg"
            : topic.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) ? "keyboard.cfg"
            : topic.Contains("Hinge", StringComparison.OrdinalIgnoreCase) ? "hingelight.cfg"
            : topic.Contains("_Sync", StringComparison.OrdinalIgnoreCase) ? "synclight.cfg"
            : topic.StartsWith("MyRgbLightbar/", StringComparison.OrdinalIgnoreCase) ? "eclightbar.cfg"
            : "lightbar.cfg";
        string? overridden = Environment.GetEnvironmentVariable(ConfigDirectoryOverrideVariable);
        string directory = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite")
            : overridden!;
        return Path.Combine(directory, fileName);
    }
}
