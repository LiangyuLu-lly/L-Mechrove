using MechrevoLite.Properties;

namespace MechrevoLite.UI
{
    /// <summary>GCU 连接状态指示条的离散状态。</summary>
    internal enum GcuConnectionState
    {
        Unknown,
        Connected,
        Connecting,
        Disconnected,
    }

    /// <summary>
    /// GCU 连接状态 → （前景色，文本，tooltip）的纯映射。真值来源：
    /// <see cref="MechrevoLite.Hardware.MechrevoHw"/> 的 IsConnected / IsReconnecting /
    /// ConnectionGeneration（MechrevoHw.cs:938 / 437 / 436）。
    /// 颜色只是强调：每个状态都带互不相同的文本标签（无障碍，非仅色差）。
    /// </summary>
    internal static class GcuConnectionStatus
    {
        /// <summary>
        /// 从硬件快照解析状态。everConnected = ConnectionGeneration &gt; 0（本进程曾连上过）。
        /// 曾连上后断线（即使重连循环在飞）一律报「未连接」——服务停了就是停了，
        /// 不能让用户误以为马上会好；只有首连尚未成功（服务开机晚于登录）才报「连接中」。
        /// </summary>
        internal const long FirstConnectGiveUpMs = 20_000;

        internal static GcuConnectionState Resolve(
            bool hardwarePresent, bool connected, bool reconnecting, bool everConnected,
            long reconnectAgeMs = 0)
        {
            if (!hardwarePresent) return GcuConnectionState.Unknown;
            if (connected) return GcuConnectionState.Connected;
            // Gen0 retry stays Connecting even past FirstConnectGiveUpMs; that age is not Unparsable.
            if (reconnecting && !everConnected)
                return GcuConnectionState.Connecting;
            return GcuConnectionState.Disconnected;
        }

        /// <summary>状态 → 前景色 / 文本 / tooltip。文本含「●」圆点作颜色之外的形状信号。</summary>
        internal static (Color Fore, string Text, string Tooltip) Describe(GcuConnectionState state) => state switch
        {
            GcuConnectionState.Connected => (
                UiVisualStyle.Ok,
                Strings.GcuConnected,
                Strings.GcuConnectedTip),
            GcuConnectionState.Connecting => (
                UiVisualStyle.Warn,
                Strings.GcuConnecting,
                Strings.GcuConnectingTip),
            GcuConnectionState.Disconnected => (
                UiVisualStyle.Danger,
                Strings.GcuDisconnected,
                Strings.GcuDisconnectedTip),
            _ => (
                UiVisualStyle.Muted,
                Strings.GcuUnknown,
                Strings.GcuUnknownTip),
        };
    }
}
