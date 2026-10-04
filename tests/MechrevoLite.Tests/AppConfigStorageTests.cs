using MechrevoLite;

namespace MechrevoLite.Tests;

/// <summary>
/// AppConfig 是全局状态存储，此前 698 行零测试覆盖。
/// 这里覆盖它最容易静默失效的三条路径：配置位置优先级、加载降级链（含损坏抢救）、
/// 原子写与 .bak 兜底。这些路径失效的表现都是「设置改了但重启后丢」，
/// 不会抛异常、不会有报错，只能靠测试锁住。
/// </summary>
public class AppConfigStorageTests
{
    // ---------- 配置位置优先级 ----------

    [Fact]
    public void ResolveConfigPath_PrefersPortableConfigNextToExecutable()
    {
        string path = AppConfig.ResolveConfigPath(
            @"D:\portable\config.json",
            @"C:\ProgramData\MechrevoLite\config.json",
            @"C:\Users\me\AppData\Roaming\MechrevoLite\config.json",
            runningAsSystem: false,
            fileExists: p => p == @"D:\portable\config.json");

        Assert.Equal(@"D:\portable\config.json", path);
    }

    [Fact]
    public void ResolveConfigPath_PortableWinsEvenWhenRunningAsSystem()
    {
        // 便携部署是本项目的主要分发形态：程序目录旁的配置必须始终优先，
        // 否则开机任务（SYSTEM 身份）会读到另一份配置，形成两套互不可见的设置。
        string path = AppConfig.ResolveConfigPath(
            @"D:\portable\config.json",
            @"C:\ProgramData\MechrevoLite\config.json",
            @"C:\Users\me\AppData\Roaming\MechrevoLite\config.json",
            runningAsSystem: true,
            fileExists: _ => true);

        Assert.Equal(@"D:\portable\config.json", path);
    }

    [Fact]
    public void ResolveConfigPath_UsesProgramDataOnlyForSystemAccount()
    {
        const string fallback = @"C:\ProgramData\MechrevoLite\config.json";
        const string appData = @"C:\Users\me\AppData\Roaming\MechrevoLite\config.json";

        // SYSTEM 读不到用户 AppData，必须走 ProgramData 副本。
        Assert.Equal(fallback, AppConfig.ResolveConfigPath(
            @"D:\portable\config.json", fallback, appData,
            runningAsSystem: true, fileExists: p => p == fallback));

        // 普通用户身份即使 ProgramData 副本存在也不用它，避免读到旧的开机任务快照。
        Assert.Equal(appData, AppConfig.ResolveConfigPath(
            @"D:\portable\config.json", fallback, appData,
            runningAsSystem: false, fileExists: p => p == fallback));
    }

    [Fact]
    public void ResolveConfigPath_FallsBackToAppDataWhenNothingExists()
    {
        const string appData = @"C:\Users\me\AppData\Roaming\MechrevoLite\config.json";
        Assert.Equal(appData, AppConfig.ResolveConfigPath(
            @"D:\portable\config.json", @"C:\ProgramData\MechrevoLite\config.json", appData,
            runningAsSystem: true, fileExists: _ => false));
    }

    // ---------- 加载：不可用内容必须被识别为不可用 ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    [InlineData("{ broken")]
    [InlineData("[1,2,3]")]
    public void DeserializeConfig_ReturnsNullForUnusableContent(string? json)
    {
        // 字面量 null 是关键用例：它能合法反序列化成 null 引用，
        // 旧代码会把 null 赋给 config 字段，之后每次读取都抛 NullReferenceException。
        Assert.Null(AppConfig.DeserializeConfig(json));
    }

    [Fact]
    public void DeserializeConfig_AcceptsTrailingCommasAndComments()
    {
        var loaded = AppConfig.DeserializeConfig("""
            {
              // 手工编辑过的配置也要能读
              "performance_mode": 2,
              "aspm": true,
            }
            """);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.Count);
        Assert.True(loaded.ContainsKey("performance_mode"));
    }

    // ---------- 暴力对抗样本（ProtocolFuzzTests 的配置面补充）----------

    [Fact]
    public void DeserializeConfig_DeeplyNestedValueIsRejectedNotFatal()
    {
        // System.Text.Json 的默认 MaxDepth 是 64：嵌套炸弹必须落到「内容不可用」，
        // 而不是抛出未捕获异常（静态构造函数里抛出会让整个类型永久不可用）。
        string bomb = string.Concat(Enumerable.Repeat("\"a\":{", 5000)) + "1" + string.Concat(Enumerable.Repeat("}", 5000));

        Assert.Null(AppConfig.DeserializeConfig("{" + bomb + "}"));
    }

    [Fact]
    public void DeserializeConfig_HugeScalarValuesDoNotBlowUp()
    {
        // 手改配置写了个 1 MB 的字符串值：要么读进来要么整体判废，不允许挂死或抛出。
        string huge = "{\"theme\":\"" + new string('x', 1_048_576) + "\"}";

        var loaded = AppConfig.DeserializeConfig(huge);

        if (loaded is not null) Assert.True(loaded.ContainsKey("theme"));
    }

    [Fact]
    public void DeserializeConfig_DuplicateKeysLastValueWins()
    {
        var loaded = AppConfig.DeserializeConfig("""{"performance_mode":1,"performance_mode":2}""");

        Assert.NotNull(loaded);
        Assert.Equal("2", loaded!["performance_mode"]?.ToString());
    }

    [Theory]
    [InlineData("{\"theme\":\"dark\"} {{{ 尾部垃圾")]
    [InlineData("{\"theme\":\"dark\"},\"brightness\":80")]
    [InlineData("{\"a\":\"未闭合的字符串]}")]
    public void RecoverConfig_ToleratesAdversarialTrashAfterValidPairs(string json)
    {
        // 抢救路径本身面对任意垃圾不能抛异常——它在启动阶段运行，抛了就是起不来。
        var recovered = AppConfig.RecoverConfig(json);

        if (recovered is not null) Assert.NotEmpty(recovered);
    }

    // ---------- 损坏抢救 ----------

    [Fact]
    public void RecoverConfig_SalvagesLeadingKeysFromTruncatedFile()
    {
        // 断电或写入中断的典型形态：前半段完好，尾部被截断。
        string truncated = """
            {
              "performance_mode": 2,
              "brightness": 80,
              "theme": "flat",
              "aspm": tr
            """;

        var recovered = AppConfig.RecoverConfig(truncated);

        Assert.NotNull(recovered);
        Assert.Equal(3, recovered!.Count);
        Assert.True(recovered.ContainsKey("performance_mode"));
        Assert.True(recovered.ContainsKey("brightness"));
        Assert.True(recovered.ContainsKey("theme"));
        Assert.False(recovered.ContainsKey("aspm"));   // 截断处的键不应被瞎猜
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("完全不是 JSON 的内容")]
    [InlineData("{}")]
    public void RecoverConfig_ReturnsNullWhenNothingSalvageable(string? json)
    {
        Assert.Null(AppConfig.RecoverConfig(json));
    }

    [Fact]
    public void RecoverKeyValuePairs_KeepsLastValueForDuplicateKeys()
    {
        // 与 JSON 的「后者覆盖前者」一致：追加写坏的文件里同一键可能出现两次。
        var pairs = AppConfig.RecoverKeyValuePairs("""{"mode":1,"mode":3}""");

        Assert.Single(pairs);
        Assert.Equal("3", pairs["\"mode\""]);
    }

    [Fact]
    public void RecoverKeyValuePairs_HandlesAllScalarKindsAndEscapes()
    {
        var pairs = AppConfig.RecoverKeyValuePairs(
            """{"i":-12,"f":1.5,"t":true,"f2":false,"n":null,"s":"a\"b"}""");

        Assert.Equal(6, pairs.Count);
        Assert.Equal("-12", pairs["\"i\""]);
        Assert.Equal("1.5", pairs["\"f\""]);
        Assert.Equal("true", pairs["\"t\""]);
        Assert.Equal("false", pairs["\"f2\""]);
        Assert.Equal("null", pairs["\"n\""]);
        Assert.Equal("\"a\\\"b\"", pairs["\"s\""]);
    }

    [Fact]
    public void RecoverConfig_OutputIsValidJsonThatRoundTrips()
    {
        var recovered = AppConfig.RecoverConfig("""{"a":1,"b":"x"} 尾部垃圾 {{{""");

        Assert.NotNull(recovered);
        // 抢救结果必须能再被正常路径读一遍，否则下一次启动仍然走抢救分支。
        string rebuilt = AppConfig.RebuildJson(AppConfig.RecoverKeyValuePairs("""{"a":1,"b":"x"}"""));
        Assert.NotNull(AppConfig.DeserializeConfig(rebuilt));
    }

    // ---------- 原子写与 .bak ----------

    [Fact]
    public void AWriterWaitingForTheDiskLockSerializesTheLatestConfiguration()
    {
        ConfigScopeTests.WithConfigSnapshot(() =>
        {
            string key = "write-order-" + Guid.NewGuid().ToString("N");
            object gate = typeof(AppConfig).GetField("writeLock",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            using var started = new ManualResetEventSlim();
            Exception? error = null;
            AppConfig.Set(key, 1);
            var writer = new Thread(() =>
            {
                started.Set();
                try { AppConfig.Flush(); }
                catch (Exception ex) { error = ex; }
            }) { IsBackground = true };
            Monitor.Enter(gate);
            try
            {
                writer.Start();
                Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
                Assert.True(SpinWait.SpinUntil(() => (writer.ThreadState & ThreadState.WaitSleepJoin) != 0, 2000));
                AppConfig.Set(key, 2);
            }
            finally { Monitor.Exit(gate); }
            Assert.True(writer.Join(2000));
            Assert.Null(error);
            using var saved = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
                Environment.GetEnvironmentVariable("LMECHREVO_CONFIG_FILE")!));
            Assert.Equal(2, saved.RootElement.GetProperty(key).GetInt32());
        });
    }

    [Fact]
    public void ARealDiskWriteFailureIsReportedInsteadOfReturningSuccess()
    {
        var field = typeof(AppConfig).GetField("configFile",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        string original = (string)field.GetValue(null)!;
        using var dir = new TempDirectory();
        string blocker = Path.Combine(dir.Path, "file-as-directory");
        File.WriteAllText(blocker, "occupied");
        try
        {
            field.SetValue(null, Path.Combine(blocker, "config.json"));
            Assert.False(AppConfig.TryFlush());
        }
        finally
        {
            field.SetValue(null, original);
            Assert.True(AppConfig.TryFlush());
        }
    }

    [Fact]
    public void WriteAtomic_CreatesFileAndLeavesNoTempBehind()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "config.json");

        AppConfig.WriteAtomic(path, """{"a":1}""");

        Assert.Equal("""{"a":1}""", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"), ".tmp 必须被换入或移走，不能残留");
        Assert.False(File.Exists(path + ".bak"), "首次写入没有旧内容，不应产生 .bak");
    }

    [Fact]
    public void WriteAtomic_MovesPreviousContentToBak()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "config.json");

        AppConfig.WriteAtomic(path, """{"generation":1}""");
        AppConfig.WriteAtomic(path, """{"generation":2}""");

        Assert.Equal("""{"generation":2}""", File.ReadAllText(path));
        Assert.Equal("""{"generation":1}""", File.ReadAllText(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void WriteAtomic_BakAlwaysHoldsTheImmediatelyPreviousGeneration()
    {
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "config.json");

        for (int generation = 1; generation <= 4; generation++)
            AppConfig.WriteAtomic(path, $"{{\"generation\":{generation}}}");

        Assert.Equal("""{"generation":4}""", File.ReadAllText(path));
        Assert.Equal("""{"generation":3}""", File.ReadAllText(path + ".bak"));
    }

    [Fact]
    public void WriteAtomic_ThenCorruptingTargetStillLeavesRecoverableBak()
    {
        // 完整降级链演练：目标文件损坏 -> 正常加载失败 -> 抢救失败 -> .bak 可用。
        using var dir = new TempDirectory();
        string path = Path.Combine(dir.Path, "config.json");

        AppConfig.WriteAtomic(path, """{"performance_mode":2}""");
        AppConfig.WriteAtomic(path, """{"performance_mode":1}""");
        File.WriteAllText(path, "\0\0\0");   // 模拟写坏

        Assert.Null(AppConfig.DeserializeConfig(File.ReadAllText(path)));
        Assert.Null(AppConfig.RecoverConfig(File.ReadAllText(path)));

        var fromBak = AppConfig.DeserializeConfig(File.ReadAllText(path + ".bak"));
        Assert.NotNull(fromBak);
        Assert.True(fromBak!.ContainsKey("performance_mode"));
    }

    // ---------- ProgramData 副本同步 ----------

    [Fact]
    public void ShouldSyncFallback_SkipsWhenTargetIsMissingOrSameFile()
    {
        Assert.False(AppConfig.ShouldSyncFallback(null, @"C:\a\config.json"));
        Assert.False(AppConfig.ShouldSyncFallback("", @"C:\a\config.json"));
        Assert.False(AppConfig.ShouldSyncFallback(@"C:\a\config.json", @"C:\a\config.json"));
        Assert.False(AppConfig.ShouldSyncFallback(@"C:\A\CONFIG.JSON", @"C:\a\config.json"));
    }

    [Fact]
    public void ShouldSyncFallback_SkipsRootPathsThatHaveNoParentDirectory()
    {
        // Path.GetDirectoryName(@"C:\") 返回 null；旧代码会把 null 传给
        // Directory.CreateDirectory 并抛 ArgumentNullException。
        Assert.False(AppConfig.ShouldSyncFallback(@"C:\", @"D:\a\config.json"));
    }

    [Fact]
    public void ShouldSyncFallback_AllowsDistinctTargetWithParentDirectory()
    {
        Assert.True(AppConfig.ShouldSyncFallback(
            @"C:\ProgramData\MechrevoLite\config.json",
            @"C:\Users\me\AppData\Roaming\MechrevoLite\config.json"));
    }

    // ---------- BIOS 版本解析 ----------

    [Fact]
    public void ParseBiosVersion_SplitsAsusModelAndBiosNumber()
    {
        var (bios, model) = AppConfig.ParseBiosVersion("GX650PY.317");
        Assert.Equal("317", bios);
        Assert.Equal("GX650PY", model);
    }

    [Fact]
    public void ParseBiosVersion_KeepsMechrevoStringWholeInsteadOfProducingFakeValues()
    {
        // 本机实测值。旧逻辑按 '.' 切分后取 parts[1]/parts[0]，
        // 得到 (Bios="1", ModelShort="N") —— 两个都是假值。
        var (bios, model) = AppConfig.ParseBiosVersion("N.1.32MRO60");
        Assert.Equal("N.1.32MRO60", bios);
        Assert.Equal("", model);
    }

    [Theory]
    [InlineData(null, "", "")]
    [InlineData("", "", "")]
    [InlineData("   ", "", "")]
    [InlineData("1.32", "1.32", "")]            // 纯数字首段不是机型代号
    [InlineData("ABC.12", "ABC.12", "")]        // 首段过短，不像机型代号
    [InlineData("GX650PY.", "GX650PY.", "")]    // 版本号为空
    [InlineData("SomeBios", "SomeBios", "")]    // 无分隔符
    public void ParseBiosVersion_DegradesToRawStringForNonAsusFormats(string? raw, string expectedBios, string expectedModel)
    {
        var (bios, model) = AppConfig.ParseBiosVersion(raw);
        Assert.Equal(expectedBios, bios);
        Assert.Equal(expectedModel, model);
    }

    [Fact]
    public void ParseBiosVersion_TrimsSurroundingWhitespace()
    {
        var (bios, model) = AppConfig.ParseBiosVersion("  GZ302EA.402  ");
        Assert.Equal("402", bios);
        Assert.Equal("GZ302EA", model);
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; }

        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lm-appconfig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
