using System.Globalization;
using Microsoft.Win32;

namespace MechrevoLite.Hardware;

/// <summary>
/// Where the vendor GCU MQTT broker listens. Every decompiled console/service generation uses 13688;
/// the GamingCenterU legacy bridge (GTX 10/16, RTX 20) has encrypted method bodies, so the installer
/// measures the port that bridge actually listens on and records it in
/// <c>HKLM\SOFTWARE\L-Mechrevo\GcuMqttPort</c>. The app connects to the recorded port, 13688 otherwise.
/// Read once per process: the installer only changes it while the app is stopped.
/// </summary>
internal static class GcuEndpoint
{
    internal const string Host = "127.0.0.1";
    internal const int DefaultPort = 13688;
    internal const string RegistryKeyPath = @"SOFTWARE\L-Mechrevo";
    internal const string PortValueName = "GcuMqttPort";

    static readonly Lazy<int> _port = new(ReadPort, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static int Port => _port.Value;

    /// <summary>Pure: a recorded value is used only when it is a real TCP port; anything else means 13688.</summary>
    internal static int ParsePort(object? value)
    {
        long port = value switch
        {
            int i => i,
            long l => l,
            string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed) => parsed,
            _ => 0,
        };
        return port is > 0 and <= 65535 ? (int)port : DefaultPort;
    }

    static int ReadPort()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(RegistryKeyPath);
            int port = ParsePort(key?.GetValue(PortValueName));
            if (port != DefaultPort) Logger.WriteLine("GCU MQTT port from the installer record: " + port);
            return port;
        }
        catch (Exception ex)
        {
            Logger.WriteLine("Cannot read the GCU MQTT port record: " + ex.Message);
            return DefaultPort;
        }
    }
}
