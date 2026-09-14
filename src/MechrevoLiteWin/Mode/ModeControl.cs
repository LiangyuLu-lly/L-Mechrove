// TODO(nullable-migration): 这个文件继承自 g-helper 的 UI/原生互操作代码，尚未完成可空性标注。
// 项目已开启完整的可空性检查（csproj 里的 Nullable=enable），核心硬件层与 Helpers 均已清零；
// 这里显式关闭，是为了让剩余债务可见且局部化，而不是靠项目级 annotations 把它藏起来。
// 迁移某个文件时删掉下面这行（保留注解上下文，只关闭警告），然后把该文件的 CS86xx 告警修干净即可。
#nullable disable warnings
using MechrevoLite.Gpu.NVidia;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using PawnIO;

namespace MechrevoLite.Mode
{
    public class ModeControl : IDisposable
    {

        static SettingsForm settings = Program.settingsForm;

        private static bool customFans = false;
        private static int customPower = 0;

        private int _cpuUV = 0;
        private int _igpuUV = 0;
        private int _cpuTemp = CpuInfo.DefaultTemp;
        private bool _ryzenPower = false;

        private static RyzenSmuService? _smu;
        private static readonly object _smuLock = new();

        private static RyzenSmuService? GetSmu()
        {
            lock (_smuLock)
            {
                if (_smu != null && _smu.IsInitialized) return _smu;
                _smu?.Dispose();
                _smu = new RyzenSmuService();
                if (!_smu.Initialize(System.Reflection.Assembly.GetExecutingAssembly()))
                {
                    _smu.Dispose();
                    _smu = null;
                }
                else
                {
                    Logger.WriteLine($"SMU Init: {_smu.CpuCodeName} ({_smu.Family}), SMU v{_smu.SmuVersion >> 16}.{(_smu.SmuVersion >> 8) & 0xFF}.{_smu.SmuVersion & 0xFF}");
                }
                return _smu;
            }
        }

        public static bool IsPawnAvailable()  => GetSmu() != null;
        public static bool IsPawnInstalled()   => RyzenSmuService.IsPawnInstalled();

        static System.Timers.Timer? reapplyTimer;
        static System.Timers.Timer modeToggleTimer = default!;
        static CancellationTokenSource? _modeCts = new();
        static Task _modeTask = Task.CompletedTask;

        public ModeControl()
        {
            int reapplyTime = AppConfig.Get("reapply_time", -1);
            if (reapplyTime < 0)
                reapplyTime = AppConfig.IsApplyUV() && IsReapplyTempRequired() ? 30 : 0;
            if (reapplyTime > 0)
            {
                reapplyTimer = new System.Timers.Timer(reapplyTime * 1000);
                reapplyTimer.Elapsed += ReapplyTimer_Elapsed;
            }
        }

        // Cezanne/Rembrandt (Renoir) + Phoenix/HawkPoint (Mobile) silently reset temp limit under load.
        private static bool IsReapplyTempRequired()
        {
            var smu = GetSmu();
            return smu != null && smu.Family is CpuFamily.Renoir or CpuFamily.Mobile;
        }

        private static bool IsReapplyRyzenRequired()
        {
            var smu = GetSmu();
            return smu != null && smu.Family is CpuFamily.Raphael;
        }

        private static void SetReapplyEnabled(bool enabled)
        {
            if (reapplyTimer != null) reapplyTimer.Enabled = enabled;
        }


        private void ReapplyTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            SetCPUTemp(AppConfig.GetMode("cpu_temp"));
            SetRyzenPower();
        }

        public void WaitForApply()
        {
            try { _modeTask.Wait(5000); } catch { }
        }

        public void AutoPerformance(bool powerChanged = false, bool preserveActiveCustom = false)
        {
            int selectedMode = Modes.GetCurrent();
            int hardwareMode = Program.service?.CurrentMode ?? -1;
            if (ShouldPreserveCustomMode(powerChanged, Program.hw is { IsConnected: true }, selectedMode, hardwareMode, preserveActiveCustom))
            {
                int hardwareProfile = Program.hw?.CustomProfileIndex ?? -1;
                int profile = hardwareProfile is >= 0 and <= 3
                    ? hardwareProfile
                    : Math.Clamp(AppConfig.Get("custom_last_profile", 0), 0, 3);
                // 文案必须说明真实触发原因。这个分支既可能由电源切换触发，也可能由
                // 启动/唤醒/灯效重连的 preserveActiveCustom 触发；此前一律写成
                // 「Power source changed」，而这正是用户发来排查的那份日志。
                string reason = powerChanged ? "power source changed" : "custom mode preserved on init/wake";
                Logger.WriteLine($"Reapplying custom profile {profile + 1} ({reason}).");
                _ = ReapplyCustomProfileAsync(profile);
                return;
            }

            int mode = AppConfig.Get("performance_" + Program.PerformanceKey());
            Logger.WriteLine($"{Program.currentSource} Performance Mode: {Modes.GetName(mode == -1 ? Modes.GetCurrent() : mode)}");

            if (mode != -1)
                SetPerformanceMode(mode, powerChanged);
            else
                SetPerformanceMode(Modes.GetCurrent());
        }

        internal static bool ShouldPreserveCustomMode(bool powerChanged, bool connected, int selectedMode, int hardwareMode, bool preserveActiveCustom = false) =>
            (powerChanged || preserveActiveCustom) && connected &&
            (selectedMode == MechrevoService.ModeCustom || hardwareMode == MechrevoService.ModeCustom);

        private static async Task ReapplyCustomProfileAsync(int profile)
        {
            try
            {
                MechrevoService? service = Program.service;
                if (service is null) return;
                bool confirmed = await service.SwitchCustomProfile(profile);
                if (confirmed)
                {
                    bool powerConfirmed = WinPowerPlan.ApplyProfile(profile);
                    Logger.WriteLine(powerConfirmed
                        ? $"Custom profile {profile + 1} reapplied."
                        : $"Custom profile {profile + 1} reapplied, but Windows power settings were not confirmed.");
                }
                else
                {
                    Logger.WriteLine($"Custom profile {profile + 1} was not confirmed by the service.");
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Custom profile power transition failed: " + ex.Message);
            }
        }


        public void ResetPerformanceMode()
        {
            ResetRyzen();

            _ = Program.acpi.SetPerformanceMode(Modes.GetCurrentBase());

            // Default power mode
            AppConfig.RemoveMode("powermode");
            PowerNative.SetPowerMode(Modes.GetCurrentBase());
        }

        public void Toast()
        {
            Program.toast.RunToast(Modes.GetCurrentName(), SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online ? ToastIcon.Charger : ToastIcon.Battery);
        }

        public void SetPerformanceMode(int mode = -1, bool notify = false)
        {

            int oldMode = Modes.GetCurrent();
            if (mode < 0) mode = oldMode;

            if (!Modes.Exists(mode)) mode = 0;
            Logger.WriteLine($"SetPerformanceMode(mode={mode} old={oldMode} exists={Modes.Exists(mode)})");

            settings.ShowMode(mode);

            Modes.SetCurrent(mode);


            var nextCts = new CancellationTokenSource();
            var previousCts = Interlocked.Exchange(ref _modeCts, nextCts);
            previousCts?.Cancel();
            if (_modeTask.IsCompleted) previousCts?.Dispose();
            var ct = nextCts.Token;

            _modeTask = Task.Run(async () =>
            {
                try
                {
                    bool reset = AppConfig.IsResetRequired() && (Modes.GetBase(oldMode) == Modes.GetBase(mode)) && customPower > 0 && !AppConfig.IsApplyPower();

                    customFans = false;
                    customPower = 0;

                    SetModeLabel();

                    // G14 2024 workaround 的 DeviceSet 在 Mechrevo 是空实现——等待无意义，移除 1.5s 假延迟
                    if (reset)
                    {
                        Program.acpi.DeviceSet(AsusACPI.PerformanceMode, (Modes.GetBase(oldMode) != 1) ? AsusACPI.PerformanceTurbo : AsusACPI.PerformanceBalanced, "ModeReset");
                    }

                    ct.ThrowIfCancellationRequested();

                    if (AppConfig.Is("status_mode")) Program.acpi.DeviceSet(AsusACPI.StatusMode, [0x00, Modes.GetBase(mode) == AsusACPI.PerformanceSilent ? (byte)0x02 : (byte)0x03], "StatusMode");
                    await Program.acpi.SetPerformanceMode(AppConfig.IsManualModeRequired() ? AsusACPI.PerformanceManual : Modes.GetBase(mode));
                    ct.ThrowIfCancellationRequested();

                    SetGPUClocks();

                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
                    ct.ThrowIfCancellationRequested();
                    AutoFans();
                    await Task.Delay(TimeSpan.FromMilliseconds(Program.hw is { IsConnected: true } ? 50 : 250), ct);
                    ct.ThrowIfCancellationRequested();
                    await AutoPower(cancellationToken: ct);
                    
                    var command = AppConfig.GetModeString("mode_command");
                    if (command is not null)
                    {   Logger.WriteLine("Running mode command: " + command);
                        RestrictedProcessHelper.RunAsRestrictedUser(command);
                    }
                }
                catch (OperationCanceledException)
                {
                    Logger.WriteLine($"SetPerformanceMode cancelled (mode {mode})");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine($"SetPerformanceMode failed (mode {mode}): {ex.Message}");
                }
                finally
                {
                    if (!ReferenceEquals(Volatile.Read(ref _modeCts), nextCts))
                        nextCts.Dispose();
                }
            });

            if (notify) Toast();

            if (!AppConfig.Is("skip_powermode"))
            {
                // Windows power mode
                if (AppConfig.GetModeString("powermode") is not null)
                    PowerNative.SetPowerMode(AppConfig.GetModeString("powermode"));
                else
                    PowerNative.SetPowerMode(Modes.GetBase(mode));

                if (AppConfig.IsAutoASPM()) PowerNative.SetBalancedASPM();
            }

            // CPU Boost setting override
            if (AppConfig.GetMode("auto_boost") != -1)
                    PowerNative.SetCPUBoost(AppConfig.GetMode("auto_boost"));
        }


        private void ModeToggleTimer_Elapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            modeToggleTimer.Stop();
            Logger.WriteLine($"Hotkey mode: {Modes.GetCurrent()}");
            SetPerformanceMode();

        }

        public void CyclePerformanceMode(bool back = false)
        {
            int delay = AppConfig.Get("mode_delay", 1000);

            if (modeToggleTimer is null)
            {
                modeToggleTimer = new System.Timers.Timer(delay);
                modeToggleTimer.Elapsed += ModeToggleTimer_Elapsed;
            }

            modeToggleTimer.Stop();
            modeToggleTimer.Start();
            Modes.SetCurrent(Modes.GetNext(back));
            Toast();
        }

        public void AutoFans(bool force = false)
        {
            customFans = false;

            if (AppConfig.IsApplyFans() || force)
            {
                // Mechrevo：曲线由服务端按模式管理（保存时已写入），切换模式时无需用本地配置重写——跳过，避免旧数据污染
                // MQTT 未连接时同样静默跳过（旧 ACPI 路径全是存根，只会弹"不支持自定义风扇曲线"误导信息）
                if (Program.hw is { IsConnected: true })
                {
                    customFans = true;
                    return;
                }
            }
            // else 分支原来调 XGM.Reset()（重置 XG Mobile 外置显卡坞的风扇），
            // 那是华硕硬件，机械革命没有，已删除。

            SetModeLabel();

        }

        public async Task AutoPower(bool launchAsAdmin = false, CancellationToken cancellationToken = default)
        {

            customPower = 0;

            bool applyPower = AppConfig.IsApplyPower();
            bool applyFans = AppConfig.IsApplyFans();

            if (applyPower && !applyFans && AppConfig.IsFanRequired())
            {
                AutoFans(true);
                if (Program.hw is not { IsConnected: true })
                    await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            if (applyPower) await SetPower(launchAsAdmin, cancellationToken).ConfigureAwait(false);

            if (Program.hw is not { IsConnected: true })
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SetGPUPower();
            AutoRyzen();

            if (IsReapplyRyzenRequired())
                _ = ReapplyRyzenAfterDelayAsync(cancellationToken);

        }

        private async Task ReapplyRyzenAfterDelayAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(5000, cancellationToken).ConfigureAwait(false);
                AutoRyzen();
                ReadRyzenLimits();
            }
            catch (OperationCanceledException)
            {
                // A newer performance mode superseded this delayed reapply.
            }
            catch (Exception ex)
            {
                Logger.WriteLine("Delayed Ryzen settings reapply failed: " + ex.Message);
            }
        }

        /// <summary>MechrevoService 切换成功后同步模式状态（Modes/AppConfig）。</summary>
        public static void SyncExternalModeStatic(int mode)
        {
            Modes.SetCurrent(mode);
            AppConfig.Set("performance_mode", mode);
        }

        /// <summary>外部模式变化（快捷键/原版控制中心）同步 UI 高亮。</summary>
        public void SyncExternalMode(int mechrevoMode)
        {
            int ghelper = Program.hw?.GHelperMode ?? AsusACPI.PerformanceBalanced;
            settings.ShowMode(ghelper);
            Modes.SetCurrent(ghelper);
            SetModeLabel();
        }

        public void SetModeLabel()
        {
            settings.SetModeLabel(Properties.Strings.PerformanceMode + ": " + Modes.GetCurrentName() + (customFans ? "+" : "") + ((customPower > 0) ? " " + customPower + "W" : ""));
        }

        public void SetRyzenPower(bool init = false)
        {
            if (init) _ryzenPower = true;

            if (!_ryzenPower) return;
            if (!AppConfig.IsApplyPower()) return;

            var smu = GetSmu();
            if (smu == null) return;

            int limit_total = AppConfig.GetMode("limit_total");
            int limit_slow = AppConfig.GetMode("limit_slow", limit_total);
            int limit_fast = AppConfig.GetMode("limit_fast", limit_slow);

            if (limit_total > AsusACPI.MaxTotal) return;
            if (limit_total < AsusACPI.MinTotal) return;

            smu.SetAllLimits(limit_total, limit_fast, limit_slow,
                out SmuStatus stapm, out SmuStatus fast, out SmuStatus slow);
            if (init) Logger.WriteLine($"STAPM: {limit_total}W {stapm} | SLOW: {limit_slow}W {slow} | FAST: {limit_fast}W {fast}");
        }

        public async Task SetPower(bool launchAsAdmin = false, CancellationToken cancellationToken = default)
        {

            bool allAMD = Program.acpi.IsAllAmdPPT();
            bool isAMD = CpuInfo.IsAMD;

            int limit_total = AppConfig.GetMode("limit_total");
            int limit_cpu = AppConfig.GetMode("limit_cpu");
            int limit_slow = AppConfig.GetMode("limit_slow");
            int limit_fast = AppConfig.GetMode("limit_fast");

            if (limit_slow < 0 || allAMD) limit_slow = limit_total;

            if (limit_total > AsusACPI.MaxTotal) return;
            if (limit_total < AsusACPI.MinTotal) return;

            if (limit_cpu > AsusACPI.MaxCPU) return;
            if (limit_cpu < AsusACPI.MinCPU) return;

            if (limit_fast > AsusACPI.MaxTotal) return;
            if (limit_fast < AsusACPI.MinTotal) return;

            if (limit_slow > AsusACPI.MaxTotal) return;
            if (limit_slow < AsusACPI.MinTotal) return;

            // Mechrevo：PL1(持续)=limit_slow, PL2(峰值)=limit_fast，走 MQTT
            if (Program.hw is { IsConnected: true })
            {
                int pl1 = limit_slow >= 0 ? limit_slow : limit_total;
                int pl2 = limit_fast >= 0 ? limit_fast : limit_total;
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyMechrevoPowerLimitsAsync(pl1, pl2).ConfigureAwait(false);
                customPower = limit_total;
                SetModeLabel();
                return;
            }

            // SPL and SPPT
            if (Program.acpi.IsSupported(AsusACPI.PPT_APUA0))
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_APUA3, limit_total, "PowerLimit A3");
                Program.acpi.DeviceSet(AsusACPI.PPT_APUA0, limit_slow, "PowerLimit A0");
                customPower = limit_total;
            }
            else if (isAMD)
            {
                if (ProcessHelper.IsUserAdministrator())
                {
                    SetRyzenPower(true);
                }
                else if (launchAsAdmin)
                {
                    ProcessHelper.RunAsAdmin("cpu");
                    return;
                }
            }

            if (allAMD) // CPU limit all amd models
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_CPUB0, limit_cpu, "PowerLimit B0");
                customPower = limit_cpu;
            }
            else if (isAMD && Program.acpi.IsSupported(AsusACPI.PPT_APUC1)) // FPPT boost for non all-amd models
            {
                Program.acpi.DeviceSet(AsusACPI.PPT_APUC1, limit_fast, "PowerLimit C1");
            }

            SetModeLabel();

        }

        public void SetGPUClocks(bool launchAsAdmin = false, bool reset = false)
        {
            Task.Run(() =>
            {

                int core = AppConfig.GetMode("gpu_core");
                int memory = AppConfig.GetMode("gpu_memory");
                int clock_limit = AppConfig.GetMode("gpu_clock_limit");

                if (reset) core = memory = clock_limit = 0;

                if (core == -1 && memory == -1 && clock_limit == -1) return;
                //if ((gpu_core > -5 && gpu_core < 5) && (gpu_memory > -5 && gpu_memory < 5)) launchAsAdmin = false;

                if (Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1) { Logger.WriteLine("Clocks: Eco"); return; }
                if (HardwareControl.GpuControl is null) { Logger.WriteLine("Clocks: NoGPUControl"); return; }
                if (!HardwareControl.GpuControl!.IsNvidia) { Logger.WriteLine("Clocks: NotNvidia"); return; }

                NvidiaGpuControl nvControl = (NvidiaGpuControl)HardwareControl.GpuControl;
                try
                {
                    int statusClocks = nvControl.SetClocks(core, memory);
                    int statusLimit = nvControl.SetMaxGPUClock(clock_limit);
                    if ((statusLimit != 0 || statusClocks != 0) && launchAsAdmin) ProcessHelper.RunAsAdmin("gpu");
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Clocks Error:" + ex.ToString());
                }
            });
        }

        public void SetGPUPower()
        {

            int gpu_boost = AppConfig.GetMode("gpu_boost");
            int gpu_temp = AppConfig.GetMode("gpu_temp");
            int gpu_power = AppConfig.GetMode("gpu_power");

            int boostResult = -1;

            if (gpu_power >= AsusACPI.MinGPUPower && gpu_power <= AsusACPI.MaxGPUPower && Program.acpi.IsSupported(AsusACPI.GPU_POWER))
                Program.acpi.DeviceSet(AsusACPI.GPU_POWER, gpu_power, "PowerLimit TGP (GPU VAR)");

            if (gpu_boost >= AsusACPI.MinGPUBoost && gpu_boost <= AsusACPI.MaxGPUBoost && Program.acpi.IsSupported(AsusACPI.PPT_GPUC0))
                boostResult = Program.acpi.DeviceSet(AsusACPI.PPT_GPUC0, gpu_boost, "PowerLimit C0 (GPU BOOST)");

            if (gpu_temp >= AsusACPI.MinGPUTemp && gpu_temp <= AsusACPI.MaxGPUTemp && Program.acpi.IsSupported(AsusACPI.PPT_GPUC2))
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC2, gpu_temp, "PowerLimit C2 (GPU TEMP)");

            // Fallback
            if (boostResult == 0)
                Program.acpi.DeviceSet(AsusACPI.PPT_GPUC0, gpu_boost, "PowerLimit C0");

        }

        public SmuStatus? SetCPUTemp(int cpuTemp, bool log = false)
        {
            if (cpuTemp < CpuInfo.MinTemp || cpuTemp > CpuInfo.DefaultTemp) return null;
            if (cpuTemp == CpuInfo.DefaultTemp && _cpuTemp == CpuInfo.DefaultTemp) return null;

            var smu = GetSmu();
            if (smu == null) return null;
            SmuStatus status = smu.SetThm(cpuTemp);
            if (log) Logger.WriteLine($"CPU Temp: {cpuTemp}°C {status}");
            if (status == SmuStatus.OK) _cpuTemp = cpuTemp;
            return status;
        }

        public void SetUV(int cpuUV)
        {
            if (!CpuInfo.IsSupportedUV()) return;

            if (cpuUV >= CpuInfo.MinCPUUV && cpuUV <= CpuInfo.MaxCPUUV)
            {
                var smu = GetSmu();
                if (smu == null) return;
                SmuStatus status = smu.SetCoAll(cpuUV);
                Logger.WriteLine($"UV: {cpuUV} {status}");
                if (status == SmuStatus.OK) _cpuUV = cpuUV;
            }
        }

        public void SetUViGPU(int igpuUV)
        {
            if (!CpuInfo.IsSupportedUViGPU()) return;

            if (igpuUV >= CpuInfo.MinIGPUUV && igpuUV <= CpuInfo.MaxIGPUUV)
            {
                var smu = GetSmu();
                if (smu == null) return;
                SmuStatus status = smu.SetCoGfx(igpuUV);
                Logger.WriteLine($"iGPU UV: {igpuUV} {status}");
                if (status == SmuStatus.OK) _igpuUV = igpuUV;
            }
        }

        public string SetRyzen(bool launchAsAdmin = false)
        {
            if (!ProcessHelper.IsUserAdministrator())
            {
                if (launchAsAdmin) ProcessHelper.RunAsAdmin("uv");
                return string.Empty;
            }

            var smu = GetSmu();
            if (smu == null) return string.Empty;

            var lines = new System.Text.StringBuilder();
            try
            {
                int cpuUV   = AppConfig.GetMode("cpu_uv",   0);
                int igpuUV  = AppConfig.GetMode("igpu_uv",  0);
                int cpuTemp = AppConfig.GetMode("cpu_temp");

                if (CpuInfo.IsSupportedUV() && cpuUV >= CpuInfo.MinCPUUV && cpuUV <= CpuInfo.MaxCPUUV)
                {
                    SmuStatus s = smu.SetCoAll(cpuUV);
                    Logger.WriteLine($"UV: {cpuUV} {s}");
                    if (s == SmuStatus.OK) _cpuUV = cpuUV;
                    lines.AppendLine($"CPU UV {cpuUV}: {s}");
                }

                if (CpuInfo.IsSupportedUViGPU() && igpuUV >= CpuInfo.MinIGPUUV && igpuUV <= CpuInfo.MaxIGPUUV)
                {
                    SmuStatus s = smu.SetCoGfx(igpuUV);
                    Logger.WriteLine($"iGPU UV: {igpuUV} {s}");
                    if (s == SmuStatus.OK) _igpuUV = igpuUV;
                    lines.AppendLine($"iGPU UV {igpuUV}: {s}");
                }

                SmuStatus? tempStatus = SetCPUTemp(cpuTemp, true);
                if (tempStatus.HasValue) lines.AppendLine($"CPU Temp {cpuTemp}°C: {tempStatus}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine("UV Error: " + ex.ToString());
            }

            SetReapplyEnabled(AppConfig.IsApplyUV());
            return lines.ToString().TrimEnd();
        }

        public string ReadRyzenLimits()
        {
            var smu = GetSmu();
            if (smu == null) return string.Empty;

            try
            {
                PowerLimits? lim = smu.GetPowerLimits();
                if (lim == null) return string.Empty;

                string line = $"SPL: {lim.Stapm:F1}W | sPPT {lim.Slow:F1}W | fPPT {lim.Fast:F1}W";
                if (lim.ApuSlow.HasValue) line += $" | APU {lim.ApuSlow.Value:F1}W";
                line += $", Temp: {lim.TctlTemp:F0}°C";
                Logger.WriteLine("Ryzen Limits: " + line);
                return line;
            }
            catch (Exception ex)
            {
                Logger.WriteLine("ReadRyzenLimits Error: " + ex.ToString());
                return string.Empty;
            }
        }

        public void ResetRyzen()
        {
            if (_cpuUV != 0) SetUV(0);
            if (_igpuUV != 0) SetUViGPU(0);
            if (_cpuTemp != CpuInfo.DefaultTemp) SetCPUTemp(CpuInfo.DefaultTemp, true);
            SetReapplyEnabled(false);
        }

        public void AutoRyzen()
        {
            if (!CpuInfo.IsAMD) return;

            if (AppConfig.IsApplyUV()) SetRyzen();
            else ResetRyzen();
        }

        public void AutoCPUTemp()
        {
            if (!CpuInfo.IsAMD) return;
            if (!AppConfig.IsApplyUV()) return;
            if (!ProcessHelper.IsUserAdministrator()) return;

            try
            {
                SetCPUTemp(AppConfig.GetMode("cpu_temp"), true);
            }
            catch (Exception ex)
            {
                Logger.WriteLine("AutoCPUTemp Error: " + ex.Message);
            }
        }

        public void ShutdownReset()
        {
            if (!AppConfig.IsShutdownReset()) return;
            Program.acpi.DeviceSet(AsusACPI.PerformanceMode,AsusACPI.PerformanceBalanced, "Mode Reset");
        }

        public void SleepReset()
        {
            if (!AppConfig.IsSleepReset()) return;
            Program.acpi.DeviceSet(AsusACPI.PerformanceMode, Modes.GetCurrentBase(), "Sleep Reset");
        }

        async Task ApplyMechrevoPowerLimitsAsync(int pl1, int pl2)
        {
            try
            {
                if (Program.hw is not { IsConnected: true }) return;
                if (!await Program.hw.SetPl1Pl2(pl1, pl2))
                    Logger.WriteLine($"PL1/PL2 update not confirmed: requested={pl1}/{pl2}, actual={Program.hw.Pl1}/{Program.hw.Pl2}");
            }
            catch (Exception ex) { Logger.WriteLine("PL1/PL2 update failed: " + ex.Message); }
        }

        public void Dispose()
        {
            var modeCts = Interlocked.Exchange(ref _modeCts, null);
            modeCts?.Cancel();
            if (_modeTask.IsCompleted) modeCts?.Dispose();
            reapplyTimer?.Stop();
            reapplyTimer?.Dispose();
            reapplyTimer = null;
            modeToggleTimer?.Stop();
            modeToggleTimer?.Dispose();
            lock (_smuLock)
            {
                _smu?.Dispose();
                _smu = null;
            }
        }

    }
}
