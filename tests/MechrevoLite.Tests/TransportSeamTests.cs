using MechrevoLite.Hardware;
using MQTTnet.Formatter;
using Probe;

namespace MechrevoLite.Tests;

/// <summary>
/// T31（Wave A0）happy 路径：EC 只读接缝与 MQTT 接缝都能被进程内假件驱动——
/// 不碰 <c>\\.\ACPIDriver</c>、不连 broker、不发一条网络包。
/// 失败/边界断言在同名的 <see cref="TransportSeamFailTests"/>。
/// </summary>
public class TransportSeamTests
{
    /// <summary>固定字节序列的 EC 假件：未给出的地址一律 -1（读不到）。</summary>
    sealed class FixedEc : IEcReadTransport
    {
        readonly Dictionary<int, int> _values;
        public List<int> Reads { get; } = new();
        public FixedEc(Dictionary<int, int> values) => _values = values;
        public int ReadByte(int address)
        {
            Reads.Add(address);
            return _values.TryGetValue(address, out int value) ? value : -1;
        }
    }

    [Fact]
    public void AFixedEcByteSequenceIsReadThroughTheSeam()
    {
        var ec = new FixedEc(new()
        {
            [0x740] = 0x1A,   // project byte
            [0x4AB] = 0x64,   // ecBt1RSOC
            [0x751] = 0x40,   // cTGP 编码
        });

        EcSnapshotResult result = EcSnapshot.Capture(
            ec,
            new[] { new EcRange(0x740, 0x740) },
            EcSnapshot.DocumentedBreadcrumbs.Concat(new[] { 0x4AB }).ToArray(),
            delayMs: 0);

        Assert.Equal(0x1A, result.Bytes[0x740]);
        Assert.Equal(0x64, result.Bytes[0x4AB]);
        Assert.Equal(0x40, result.Bytes[0x751]);
        Assert.Equal(3, result.Count);
        Assert.Contains(0x740, ec.Reads); // 探针确实经接缝读，而不是自己开设备
    }

    [Fact]
    public async Task AFakeMqttTransportConnectsAndSubscribesInProcess()
    {
        var transport = new FakeMqttTransport();
        var frames = new List<(string Topic, string Payload)>();
        string[] topics = { "Fan/#", "System/#" };

        bool connected = await MqttProbe.CollectFramesAsync(
            transport, new MqttCollectPlan("UWPClient_5", topics, Seconds: 0),
            (topic, payload) => frames.Add((topic, payload)));

        Assert.True(connected);
        Assert.Equal(new[] { "UWPClient_5" }, transport.ConnectCalls);
        IReadOnlyCollection<string> subscribed = Assert.Single(transport.Subscriptions);
        Assert.Equal(topics, subscribed);

        transport.Deliver("Fan/Status", """{"CpuFanRpm":2372}""");
        (string Topic, string Payload) frame = Assert.Single(frames);
        Assert.Equal("Fan/Status", frame.Topic);
        Assert.Equal("""{"CpuFanRpm":2372}""", frame.Payload);
    }

    [Fact]
    public async Task AConnectedFakeTransportSurvivesDisposalWithoutABroker()
    {
        var transport = new FakeMqttTransport();
        await using (transport)
        {
            Assert.True(await MqttProbe.CollectFramesAsync(
                transport, new MqttCollectPlan("UWPClient_5", new[] { "Fan/#" }, Seconds: 0), (_, _) => { }));
        }
        Assert.True(transport.Disposed);
    }

    [Fact]
    public void TheProbeMqttOptionsAreBuiltInOnePlace()
    {
        Assert.Equal("localhost", MqttNetTransport.Host);
        Assert.Equal(13688, MqttNetTransport.Port);

        var options = MqttNetTransport.BuildOptions("UWPClient_5");

        Assert.Equal("UWPClient_5", options.ClientId);
        Assert.Equal(MqttProtocolVersion.V311, options.ProtocolVersion);
        Assert.False(options.CleanSession);
    }

    [Fact]
    public void TheModelOverrideEnvironmentVariableInjectsAModelWithoutTouchingTheRegistry()
    {
        const string Variable = "LMECHREVO_MODEL_OVERRIDE";
        string? previous = Environment.GetEnvironmentVariable(Variable);
        try
        {
            Environment.SetEnvironmentVariable(Variable, "PH4TRX1");

            MechrevoDeviceCapabilities injected = MechrevoDeviceCapabilities.Load();

            Assert.Equal("PH4TRX1", injected.ProjectId);
            Assert.Equal("PH4TRX1", injected.Model);
        }
        finally
        {
            Environment.SetEnvironmentVariable(Variable, previous);
            MechrevoDeviceCapabilities.Invalidate();
        }
    }

    [Fact]
    public void TheAppStillStartsFromItsOwnProgramAndReferencesTheProbeSeam()
    {
        System.Reflection.Assembly app = typeof(MechrevoHw).Assembly;
        System.Reflection.Assembly probe = typeof(IEcReadTransport).Assembly;

        Assert.Equal("L-Mechrevo", app.GetName().Name);
        Assert.Equal("Probe", probe.GetName().Name);
        Assert.Equal("MechrevoLite.Program", app.EntryPoint?.DeclaringType?.FullName);
        Assert.Contains("..\\Probe\\Probe.csproj", File.ReadAllText(RepoFile("src\\MechrevoLiteWin\\MechrevoLite.csproj")));
    }

    static string RepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MechrevoLite.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, relativePath);
    }
}
