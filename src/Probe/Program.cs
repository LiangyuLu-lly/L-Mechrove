using Probe;

var cmd = args.Length > 0 ? args[0] : "handshake";
switch (cmd)
{
    case "handshake": await MqttProbe.Handshake(); break;
    case "sniff": await MqttProbe.Sniff(int.TryParse(args.ElementAtOrDefault(1), out var s) ? s : 60); break;
    case "ec": EcProbe.Run(args.Skip(1).ToArray()); break;
    case "selftest": await MqttProbe.SelfTest(int.TryParse(args.ElementAtOrDefault(1), out var sr) ? sr : 3); break;
    case "modetest": await MqttProbe.ModeTest(); break;
    case "send": await MqttProbe.Send(args.ElementAtOrDefault(1) ?? "Setting/Control", args.ElementAtOrDefault(2) ?? "{}", int.TryParse(args.ElementAtOrDefault(3), out var waitSec) ? waitSec : 8); break;
    case "golden": await GoldenCapture.Run(int.TryParse(args.ElementAtOrDefault(1), out var g) ? g : 120); break;
    case "nvoc": NvOcProbe.Read(); break;
    default: Console.WriteLine("usage: probe <handshake|sniff|ec|golden [seconds]>"); break;
}
