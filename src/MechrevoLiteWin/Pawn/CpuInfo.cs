using MechrevoLite;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;

namespace PawnIO
{
    public static class CpuInfo
    {
        public static readonly bool IsAMD = DetectAMD();

        private static bool DetectAMD()
        {
            if (!X86Base.IsSupported) return false;
            var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);

            Span<uint> regs = stackalloc uint[] { (uint)ebx, (uint)edx, (uint)ecx };
            return MemoryMarshal.Cast<uint, byte>(regs).SequenceEqual("AuthenticAMD"u8);
        }


        private const string CpuRegKey = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        private static readonly Lazy<(string Name, string Caption)> _data =
            new Lazy<(string, string)>(Load, LazyThreadSafetyMode.ExecutionAndPublication);

        public static string Name    => _data.Value.Name;
        public static string Caption => _data.Value.Caption;

        // CPU undervolting / temperature limits.
        // Defaults match the original RyzenControl values; can be overridden via AppConfig.
        public static int MinCPUUV   => AppConfig.Get("min_uv",      -40);
        public static int MaxCPUUV   => AppConfig.Get("max_uv",        0);
        public static int MinIGPUUV  => AppConfig.Get("min_igpu_uv", -30);
        public static int MaxIGPUUV  => AppConfig.Get("max_igpu_uv",   0);
        public static int MinTemp    => AppConfig.Get("min_temp",     75);
        public static int DefaultTemp=> AppConfig.Get("max_temp",     96);

        public static bool IsSupportedUV()
            => RyzenSmuService.CurveCommand(DetectedAmdCodeName) is not null;

        internal static CpuCodeName DetectedAmdCodeName
        {
            get
            {
                if (!IsAMD || !X86Base.IsSupported) return CpuCodeName.Undefined;
                var (eax, _, _, _) = X86Base.CpuId(1, 0);
                int family = (eax >> 8) & 0xF;
                if (family == 0xF) family += (eax >> 20) & 0xFF;
                int model = ((eax >> 4) & 0xF) | ((eax >> 12) & 0xF0);
                int package = (X86Base.CpuId(unchecked((int)0x80000001), 0).Ebx >> 28) & 0xF;
                return ((family << 8) | model) switch
                {
                    0x1701 => package == 3 ? CpuCodeName.Naples : package == 7 ? CpuCodeName.Threadripper : CpuCodeName.SummitRidge,
                    0x1708 => package is 3 or 7 ? CpuCodeName.Colfax : CpuCodeName.PinnacleRidge,
                    0x1711 => CpuCodeName.RavenRidge,
                    0x1718 => package == 7 ? CpuCodeName.RavenRidge2 : CpuCodeName.Picasso,
                    0x1720 => CpuCodeName.Dali, 0x1731 => package == 7 ? CpuCodeName.CastlePeak : CpuCodeName.Rome,
                    0x1750 => CpuCodeName.FireFlight, 0x1771 => CpuCodeName.Matisse,
                    0x1798 => CpuCodeName.Mero, 0x17A0 => CpuCodeName.Mendocino,
                    0x1900 or 0x1901 => CpuCodeName.Milan, 0x1908 => CpuCodeName.Chagall,
                    0x1911 => CpuCodeName.Genoa, 0x1918 => CpuCodeName.StormPeak,
                    0x1920 or 0x1921 => CpuCodeName.Vermeer, 0x19A0 => CpuCodeName.Bergamo,
                    0x1760 => CpuCodeName.Renoir, 0x1768 => CpuCodeName.Lucienne,
                    0x1790 or 0x1791 => CpuCodeName.Vangogh, 0x1944 => CpuCodeName.Rembrandt,
                    0x1950 => CpuCodeName.Cezanne,
                    0x1961 => package == 1 ? CpuCodeName.DragonRange : CpuCodeName.Raphael,
                    0x1974 or 0x1975 => CpuCodeName.Phoenix, 0x1978 => CpuCodeName.Phoenix2,
                    0x197C => CpuCodeName.HawkPoint, 0x1A08 => CpuCodeName.ShimadaPeak,
                    0x1A02 => CpuCodeName.Turin, 0x1A10 => CpuCodeName.TurinD,
                    0x1A20 or 0x1A24 => CpuCodeName.StrixPoint, 0x1A44 => CpuCodeName.GraniteRidge,
                    0x1A60 => CpuCodeName.KrackanPoint, 0x1A68 => CpuCodeName.KrackanPoint2,
                    0x1A70 => CpuCodeName.StrixHalo, _ => CpuCodeName.Undefined,
                };
            }
        }

        public static bool IsSupportedUViGPU() => Name.Contains("6900H");

        private static (string Name, string Caption) Load()
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(CpuRegKey);
                if (key is not null)
                {
                    var name = key.GetValue("ProcessorNameString")?.ToString()?.Trim() ?? string.Empty;
                    var caption = key.GetValue("Identifier")?.ToString()?.Trim() ?? string.Empty;
                    return (name, caption);
                }
            }
            catch (Exception ex)
            {
                Logger.WriteLine("CPU registry read failed: " + CpuRegKey + " " + ex.GetType().Name + " " + ex.Message);
            }

            return (string.Empty, string.Empty);
        }
    }
}
