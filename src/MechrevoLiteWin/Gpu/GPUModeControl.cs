using MechrevoLite.Display;
using MechrevoLite.Gpu.NVidia;
using MechrevoLite.Hardware;
using MechrevoLite.Helpers;
using System.Diagnostics;

namespace MechrevoLite.Gpu
{
    public class GPUModeControl
    {
        SettingsForm settings;

        public static int gpuMode;
        public static bool? gpuExists = null;

        static bool nvRestartPending;


        public GPUModeControl(SettingsForm settingsForm)
        {
            settings = settingsForm;
        }

        public void InitGPUMode()
        {
            if (Program.hw is not null && (Program.hw.Capabilities.ProfileAvailable || Program.hw.SettingStatusSeen))
            {
                bool switchSupported = Program.hw.CanOfferGpuModeSwitch;
                bool ecoSupported = switchSupported && Program.hw.CanOfferIgpuOnly;
                bool muxSupported = switchSupported && Program.hw.SupportsDgpuDirect;
                settings.VisualiseGPUButtons(ecoSupported || muxSupported, muxSupported, ecoSupported);
                settings.RefreshDeviceCapabilities();
                if (!ecoSupported && AppConfig.Is("gpu_auto"))
                {
                    AppConfig.Set("gpu_auto", 0);
                    AppConfig.Set("gpu_mode", MechrevoService.GpuStandard);
                }
                if (!ecoSupported && !muxSupported)
                {
                    settings.HideGPUModes(false);
                    return;
                }

                int configuredMode = AppConfig.Get("gpu_mode");
                if (configuredMode == MechrevoService.GpuStandard && AppConfig.Is("gpu_auto"))
                    configuredMode = MechrevoService.GpuAuto; // beta5.x 配置迁移
                gpuMode = AppConfig.Is("gpu_auto") && ecoSupported
                    ? MechrevoService.GpuAuto
                    : Program.hw.GpuMode is >= MechrevoService.GpuIGpu and <= MechrevoService.GpuAuto
                        ? Program.hw.GpuMode
                        : configuredMode;
                settings.VisualiseGPUMode(gpuMode);
                return;
            }

            if (AppConfig.NoGpu())
            {
                settings.HideGPUModes(false);
                return;
            }

            int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);

            Logger.WriteLine("Eco flag : " + eco);
            Logger.WriteLine("Mux flag : " + mux);

            if (eco == 1 && HardwareControl.GpuControl?.IsValid == true)
            {
                // T29：原先这里在 IsEcoBootFix()（ASUS 机型串）为真时 DisposeGpuControl；
                // 该谓词在机械革命机型上恒假，分支从未执行，随 ASUS 残留一并删除。
                Logger.WriteLine("Eco half-state");
            }

            settings.VisualiseGPUButtons(eco >= 0 || mux >= 0, mux >= 0, eco >= 0);

            if (mux == 0)
            {
                gpuMode = AsusACPI.GPUModeUltimate;
            }
            else
            {
                if (eco == 1)
                    gpuMode = AsusACPI.GPUModeEco;
                else
                    gpuMode = AsusACPI.GPUModeStandard;

                // GPU mode not supported
                if (eco < 0 && mux < 0)
                {
                    if (gpuExists is null) gpuExists = Program.acpi.GetFan(AsusFan.GPU) >= 0;
                    settings.HideGPUModes((bool)gpuExists);
                }
            }

            AppConfig.Set("gpu_mode", gpuMode);
            settings.VisualiseGPUMode(gpuMode);

            Aura.CustomRGB.ApplyGPUColor(gpuMode);

            Task.Run(CheckGpuError);

        }



        public async Task SetGPUMode(int GPUMode, int auto = 0)
        {
            int CurrentGPU = Program.hw?.GpuMode is >= MechrevoService.GpuIGpu and <= MechrevoService.GpuAuto
                ? Program.hw.GpuMode
                : AppConfig.Get("gpu_mode");

            if (CurrentGPU == GPUMode)
            {
                settings.VisualiseGPUMode();
                return;
            }

            // Mechrevo：MUX 切换走 MQTT（Setting/Control IGPU_ONLY_CONNECT_RB_*），切换后回读确认
            if (Program.hw is { IsConnected: true })
            {
                var target = GPUMode;
                try
                {
                    // 手动选具体模式必须先退出自动：gpu_auto=1 时 Program 的回显处理器会把
                    // 标准/核显回显重新标注成「自动」，gpu_auto 永远清不掉；二十秒后电源事件
                    // 触发的自动流程还会取消这次手动切换——用户点了核显，软件自己又切回去。
                    // 原来只在确认成功后才写 gpu_auto，而失败/被取消时永远走不到那里。
                    if (auto == 0) AppConfig.Set("gpu_auto", 0);

                    // 与 MechrevoService 同一条热切换判定：只有「标准→核显」且机型声明支持
                    // 热切换时，独显占用才会挡住 EC 断电，才需要预检和重启生效兜底。
                    int modeBeforeSwitch = Program.hw.GpuMode;
                    bool hotIgpuSwitch = target == MechrevoService.GpuIGpu &&
                        Program.hw.CanOfferGpuHotSwap &&
                        (modeBeforeSwitch == MechrevoService.GpuStandard ||
                         (modeBeforeSwitch == MechrevoService.GpuAuto && Program.hw.GpuSwitchResult == 1));
                    if (hotIgpuSwitch && !await CloseDgpuApplicationsForManualSwitchAsync().ConfigureAwait(false))
                    {
                        settings.VisualiseGPUMode(modeBeforeSwitch);
                        return;   // 用户取消关闭独显程序，保持现状
                    }

                    Interlocked.Exchange(ref _manualGpuSwitchInFlight, 1);
                    bool confirmed;
                    try
                    {
                        confirmed = Program.service is not null
                            ? await Program.service.SwitchGpuMode(
                                target, keepRegisterOnHotSwitchTimeout: hotIgpuSwitch)
                            : await Program.hw.SetGpuMode(target);
                    }
                    finally { Interlocked.Exchange(ref _manualGpuSwitchInFlight, 0); }
                    if (confirmed)
                    {
                        AppConfig.Set("gpu_mode", target);
                        AppConfig.Set("gpu_auto", auto);
                        settings.VisualiseGPUMode(target);
                    }
                    else if (hotIgpuSwitch)
                    {
                        // 实时切换失败：寄存器已保留（服务层未回滚），把「重启生效」的决定权交给用户。
                        // 这是不支持实时核显切换的机型上唯一 100% 可行的路径。
                        Logger.WriteLine($"Manual hot switch to iGPU not confirmed; offering reboot-to-apply (expected={target} actual={Program.hw.GpuMode})");
                        await OfferRebootFallbackAsync(modeBeforeSwitch, target).ConfigureAwait(false);
                    }
                    else
                    {
                        // 失败必须让用户看见：过去这里只写日志，而界面此前已被 GCU 的
                        // 状态回显画成了目标模式，用户看到的就是「切换成功」。
                        // 服务层在热切换超时后已经回滚，这里的 GpuMode 是回滚后的真实状态。
                        settings.VisualiseGPUMode(Program.hw.GpuMode);
                        Logger.WriteLine($"Legacy SetGPUMode not confirmed: expected={target} actual={Program.hw.GpuMode}");
                        try { Program.toast?.RunToast("显卡模式切换失败，已恢复切换前模式。"); }
                        catch (Exception tex) { Logger.WriteLine("GPU switch failure toast failed: " + tex.Message); }
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Legacy SetGPUMode failed: " + ex.Message);
                    if (Program.hw is not null)
                        settings.VisualiseGPUMode(Program.hw.GpuMode);
                    try { Program.toast?.RunToast("显卡模式切换失败，已恢复切换前模式。"); }
                    catch (Exception tex) { Logger.WriteLine("GPU switch failure toast failed: " + tex.Message); }
                }
                return;
            }

            var restart = false;
            var changed = false;

            if (CurrentGPU == AsusACPI.GPUModeUltimate)
            {
                DialogResult dialogResult = MessageBox.Show(Properties.Strings.AlertUltimateOff, Properties.Strings.AlertUltimateTitle, MessageBoxButtons.YesNo);
                if (dialogResult == DialogResult.Yes)
                {
                    // 确认框刚点过，立刻捕获重启凭证（此时确有新鲜输入），
                    // 重启请求不会因 500ms 窗口过期被守卫丢掉。
                    SystemRestart.CaptureUserConfirmation();
                    restart = true;
                    changed = true;
                }
            }
            else if (GPUMode == AsusACPI.GPUModeUltimate)
            {
                if (Program.acpi.DeviceGet(AsusACPI.GPUMux) < 0)
                {
                    Logger.WriteLine("Mux not supported");
                    settings.VisualiseGPUMode();
                    return;
                }

                DialogResult dialogResult = MessageBox.Show(Properties.Strings.AlertUltimateOn, Properties.Strings.AlertUltimateTitle, MessageBoxButtons.YesNo);
                if (dialogResult == DialogResult.Yes)
                {
                    // 必须在 await Task.Delay(500) 之前捕获：确认后的这 500ms 会吃掉
                    // GetLastInputInfo 的 500ms 窗口，过去导致这里请求的重启永远被守卫拒绝。
                    SystemRestart.CaptureUserConfirmation();
                    Program.acpi.SetGPUEco(0);
                    await Task.Delay(500);

                    int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
                    Logger.WriteLine("Eco flag : " + eco);
                    if (eco == 1)
                    {
                        settings.VisualiseGPUMode();
                        return;
                    }

                    restart = true;
                    changed = true;
                }

            }
            else if (GPUMode == AsusACPI.GPUModeEco)
            {
                settings.VisualiseGPUMode(GPUMode);
                SetGPUEco(1);
                changed = true;
            }
            else if (GPUMode == AsusACPI.GPUModeStandard)
            {
                settings.VisualiseGPUMode(GPUMode);
                SetGPUEco(0);
                changed = true;
            }

            if (changed)
            {
                AppConfig.Set("gpu_mode", GPUMode);
            }

            if (restart)
            {
                settings.VisualiseGPUMode();
                // 不可逆动作走统一入口（真实输入或确认凭证 + 后台线程），
                // 程序化路径两者皆无 → 拒绝，绝不静默重启。
                SystemRestart.RequestRestart("legacy GPU mode switch", SystemRestart.RebootNowArguments);
            }

        }



        public void SetGPUEco(int eco)
        {

            settings.LockGPUModes();

            Task.Run(async () =>
            {

                int status = 1;

                Program.modeControl.WaitForApply();

                if (eco == 1)
                {
                    HardwareControl.KillGPUApps();
                    HardwareControl.DisposeGpuControl();
                    if (AppConfig.IsNVPlatform()) NvidiaGpuControl.StopNVService();
                }

                Logger.WriteLine($"Running eco command {eco}");

                try
                {

                    status = Program.acpi.SetGPUEco(eco);
                    await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("refresh_delay", 500)));

                    await settings.InvokeAsync(() =>
                    {
                        InitGPUMode();
                        ScreenControl.AutoScreen();
                    });

                    if (eco == 0)
                    {
                        if (AppConfig.IsNVPlatform() || nvRestartPending)
                        {
                            await settings.InvokeAsync(() => settings.LockGPUModes(Properties.Strings.RestartingNVServices));
                            await Task.Delay(TimeSpan.FromMilliseconds(AppConfig.Get("nv_delay", 5000)));
                            if (AppConfig.IsNVPlatform()) NvidiaGpuControl.RestartNVService();
                            else NvidiaGpuControl.RestartNvContainer();
                            nvRestartPending = false;
                            await settings.InvokeAsync(InitGPUMode);
                            await Task.Delay(TimeSpan.FromMilliseconds(1000));
                        }

                        for (int i = 0; i < 3; i++)
                        {
                            HardwareControl.RecreateGpuControl();
                            if (HardwareControl.GpuControl is not null) break;
                            await Task.Delay(TimeSpan.FromSeconds(2));
                        }
                        Program.modeControl.SetGPUClocks(false);
                    }

                    if (AppConfig.IsModeReapplyRequired() && MechrevoLite.Mode.PerfModeService.Instance is { } perfModes)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(3000));
                        await perfModes.ReplayAsync("gpu eco switch", powerSourceChanged: false, appSideOnly: false);
                    }
                }
                catch (Exception ex)
                {
                    Logger.WriteLine("Error setting GPU Eco: " + ex.Message);
                }

            });


        }

        /// <summary>手动显卡切换在途标志：自动流程见到它必须让位，否则会取消用户的在途切换。</summary>
        static int _manualGpuSwitchInFlight;

        /// <summary>
        /// 是否插着外接电源。USBC 档位不可达：ReadPowerSource 判定 USBC 需要 ASUS 的
        /// ChargerMode 设备码，本机读不到（恒 NotSupported），插电只可能是 Barrel。
        /// </summary>
        public static bool IsPlugged() =>
            Program.currentSource == Program.PowerSource.Barrel;

        public bool AutoGPUMode(bool optimized = false, int delay = 0)
        {

            bool GpuAuto = AppConfig.Is("gpu_auto");
            // 强制模式不再是机型串判定：必须矩阵/画像声明本机支持至少一条 GPU 通路（E1）。
            bool ForceGPU = AppConfig.IsForceSetGPUMode() && !GpuAuto &&
                (Program.hw?.SupportsDgpuDirect == true || Program.hw?.SupportsIgpuOnly == true);

            int GpuMode = AppConfig.Get("gpu_mode");

            if (GpuSwitchPolicy.ShouldDeferUntilGcuConnection(
                    Program.hw is not null,
                    Program.hw?.IsConnected == true,
                    Program.service is not null))
            {
                Logger.WriteLine("Automatic GPU mode deferred until the GCU connection is ready.");
                return false;
            }

            if (Program.hw is { IsConnected: true })
            {
                if (!GpuAuto || Program.service is null) return false;
                if (!Program.hw.SupportsIgpuOnly)
                {
                    AppConfig.Set("gpu_auto", 0);
                    AppConfig.Set("gpu_mode", MechrevoService.GpuStandard);
                    Logger.WriteLine("Automatic GPU mode disabled: iGPU-only is not supported by this device.");
                    InitGPUMode();
                    return false;
                }

                _ = Task.Run(async () =>
                {
                    if (delay > 0) await Task.Delay(delay).ConfigureAwait(false);
                    bool plugged = IsPlugged();
                    bool confirmed = await TryApplyAutomaticHotSwitchAsync(plugged).ConfigureAwait(false);
                    Logger.WriteLine($"GCU automatic GPU mode after power change: confirmed={confirmed}, plugged={plugged}, actual={Program.hw.GpuMode}, runtime={Program.hw.GpuSwitchResult}");
                    try
                    {
                        if (confirmed && settings is { IsDisposed: false })
                            settings.BeginInvoke(() => settings.VisualiseGPUMode(MechrevoService.GpuAuto));
                    }
                    catch (Exception ex) { Logger.WriteLine("Automatic GPU UI refresh failed: " + ex.Message); }
                });
                return true;
            }

            if (!GpuAuto && !ForceGPU) return false;

            int eco = Program.acpi.DeviceGet(AsusACPI.GPUEco);
            int mux = Program.acpi.DeviceGet(AsusACPI.GPUMux);

            if (mux == 0)
            {
                if (optimized) _ = SetGPUMode(AsusACPI.GPUModeStandard, 1);
                return false;
            }
            else
            {

                if (eco == 1)
                    if ((GpuAuto && IsPlugged()) || (ForceGPU && GpuMode == AsusACPI.GPUModeStandard))
                    {
                        ScheduleGpuEco(0, delay);
                        return true;
                    }
                if (eco == 0)
                    if ((GpuAuto && !IsPlugged()) || (ForceGPU && GpuMode == AsusACPI.GPUModeEco))
                    {
                        // beta17 移除：此处曾有「GPU 正在被占用，是否仍切换?」确认框，但其判定 IsUsedGPU() 被硬编码为恒 false，从未触发过，属死代码；
                        // 若将来引入真实的占用判定，需有意重新加回该确认门。
                        ScheduleGpuEco(1, delay);
                        return true;
                    }
            }

            return false;

        }

        internal async Task<bool> TryApplyAutomaticHotSwitchAsync(bool plugged)
        {
            if (Program.hw is not { IsConnected: true } || Program.service is null)
                return false;

            // 手动切换在途时不插手：自动流程的 SwitchGpuMode 会经 previous?.Cancel()
            // 取消在途请求，用户刚点核显 20 秒后被自动流程切回去就是这么来的。
            if (Interlocked.CompareExchange(ref _manualGpuSwitchInFlight, 0, 0) == 1)
            {
                Logger.WriteLine("Automatic GPU mode skipped: a manual GPU switch is in flight.");
                return false;
            }

            // 自动档一律交给服务端自己的自动切换。过去这里还有一条客户端自管的路径，
            // 由「自动模式离电切核显 / 允许关闭独显程序」两个选项开启；这两个选项已按用户
            // 决定整体移除（2026-09-11），自管路径连同它的预检与强杀逻辑一起删掉——
            // 本机这两个选项原本就默认关闭，因此行为与旧默认一致。
            return await Program.service.SwitchAutomaticGpuMode(plugged).ConfigureAwait(false);
        }

        void NotifyAutomaticHotSwitchBlocked(string message)
        {
            Logger.WriteLine("Automatic GPU hot switch: " + message);
            try { Program.toast?.RunToast(message); }
            catch (Exception ex) { Logger.WriteLine("Automatic GPU hot switch notification failed: " + ex.Message); }
        }

        /// <summary>
        /// 手动热切换到核显前的独显占用预检。有程序占用时先征求同意，优雅关闭→强杀；
        /// 杀不掉的（通常是权限更高的进程）不阻塞切换，但明确告知可能失败。
        /// 返回 false 表示用户取消。
        /// </summary>
        async Task<bool> CloseDgpuApplicationsForManualSwitchAsync()
        {
            DgpuApplicationSnapshot snapshot = DgpuApplicationCoordinator.Snapshot();
            if (!snapshot.IsAvailable || snapshot.Applications.Count == 0) return true;

            string listing = string.Join("\n", snapshot.Applications.Select(a =>
                "· " + a.ProcessName + "  (PID " + a.ProcessId + ")"));
            if (MessageBox.Show(
                    "以下程序正在占用独显，需要先关闭才能完成核显切换：\n\n" + listing +
                    "\n\n关闭这些程序并继续切换？",
                    "切换到核显模式", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                return false;

            Logger.WriteLine("Manual iGPU preflight: closing dGPU applications: " +
                string.Join(", ", snapshot.Applications.Select(a => a.ProcessName + "#" + a.ProcessId)));
            snapshot = await DgpuApplicationCoordinator.RequestGracefulCloseAsync(snapshot.Applications).ConfigureAwait(false);
            if (snapshot.Applications.Count > 0)
                snapshot = await DgpuApplicationCoordinator.ForceCloseAsync(snapshot.Applications).ConfigureAwait(false);

            if (snapshot.Applications.Count > 0)
            {
                string leftover = string.Join(", ", snapshot.Applications.Select(a => a.ProcessName + "#" + a.ProcessId));
                Logger.WriteLine("Manual iGPU preflight: unclosable dGPU applications remain: " + leftover);
                DialogResult elevate = MessageBox.Show(
                    "以下程序无法以普通权限结束（可能以管理员身份运行）：\n\n" +
                    string.Join("\n", snapshot.Applications.Select(a => "· " + a.ProcessName)) +
                    "\n\n是否授权使用管理员权限强制结束？（会弹出 UAC 确认框）",
                    "需要管理员权限", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (elevate == DialogResult.Yes)
                {
                    (bool clean, string message) = await ElevatedProcessKiller
                        .KillWithElevationAsync(snapshot.Applications).ConfigureAwait(false);
                    Logger.WriteLine($"Manual iGPU preflight: elevated kill clean={clean}, {message}");

                    snapshot = DgpuApplicationCoordinator.Snapshot();
                    if (snapshot.IsAvailable && snapshot.Applications.Count > 0)
                    {
                        Logger.WriteLine("Manual iGPU preflight: dGPU still occupied after elevated kill: " +
                            string.Join(", ", snapshot.Applications.Select(a => a.ProcessName + "#" + a.ProcessId)));
                        try { Program.toast?.RunToast("仍有独显程序占用，切换可能失败"); }
                        catch (Exception ex) { Logger.WriteLine("Preflight toast failed: " + ex.Message); }
                    }
                }
                else
                {
                    try { Program.toast?.RunToast("部分独显程序无法关闭，切换可能失败"); }
                    catch (Exception ex) { Logger.WriteLine("Preflight toast failed: " + ex.Message); }
                }
            }

            await Task.Delay(1000).ConfigureAwait(false);   // 给驱动一点释放句柄的时间
            return true;
        }

        /// <summary>
        /// 热切换超时后的兜底询问。寄存器此时仍是核显（服务层未回滚）：
        /// [是] 立即重启生效；[否] 保持设置稍后自行重启；[取消] 回滚到切换前的模式。
        /// </summary>
        async Task OfferRebootFallbackAsync(int modeBeforeSwitch, int target)
        {
            DialogResult choice = MessageBox.Show(
                "实时切换未能在两分钟内完成，已停止切换。\n\n" +
                "核显模式支持「重启生效」：现在保持核显设置，重启后将以核显模式启动。\n\n" +
                "[是] 立即重启并生效\n[否] 保持设置，稍后自行重启\n[取消] 放弃并恢复原模式",
                "核显模式", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            // 选 [是] 的用户刚点过按钮，立刻捕获重启凭证；后面的 AppConfig.Flush()/Toast 即使
            // 超过 500ms，也不会把这次「重启生效」请求判成陈旧而丢掉。
            if (choice == DialogResult.Yes) SystemRestart.CaptureUserConfirmation();

            if (choice == DialogResult.Cancel)
            {
                if (Program.service is not null)
                    await Program.service.RollbackFailedHotSwitchAsync(modeBeforeSwitch, CancellationToken.None).ConfigureAwait(false);
                try { Program.toast?.RunToast("已恢复切换前模式"); }
                catch (Exception ex) { Logger.WriteLine("Rollback toast failed: " + ex.Message); }
                return;
            }

            AppConfig.Set("gpu_mode", target);
            AppConfig.Flush();
            Logger.WriteLine($"Reboot-to-apply accepted (restartNow={choice == DialogResult.Yes}); gpu_mode={target}");
            if (choice == DialogResult.Yes)
            {
                try { Program.toast?.RunToast("5 秒后重启以应用核显模式"); } catch { }
                // 用户刚点过 [是] → 新鲜输入放行；后台线程发起，不占用 UI 线程。
                SystemRestart.RequestRestart("iGPU reboot-to-apply", SystemRestart.RebootAfterFiveSecondsArguments);
            }
            else
            {
                try { Program.toast?.RunToast("核显模式将在下次重启后生效"); } catch { }
            }
        }

        private void ScheduleGpuEco(int eco, int delay)
        {
            if (delay <= 0)
            {
                SetGPUEco(eco);
                return;
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                SetGPUEco(eco);
            });
        }


        // ToggleXGM(bool) 已删除：华硕 XG Mobile 外置显卡坞的启停流程。
        //
        // 机械革命全系没有这个接口，方法里的每一层都是空转：
        // DeviceGet(GPUXG) 恒 -1、DeviceSet 是 `=> 0` 空实现、XGM.Init/Reset/SetFan
        // 全是空存根、"xgm_special" 这个配置键从来没被写过。
        // 唯一入口是设计器里 Visible=false 的 buttonXGM，已一并删除。
        // 留着的风险不止是没用：它会弹一个英文对话框、再 Task.Delay 15 秒锁住 GPU 面板。

        public void KillGPUApps()
        {
            if (HardwareControl.GpuControl is not null)
            {
                HardwareControl.GpuControl.KillGPUApps();
            }
        }

        public void CaptureNvBootState()
        {
            nvRestartPending = Program.acpi.IsNVidiaGPU() && Program.acpi.DeviceGet(AsusACPI.GPUEco) == 1;
        }

        public void StandardModeFix()
        {
            if (!AppConfig.IsStandardModeFix()) return;
            if (Program.acpi.DeviceGet(AsusACPI.GPUMux) == 0) return; // Ultimate mode

            Logger.WriteLine("Forcing Standard Mode on shutdown");
            Program.acpi.SetGPUEco(0);
        }

        public static string? gpuError = null;

        void CheckGpuError()
        {
            string? error = DeviceHelper.GetGpuError();

            if (gpuError != error)
            {
                gpuError = error;
                if (error != null) Logger.WriteLine(error);
                settings.VisualiseGPUMode();
            }
        }

    }
}
