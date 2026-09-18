using MQTTnet;
using MQTTnet.Protocol;

namespace Probe;

public static class MqttProbe
{
    /// <summary>
    /// 连接（唯一的 options/client 构造点已移入 <see cref="MqttNetTransport"/>）；
    /// 返回的实例同时带诊断发布所需的客户端句柄。
    /// </summary>
    public static async Task<MqttNetTransport> Connect(string clientId)
    {
        var transport = new MqttNetTransport();
        await transport.ConnectAsync(clientId);
        return transport;
    }

    /// <summary>
    /// 连接 → 订阅 → 把收到的帧交给 <paramref name="onFrame"/>，持续 <c>plan.Seconds</c> 秒。
    /// 连不上直接返回 false 且**不订阅**（fail-closed）。这是探针 CLI（GoldenCapture / EcSnapshotReport）
    /// 的公共上层——单元 TDD 用进程内假传输驱动，不碰 broker。
    /// </summary>
    public static async Task<bool> CollectFramesAsync(IMqttTransport transport, MqttCollectPlan plan, Action<string, string> onFrame)
    {
        if (!await transport.ConnectAsync(plan.ClientId)) return false;
        transport.MessageReceived += onFrame;   // 先挂收帧再订阅：SUBACK 与首帧之间没有空窗
        await transport.SubscribeAsync(plan.Topics);
        if (plan.Seconds > 0) await Task.Delay(TimeSpan.FromSeconds(plan.Seconds));
        return true;
    }

    /// <summary>订阅走 <see cref="IMqttTransport"/>；发布不在此列（见 <see cref="MqttNetTransport"/>）。</summary>
    public static Task Subscribe(IMqttTransport transport, IEnumerable<string> topics) =>
        transport.SubscribeAsync(topics.ToArray());

    /// <summary>诊断发布：唯一实现，句柄来自传输自身的连接。</summary>
    public static async Task Publish(IMqttClient client, string topic, object payload, bool retain = false)
    {
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(payload);
        Console.WriteLine($"[{client.Options.ClientId}] PUB {topic} <- {json}");
        await client.PublishStringAsync(topic, json, MqttQualityOfServiceLevel.ExactlyOnce, retain);
    }

    /// <summary>把接缝的收帧/断线通知打到控制台。</summary>
    public static void WirePrint(IMqttTransport transport, string tag)
    {
        transport.MessageReceived += (topic, payload) => Console.WriteLine($"[{tag}] << {topic}: {payload}");
        transport.Disconnected += reason => Console.WriteLine($"[{tag}] DISCONNECTED: {reason}");
    }

    static IMqttClient ClientOf(MqttNetTransport transport) =>
        transport.Client ?? throw new InvalidOperationException("MQTT 未连接");

    public static async Task Handshake()
    {
        var topics = new[] { "Customize/#", "System/#", "Fan/#", "Setting/#", "Keyboard/#", "GPUDevice/#", "BatteryProtection/#", "HidLightbar/#", "MyRgbLightbar/#", "Languages/#", "OSD/#", "Display/#", "WhisperMode/#", "BT_LC/#", "LCHWOC/#", "GameProfile/#", "GamingMonitor/#", "Doudou/#", "OTA/#", "Settings/#" };

        // 第 1 步：只连接 + 订阅，不发布
        await using var transport = await Connect("UWPClient_5");
        IMqttClient client = ClientOf(transport);
        WirePrint(transport, "step1");
        await Subscribe(transport, topics);
        Console.WriteLine(">>> [step1] connect+subscribe only, wait 15s");
        await Task.Delay(15000);

        // 第 2 步：Keyboard/Ctrl Init（UI 启动初始化）
        await Publish(client, "Keyboard/Ctrl", new { function = "Init" });
        Console.WriteLine(">>> [step2] Keyboard Init, wait 15s");
        await Task.Delay(15000);

        // 第 3 步：Customize/Control GETSUPPORT
        await Publish(client, "Customize/Control", new { Action = "GETSUPPORT" });
        Console.WriteLine(">>> [step3] Customize GETSUPPORT, wait 15s");
        await Task.Delay(15000);

        // 第 4 步：批量 GETSTATUS
        await Publish(client, "Fan/Control", new { Action = "GETSTATUS" });
        await Publish(client, "Setting/Control", new { Action = "GETSTATUS" });
        await Publish(client, "Keyboard/Ctrl", new { Action = "GETSTATUS" });
        Console.WriteLine(">>> [step4] GETSTATUS batch, wait 15s");
        await Task.Delay(15000);

        // 第 5 步：System_ON 触发遥测
        await Publish(client, "System/Control", new { Action = "System_ON" });
        Console.WriteLine(">>> [step5] System_ON, wait 30s");
        await Task.Delay(30000);

        Console.WriteLine("=== handshake done: record findings to docs/superpowers/plans/m1-findings.md ===");
    }

    public static async Task Sniff(int seconds)
    {
        await using var transport = await Connect("UWPClient_5");
        IMqttClient client = ClientOf(transport);
        WirePrint(transport, "mqtt");
        await Subscribe(transport, new[] { "System/#", "Fan/#", "Setting/#", "EC/#", "Keyboard/#", "MyRgbLightbar/#", "HidLightbar/#", "GPUDevice/#" });
        await Publish(client, "System/Control", new { Action = "System_ON" });

        var snapDir = Path.Combine(AppContext.BaseDirectory, "snaps");
        Directory.CreateDirectory(snapDir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var snap = new List<string>();
            IntPtr h = EcProbe.OpenDriver();
            if (h != new IntPtr(-1))
            {
                // 读不到记 ??，不再把 -1 记成 0xFF（0xFF 是合法读值）。
                for (int a = 0; a < 0x80; a++)
                {
                    int value = EcProbe.ReadReg(h, a);
                    snap.Add(value < 0 ? "??" : value.ToString("X2"));
                }
                EcProbe.CloseDriver(h);
            }
            File.WriteAllText(Path.Combine(snapDir, $"{DateTime.Now:HHmmss}.txt"), string.Join(" ", snap));
            await Task.Delay(1000);
        }
        Console.WriteLine(">>> 快照目录: " + snapDir);
    }

    /// <summary>
    /// 裸发送：probe send &lt;topic&gt; &lt;json&gt; [waitSeconds]。订阅 Setting/# 与 Fan/#
    /// 后发布给定载荷，把等待期间收到的回显原样打印——用于寄存器级补救和协议形状验证。
    /// </summary>
    public static async Task Send(string topic, string json, int waitSeconds)
    {
        // PowerShell 5.1 向原生程序传参会剥内嵌双引号，JSON 走文件最稳。
        if (File.Exists(json)) json = File.ReadAllText(json);
        await using var transport = await Connect("UWPClient_5");
        IMqttClient client = ClientOf(transport);
        WirePrint(transport, "send");
        await Subscribe(transport, new[] { "Setting/#", "Fan/#" });
        await Publish(client, topic, Newtonsoft.Json.Linq.JToken.Parse(json));
        await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
        Console.WriteLine("send done");
    }

    public static async Task ModeTest()
    {
        await using var transport = await Connect("UWPClient_5");
        IMqttClient client = ClientOf(transport);
        WirePrint(transport, "mode");
        await Subscribe(transport, new[] { "Fan/Status" });
        var modes = new (string Name, string Action)[]
        {
            ("办公 Office", "OPERATING_OFFICE_MODE"),
            ("游戏 Gaming", "OPERATING_GAMING_MODE"),
            ("极致 Turbo", "OPERATING_TURBO_MODE"),
            ("自定义 Custom", "OPERATING_CUSTOM_MODE"),
        };
        foreach (var (name, action) in modes)
        {
            Console.WriteLine($"=== 切换到 {name} ===");
            await Publish(client, "Fan/Control", new { Action = action });
            await Task.Delay(4000);
            await Publish(client, "Fan/Control", new { Action = "GETSTATUS" });
            await Task.Delay(3000);
        }
        Console.WriteLine("mode test done");
    }

    public static async Task SelfTest(int rounds = 3)
    {
        await using var transport = await Connect("UWPClient_5");
        IMqttClient client = ClientOf(transport);
        WirePrint(transport, "st");
        await Subscribe(transport, new[] { "Fan/Status", "Fan/Table", "System/FanInfo" });
        var modes = new (int G, string Name, string Action)[]
        {
            (2, "办公Office", "OPERATING_OFFICE_MODE"),
            (0, "游戏Gaming", "OPERATING_GAMING_MODE"),
            (1, "增强Turbo", "OPERATING_TURBO_MODE"),
        };
        for (int r = 0; r < rounds; r++)
        {
            Console.WriteLine($"========== ROUND {r + 1} ==========");
            foreach (var (g, name, action) in modes)
            {
                Console.WriteLine($"--- [{name}] expectG={g} ---");
                await Publish(client, "Fan/Control", new { Action = action, ProfileIndex = "0" });
                await Task.Delay(3000);
                await Publish(client, "Fan/Control", new { Action = "GETSTATUS" });
                await Task.Delay(1000);
                await Publish(client, "Fan/Control", new { Action = "GET_FAN_SPEED_CURVE_SETTING" });
                await Task.Delay(1500);
            }
        }
        Console.WriteLine("selftest done");
    }
}
