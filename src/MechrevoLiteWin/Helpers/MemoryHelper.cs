using System.Runtime.InteropServices;

namespace MechrevoLite.Helpers
{
    public static class MemoryHelper
    {
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nint dwMin, nint dwMax);

        public static void TrimAfter(Task? prerequisite = null, TimeSpan? timeout = null)
        {
            Task.Run(async () =>
            {
                if (prerequisite != null)
                {
                    try
                    {
                        await prerequisite.WaitAsync(timeout ?? TimeSpan.FromSeconds(3));
                    }
                    catch { }
                }

                Trim();
            });
        }

        /// <summary>
        /// 延迟 <paramref name="delay"/> 后、且 <paramref name="stillIdle"/> 仍成立时整理一次。开机自启到托盘时
        /// 主窗从不隐藏（也就从不走 HideAll 的整理）：启动期的临时分配与只用一次的代码页一直占着工作集。
        /// </summary>
        public static void TrimLater(TimeSpan delay, Func<bool> stillIdle)
        {
            Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                try
                {
                    if (stillIdle()) Trim();
                }
                catch { /* 整理失败不影响任何功能 */ }
            });
        }

        private static void Trim()
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized);

            using var p = System.Diagnostics.Process.GetCurrentProcess();
            SetProcessWorkingSetSize(p.Handle, -1, -1);
        }
    }
}
