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
        internal static GcuConnectionState Resolve(
            bool hardwarePresent, bool connected, bool reconnecting, bool everConnected)
        {
            if (!hardwarePresent) return GcuConnectionState.Unknown;
            if (connected) return GcuConnectionState.Connected;
            if (reconnecting && !everConnected) return GcuConnectionState.Connecting;
            return GcuConnectionState.Disconnected;
        }

        /// <summary>状态 → 前景色 / 文本 / tooltip。文本含「●」圆点作颜色之外的形状信号。</summary>
        internal static (Color Fore, string Text, string Tooltip) Describe(GcuConnectionState state) => state switch
        {
            GcuConnectionState.Connected => (
                UiVisualStyle.Ok,
                "● GCU 已连接",
                "GCU 服务连接正常（GCUBridge，MQTT 127.0.0.1:13688）"),
            GcuConnectionState.Connecting => (
                UiVisualStyle.Warn,
                "● GCU 连接中",
                "正在连接 GCU 服务（127.0.0.1:13688）——服务通常在登录后数秒内就绪，应用会自动重试"),
            GcuConnectionState.Disconnected => (
                UiVisualStyle.Danger,
                "● GCU 未连接",
                "GCU 服务未运行或连接失败（127.0.0.1:13688）；应用正在自动重连，恢复后此指示会变绿"),
            _ => (
                UiVisualStyle.Muted,
                "● GCU 状态未知",
                "硬件后端尚未初始化"),
        };
    }
}
