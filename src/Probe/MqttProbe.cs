using MQTTnet;

namespace Probe;

public static class MqttProbe
{
    const string Host = "localhost";
    const int Port = 13688;
    const string User = "UWPClient_User_5";
    const string Pwd = "UWPClient_Pwd888881772688_5";

    static readonly MqttClientFactory Factory = new();

    public static MqttClientOptions Opts(string clientId) => new MqttClientOptionsBuilder()
        .WithTcpServer(Host, Port).WithCredentials(User, Pwd).WithClientId(clientId)
        .WithProtocolVersion(MQTTnet.Formatter.MqttProtocolVersion.V311)
        .WithCleanSession(false).Build();

    public static async Task<IMqttClient> Connect(string clientId)
    {
        var c = Factory.CreateMqttClient();
        var res = await c.ConnectAsync(Opts(clientId));
        Console.WriteLine($"[{clientId}] Connect: {res.ResultCode} Reason={res.ReasonString}");
        return c;
    }

    public static async Task Subscribe(IMqttClient c, IEnumerable<string> topics)
    {
        foreach (var t in topics)
        {
            await c.SubscribeAsync(new MqttTopicFilterBuilder().WithTopic(t).WithQualityOfServiceLevel(
                MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce).Build());
        }
        Console.WriteLine($"[{c.Options.ClientId}] subscribed {string.Join(",", topics)}");
    }

    public static async Task Publish(IMqttClient c, string topic, object payload, bool retain = false)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
        Console.WriteLine($"[{c.Options.ClientId}] PUB {topic} <- {json}");
        await c.PublishStringAsync(topic, json, MQTTnet.Protocol.MqttQualityOfServiceLevel.ExactlyOnce, retain);
    }

    public static void WirePrint(IMqttClient c, string tag)
    {
        c.ApplicationMessageReceivedAsync += e =>
        {
            Console.WriteLine($"[{tag}] << {e.ApplicationMessage.Topic}: {e.ApplicationMessage.ConvertPayloadToString()}");
            return Task.CompletedTask;
        };
        c.DisconnectedAsync += e =>
        {
            Console.WriteLine($"[{tag}] DISCONNECTED: {e.Reason}");
            return Task.CompletedTask;
        };
    }

    public static async Task Handshake()
    {
        var topics = new[] { "Customize/#", "System/#", "Fan/#", "Setting/#", "Keyboard/#", "GPUDevice/#", "BatteryProtection/#", "HidLightbar/#", "MyRgbLightbar/#", "Languages/#", "OSD/#", "Display/#", "WhisperMode/#", "BT_LC/#", "LCHWOC/#", "GameProfile/#", "GamingMonitor/#", "Doudou/#", "OTA/#", "Settings/#" };

        // 绗?1 姝ワ細浠呰繛鎺?+ 璁㈤槄锛屼笉鍙戝竷
        var c1 = await Connect("UWPClient_5");
        WirePrint(c1, "step1");
        await Subscribe(c1, topics);
        Console.WriteLine(">>> [step1] connect+subscribe only, wait 15s");
        await Task.Delay(15000);

        // 绗?2 姝ワ細Keyboard/Ctrl Init锛圲I 鍚姩鍒濆鍖栵級
        await Publish(c1, "Keyboard/Ctrl", new { function = "Init" });
        Console.WriteLine(">>> [step2] Keyboard Init, wait 15s");
        await Task.Delay(15000);

        // 绗?3 姝ワ細Customize/Control GETSUPPORT
        await Publish(c1, "Customize/Control", new { Action = "GETSUPPORT" });
        Console.WriteLine(">>> [step3] Customize GETSUPPORT, wait 15s");
        await Task.Delay(15000);

        // 绗?4 姝ワ細鍚?GETSTATUS
        await Publish(c1, "Fan/Control", new { Action = "GETSTATUS" });
        await Publish(c1, "Setting/Control", new { Action = "GETSTATUS" });
        await Publish(c1, "Keyboard/Ctrl", new { Action = "GETSTATUS" });
        Console.WriteLine(">>> [step4] GETSTATUS batch, wait 15s");
        await Task.Delay(15000);

        // 绗?5 姝ワ細System_ON 瑙﹀彂閬ユ祴
        await Publish(c1, "System/Control", new { Action = "System_ON" });
        Console.WriteLine(">>> [step5] System_ON, wait 30s");
        await Task.Delay(30000);

        Console.WriteLine("=== handshake done: record findings to docs/superpowers/plans/m1-findings.md ===");
    }

    public static async Task Sniff(int seconds)
    {
        var c = await Connect("UWPClient_5");
        WirePrint(c, "mqtt");
        await Subscribe(c, new[] { "System/#", "Fan/#", "Setting/#", "EC/#", "Keyboard/#", "MyRgbLightbar/#", "HidLightbar/#", "GPUDevice/#" });
        await Publish(c, "System/Control", new { Action = "System_ON" });

        var snapDir = Path.Combine(AppContext.BaseDirectory, "snaps");
        Directory.CreateDirectory(snapDir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var snap = new List<string>();
            IntPtr h = EcProbe.OpenDriver();
            if (h != new IntPtr(-1))
            {
                for (int a = 0; a < 0x80; a++) snap.Add(EcProbe.ReadReg(h, a).ToString("X2"));
                EcProbe.CloseDriver(h);
            }
            File.WriteAllText(Path.Combine(snapDir, $"{DateTime.Now:HHmmss}.txt"), string.Join(" ", snap));
            await Task.Delay(1000);
        }
        Console.WriteLine(">>> 蹇収鐩綍: " + snapDir);
    }

    /// <summary>
    /// 瑁稿彂閫侊細probe send &lt;topic&gt; &lt;json&gt; [waitSeconds]銆傝闃?Setting/# 涓?Fan/#
    /// 鍚庡彂甯冪粰瀹氳浇鑽凤紝鎶婄瓑寰呮湡闂存敹鍒扮殑鍥炴樉鍘熸牱鎵撳嵃鈥斺€旂敤浜庡瘎瀛樺櫒绾цˉ鏁戝拰鍗忚褰㈢姸楠岃瘉銆?    /// </summary>
    public static async Task Send(string topic, string json, int waitSeconds)
    {
        // PowerShell 5.1 向原生程序传参会剥内嵌双引号，JSON 走文件最稳。
        if (File.Exists(json)) json = File.ReadAllText(json);
        var c = await Connect("UWPClient_5");
        WirePrint(c, "send");
        await Subscribe(c, new[] { "Setting/#", "Fan/#" });
        await Publish(c, topic, Newtonsoft.Json.Linq.JToken.Parse(json));
        await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
        Console.WriteLine("send done");
    }

    public static async Task ModeTest()
    {
        var c = await Connect("UWPClient_5");
        WirePrint(c, "mode");
        await Subscribe(c, new[] { "Fan/Status" });
        var modes = new (string Name, string Action)[]
        {
            ("鍔炲叕 Office", "OPERATING_OFFICE_MODE"),
            ("娓告垙 Gaming", "OPERATING_GAMING_MODE"),
            ("鏋侀€?Turbo", "OPERATING_TURBO_MODE"),
            ("鑷畾涔?Custom", "OPERATING_CUSTOM_MODE"),
        };
        foreach (var (name, action) in modes)
        {
            Console.WriteLine($"=== 鍒囨崲鍒?{name} ===");
            await Publish(c, "Fan/Control", new { Action = action });
            await Task.Delay(4000);
            await Publish(c, "Fan/Control", new { Action = "GETSTATUS" });
            await Task.Delay(3000);
        }
        Console.WriteLine("mode test done");
    }

    public static async Task SelfTest(int rounds = 3)
    {
        var c = await Connect("UWPClient_5");
        WirePrint(c, "st");
        await Subscribe(c, new[] { "Fan/Status", "Fan/Table", "System/FanInfo" });
        var modes = new (int G, string Name, string Action)[]
        {
            (2, "鍔炲叕Office", "OPERATING_OFFICE_MODE"),
            (0, "娓告垙Gaming", "OPERATING_GAMING_MODE"),
            (1, "澧炲己Turbo", "OPERATING_TURBO_MODE"),
        };
        for (int r = 0; r < rounds; r++)
        {
            Console.WriteLine($"========== ROUND {r + 1} ==========");
            foreach (var (g, name, action) in modes)
            {
                Console.WriteLine($"--- [{name}] expectG={g} ---");
                await Publish(c, "Fan/Control", new { Action = action, ProfileIndex = "0" });
                await Task.Delay(3000);
                await Publish(c, "Fan/Control", new { Action = "GETSTATUS" });
                await Task.Delay(1000);
                await Publish(c, "Fan/Control", new { Action = "GET_FAN_SPEED_CURVE_SETTING" });
                await Task.Delay(1500);
            }
        }
        Console.WriteLine("selftest done");
    }
}
