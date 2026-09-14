using System.Drawing;
using System.Globalization;
using System.Text;

namespace MechrevoLite.Hardware;

internal readonly record struct LightChannelSettings(
    string Effect,
    int Light,
    int Speed,
    int ColorArgb,
    bool PowerOn);

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
            }
        }
        return new LightChannelSettings(effect, light, speed, colorArgb, powerOn);
    }

    static string Serialize(LightChannelSettings settings) => string.Join('\n', new[]
    {
        "effect=" + settings.Effect,
        "light=" + settings.Light.ToString(CultureInfo.InvariantCulture),
        "speed=" + settings.Speed.ToString(CultureInfo.InvariantCulture),
        "color=" + settings.ColorArgb.ToString(CultureInfo.InvariantCulture),
        "power=" + (settings.PowerOn ? "1" : "0"),
        string.Empty,
    });

    static string DefaultEffect(string topic) => topic.Contains("Logo", StringComparison.OrdinalIgnoreCase)
        ? "Single"
        : "Single";

    /// <summary>
    /// 灯光配置目录重定向开关（环境变量），与 Logger.LogDirectoryOverrideVariable 同源。
    /// 测试宿主用假硬件驱动灯行时，Save/SavePower 必须落到临时目录，
    /// 否则会覆盖用户真实的 lightbar.cfg / logolight.cfg。
    /// </summary>
    internal const string ConfigDirectoryOverrideVariable = "LMECHREVO_LIGHT_CFG_DIR";

    static string GetPath(string topic)
    {
        string fileName = topic.Contains("Logo", StringComparison.OrdinalIgnoreCase)
            ? "logolight.cfg"
            : "lightbar.cfg";
        string? overridden = Environment.GetEnvironmentVariable(ConfigDirectoryOverrideVariable);
        string directory = string.IsNullOrWhiteSpace(overridden)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MechrevoLite")
            : overridden!;
        return Path.Combine(directory, fileName);
    }
}
