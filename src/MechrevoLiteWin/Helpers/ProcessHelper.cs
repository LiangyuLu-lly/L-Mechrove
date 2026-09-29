using System.Diagnostics;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MechrevoLite.Helpers
{
    public static class ProcessHelper
    {
        private const string ExitEventName = "Global\\LMechrevoApp-Exit";
        private static EventWaitHandle? exitEvent;
        private static long lastAdmin;

        private static bool? _isSystem;
        public static bool IsRunningAsSystem()
        {
            if (_isSystem.HasValue)
                return _isSystem.Value;

            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                if (identity == null)
                    _isSystem = false;
                else
                    _isSystem = string.Equals(identity.Name, @"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase);
            }

            return _isSystem.Value;
        }

        public static bool IsLegacyHandoffAction(string? action) =>
            action?.Equals("cpu", StringComparison.OrdinalIgnoreCase) == true ||
            action?.Equals("gpu", StringComparison.OrdinalIgnoreCase) == true ||
            action?.Equals("uv", StringComparison.OrdinalIgnoreCase) == true ||
            action?.Equals("services", StringComparison.OrdinalIgnoreCase) == true;

        public static bool ShouldContinueAfterExistingInstance(bool handoff, bool ownerExited) =>
            handoff && ownerExited;

        public static bool IsGpuOverclockHelperCommandLine(string? commandLine) =>
            !string.IsNullOrWhiteSpace(commandLine) &&
            commandLine.Contains("--gpu-oc-helper", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Establishes the UI owner, or performs the explicit legacy elevation handoff.
        /// A normal duplicate startup never signals or kills the current owner.
        /// </summary>
        public static bool CheckAlreadyRunning(string? action = null)
        {
            EventWaitHandle? currentEvent = CreateOrOpenExitEvent(out bool created);
            exitEvent = currentEvent;
            if (currentEvent is null)
            {
                // Preserve the old fail-open behavior when the named event cannot be created.
                Logger.WriteLine("Singleton event unavailable; continuing without duplicate-process enforcement.");
                return true;
            }

            if (!created)
            {
                bool handoff = IsLegacyHandoffAction(action);
                // The legacy event is global for compatibility with older builds.  First
                // establish whether an owner exists in this login session; an event held by
                // another user must never block or signal this user's UI.
                List<Process> owners = FindExistingUiProcesses();
                try
                {
                    if (!handoff)
                    {
                        if (owners.Count == 0)
                        {
                            Logger.WriteLine("Singleton event has no owner in this session; continuing.");
                            ReleaseExitEvent(currentEvent);
                            return true;
                        }

                        Logger.WriteLine("Another L-Mechrevo instance is already running; leaving it untouched.");
                        // A user-initiated second launch (shortcut / Start menu) brings the running
                        // instance's window up; the logon autostart of an already running app stays quiet.
                        if (!string.Equals(action, "startup", StringComparison.OrdinalIgnoreCase))
                            SingleInstanceSignal.RequestShow();
                        ReleaseExitEvent(currentEvent);
                        return false;
                    }

                    if (owners.Count == 0)
                    {
                        Logger.WriteLine("No L-Mechrevo owner exists in this session; skipping legacy handoff signal.");
                        ReleaseExitEvent(currentEvent);
                        return true;
                    }

                    try
                    {
                        currentEvent.Set();
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine("Broadcast exit failed: " + ex.Message);
                        ReleaseExitEvent(currentEvent);
                        return false;
                    }

                    bool ownerExited = WaitForExistingOwnersExit(owners, TimeSpan.FromSeconds(8));
                    if (!ShouldContinueAfterExistingInstance(handoff, ownerExited))
                    {
                        Logger.WriteLine("Legacy elevation handoff stopped because the existing UI did not exit cleanly.");
                        ReleaseExitEvent(currentEvent);
                        return false;
                    }

                    try
                    {
                        currentEvent.Reset();
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine("Resetting singleton event after handoff failed: " + ex.Message);
                        ReleaseExitEvent(currentEvent);
                        return false;
                    }
                }
                finally
                {
                    foreach (Process owner in owners) owner.Dispose();
                }
            }

            ThreadPool.RegisterWaitForSingleObject(currentEvent, (_, _) => Application.Exit(), null, Timeout.Infinite, true);
            return true;
        }

        /// <summary>Is a UI instance of L-Mechrevo already running in this login session (helpers excluded)?</summary>
        internal static bool HasExistingUiOwner()
        {
            List<Process> owners = FindExistingUiProcesses();
            try { return owners.Count > 0; }
            finally { foreach (Process owner in owners) owner.Dispose(); }
        }

        private static void ReleaseExitEvent(EventWaitHandle currentEvent)
        {
            if (ReferenceEquals(exitEvent, currentEvent)) exitEvent = null;
            currentEvent.Dispose();
        }

        private static EventWaitHandle? CreateOrOpenExitEvent(out bool created)
        {
            created = false;
            try
            {
                var sec = new EventWaitHandleSecurity();
                sec.AddAccessRule(new EventWaitHandleAccessRule(
                    new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
                    EventWaitHandleRights.Synchronize | EventWaitHandleRights.Modify,
                    AccessControlType.Allow));
                return EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, ExitEventName, out created, sec);
            }
            catch (Exception createException)
            {
                Logger.WriteLine("Creating singleton event failed: " + createException.Message);
                try { return EventWaitHandle.OpenExisting(ExitEventName); }
                catch (Exception ex)
                {
                    Logger.WriteLine("Opening singleton event failed: " + ex.Message);
                    return null;
                }
            }
        }

        private static List<Process> FindExistingUiProcesses()
        {
            using Process currentProcess = Process.GetCurrentProcess();
            int currentSessionId;
            try { currentSessionId = currentProcess.SessionId; }
            catch { return new List<Process>(); }

            Process[] processes;
            try { processes = Process.GetProcessesByName(currentProcess.ProcessName); }
            catch (Exception ex)
            {
                Logger.WriteLine("Enumerating existing instances failed: " + ex.Message);
                return new List<Process>();
            }

            var owners = new List<Process>();
            foreach (Process process in processes)
            {
                if (process.Id == currentProcess.Id || !SameSession(process, currentSessionId))
                {
                    process.Dispose();
                    continue;
                }

                if (IsGpuOverclockHelper(process))
                {
                    Logger.WriteLine($"Ignoring GPU overclock helper PID {process.Id} during singleton handoff.");
                    process.Dispose();
                    continue;
                }

                owners.Add(process);
            }

            return owners;
        }

        private static bool WaitForExistingOwnersExit(IReadOnlyList<Process> owners, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            foreach (Process owner in owners)
            {
                while (true)
                {
                    bool exited;
                    try { exited = owner.HasExited; }
                    catch (InvalidOperationException) { exited = true; }
                    catch (Exception ex)
                    {
                        Logger.WriteLine($"Reading existing instance PID {owner.Id} failed: {ex.Message}");
                        exited = false;
                    }

                    if (exited) break;
                    TimeSpan remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return false;

                    try { owner.WaitForExit((int)Math.Min(250, Math.Max(1, remaining.TotalMilliseconds))); }
                    catch (InvalidOperationException) { break; }
                    catch (Exception ex)
                    {
                        Logger.WriteLine($"Waiting for existing instance PID {owner.Id} failed: {ex.Message}");
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsGpuOverclockHelper(Process process)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {process.Id}");
                using ManagementObjectCollection results = searcher.Get();
                foreach (ManagementObject item in results)
                {
                    try { return IsGpuOverclockHelperCommandLine(item["CommandLine"] as string); }
                    finally { item.Dispose(); }
                }
            }
            catch (Exception ex)
            {
                // Unknown command lines are treated as owners; this path must never kill a process.
                Logger.WriteLine($"Reading command line for PID {process.Id} failed: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// 进程是否属于指定的登录会话。读 SessionId 可能因权限不足抛
        /// （跨会话、或进程正在退出），那种情况按「不是同一会话」处理——
        /// 宁可漏杀一个陌生会话里的进程，也不要因为读不到就把自己关掉。
        /// </summary>
        static bool SameSession(Process process, int sessionId)
        {
            try { return process.SessionId == sessionId; }
            catch (Exception ex)
            {
                Logger.WriteLine($"Can't read session of PID {process.Id}: {ex.Message}");
                return false;
            }
        }

        public static bool IsUserAdministrator()
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }

        public static void RunAsAdmin(string? param = null, bool force = false)
        {

            if (Math.Abs(DateTimeOffset.Now.ToUnixTimeMilliseconds() - lastAdmin) < 2000) return;
            lastAdmin = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            // Check if the current user is an administrator
            if (!IsUserAdministrator() || force)
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.UseShellExecute = true;
                startInfo.WorkingDirectory = Environment.CurrentDirectory;
                startInfo.FileName = Application.ExecutablePath;
                startInfo.Arguments = param;
                startInfo.Verb = "runas";
                try
                {
                    Process.Start(startInfo);
                    Application.Exit();
                }
                catch (Exception ex)
                {
                    Logger.WriteLine(ex.Message);
                }
            }
        }


        public static void KillByName(string name)
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                foreach (var process in processes)
                {
                    try
                    {
                        process.Kill();
                        Logger.WriteLine($"Stopped: {process.ProcessName}");
                    }
                    catch (Exception ex)
                    {
                        Logger.WriteLine($"Failed to stop: {process.ProcessName} {ex.Message}");
                    }
                }
            }
            finally
            {
                foreach (var p in processes) p.Dispose();
            }
        }

        public static void KillSmartDisplayControl()
        {
            KillByName("ASUSSmartDisplayControl");
        }

        public static void KillByProcess(Process process)
        {
            try
            {
                process.Kill();
                Logger.WriteLine($"Stopped: {process.ProcessName}");
            }
            catch (Exception ex)
            {
                Logger.WriteLine($"Failed to stop: {process.ProcessName} {ex.Message}");
            }
        }

        public static string RunCMD(string name, string args, string? directory = null, int timeoutMs = 0)
        {
            using var cmd = new Process();
            cmd.StartInfo.UseShellExecute = false;
            cmd.StartInfo.CreateNoWindow = true;
            cmd.StartInfo.RedirectStandardOutput = true;
            cmd.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
            cmd.StartInfo.FileName = name;
            cmd.StartInfo.Arguments = args;
            if (directory != null) cmd.StartInfo.WorkingDirectory = directory;
            cmd.Start();

            var watch = Stopwatch.StartNew();
            string result;

            if (timeoutMs > 0)
            {
                // RunCMD 的同步签名是调用方契约；超时后必须 Kill 子进程，故这里同步等待读取任务。
                // Wait 返回 true 即任务已完成，紧随其后的 .Result 不再阻塞。
                var readTask = cmd.StandardOutput.ReadToEndAsync();
                if (!readTask.Wait(timeoutMs))
                {
                    try { cmd.Kill(entireProcessTree: true); } catch { }
                    watch.Stop();
                    Logger.WriteLine(name + " " + args);
                    Logger.WriteLine($"{watch.ElapsedMilliseconds} ms: TIMEOUT after {timeoutMs} ms");
                    return string.Empty;
                }
                result = readTask.Result.Replace(Environment.NewLine, " ").Trim(' ');
            }
            else
            {
                result = cmd.StandardOutput.ReadToEnd().Replace(Environment.NewLine, " ").Trim(' ');
            }

            watch.Stop();
            Logger.WriteLine(name + " " + args);
            Logger.WriteLine(watch.ElapsedMilliseconds + " ms: " + result);
            cmd.WaitForExit();

            return result;
        }

        public static void SetPriority(ProcessPriorityClass priorityClass = ProcessPriorityClass.Normal)
        {
            try
            {
                using (Process p = Process.GetCurrentProcess())
                    p.PriorityClass = priorityClass;
            }
            catch (Exception ex)
            {
                Logger.WriteLine(ex.ToString());
            }
        }


    }
}
