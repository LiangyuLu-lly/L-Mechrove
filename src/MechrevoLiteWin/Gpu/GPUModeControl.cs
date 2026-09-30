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
                // 与主界面、托盘同一个布局判定（代际 × 服务档位 × 能力位，§7）。
                GpuRowLayout layout = Program.hw.GpuRowLayout;
                bool ecoSupported = GpuRowLayouts.HasIgpuSegment(layout);
                settings.ApplyGpuRowLayout(layout);
                settings.RefreshDeviceCapabilities();
                if (!ecoSupported && AppConfig.Is("gpu_auto"))
                {
                    AppConfig.Set("gpu_auto", 0);
                    AppConfig.Set("gpu_mode", MechrevoService.GpuStandard);
                }
                if (layout == GpuRowLayout.Hidden)
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



        // SetGPUMode(int, int) 已删除（beta21）：没有调用方的旧切换入口。它的机械革命分支在非热切换机型上
        // 对「集显」发 RB_ON、超时后让用户「重启生效」——而 RB 寄存器重启后并不生效（2026-09-10 实测）；
        // ASUS 分支读的是恒 -1 的 ACPI 设备码。界面切换只走 SettingsForm.SwitchGpuModeFromUi。

        /// <summary>
        /// 手动显卡切换在途（热切换最长约两分钟）：自动档流程见到它必须让位，否则会用 RB_AUTO
        /// 取消用户刚点的切换。返回的租约 Dispose 时清除。
        /// </summary>
        internal static IDisposable BeginManualSwitch()
        {
            Interlocked.Exchange(ref _manualGpuSwitchInFlight, 1);
            return new ManualSwitchLease();
        }

        sealed class ManualSwitchLease : IDisposable
        {
            int _disposed;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Exchange(ref _manualGpuSwitchInFlight, 0);
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
                // ASUS 残留：独显直连时自动档不动 MUX（机械革命上 DeviceGet 恒为 -1，到不了这里）。
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
