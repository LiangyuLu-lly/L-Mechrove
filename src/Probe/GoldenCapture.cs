using MQTTnet;

namespace Probe;

public static class GoldenCapture
{
    public static async Task Run(int seconds)
    {
        var factory = new MqttClientFactory();
        var c = factory.CreateMqttClient();
        c.ApplicationMessageReceivedAsync += e =>
        {
            Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} << {e.ApplicationMessage.Topic}: " +
                e.ApplicationMessage.ConvertPayloadToString());
            return Task.CompletedTask;
        };
        c.DisconnectedAsync += e => { Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} DISCONNECTED: {e.Reason}"); return Task.CompletedTask; };
        await c.ConnectAsync(MqttProbe.Opts("UWPClient_4"));
        var topics = new[] { "Customize/#", "System/#", "Fan/#", "Setting/#", "Keyboard/#", "HidLightbar/#", "MyRgbLightbar/#", "GPUDevice/#", "Settings/#", "Languages/#", "BatteryProtection/#", "OSD/#", "Display/#", "WhisperMode/#", "BT_LC/#", "LCHWOC/#", "GameProfile/#", "Doudou/#", "OTA/#" };
        foreach (var t in topics)
            await c.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).Build());
        Console.WriteLine($">>> 请在 {seconds}s 内手动打开原版『机械革命控制中心』并操作几个页面（首页/风扇/灯效），结束前关闭应用。");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Console.WriteLine("=== capture end ===");
    }
}
