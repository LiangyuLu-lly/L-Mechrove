namespace Probe;

public static class GoldenCapture
{
    public static async Task Run(int seconds)
    {
        await using var transport = await MqttProbe.Connect("UWPClient_4");
        transport.MessageReceived += (topic, payload) =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} << {topic}: {payload}");
        transport.Disconnected += reason =>
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} DISCONNECTED: {reason}");
        var topics = new[] { "Customize/#", "System/#", "Fan/#", "Setting/#", "Keyboard/#", "HidLightbar/#", "MyRgbLightbar/#", "GPUDevice/#", "Settings/#", "Languages/#", "BatteryProtection/#", "OSD/#", "Display/#", "WhisperMode/#", "BT_LC/#", "LCHWOC/#", "GameProfile/#", "Doudou/#", "OTA/#" };
        await MqttProbe.Subscribe(transport, topics);
        Console.WriteLine($">>> 请在 {seconds}s 内手动打开原版『机械革命控制中心』并操作几个页面（首页/风扇/灯效），结束前关闭应用。");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine("=== capture end ===");
    }
}
