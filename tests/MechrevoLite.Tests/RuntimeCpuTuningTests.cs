using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using MechrevoLite.Hardware;
using PawnIO;

namespace MechrevoLite.Tests;

public sealed class RuntimeCpuTuningTests
{
    sealed class MailboxRegisters
    {
        internal readonly Dictionary<int, uint> Values = new() { [0] = 0x00012328, [2] = 0x00014524 };
        internal readonly List<(int Domain, byte Command, uint Data)> Commands = new();
        internal uint Capabilities = 0x540;
        internal bool IgnoreWrites, Busy, DenyWrite;
        internal byte Rejection;
        internal byte WriteRejection;
        internal uint? WrittenValueOverride;
        internal ulong Response;

        internal bool Execute(string function, ulong[]? input, ulong[]? output)
        {
            Assert.Equal(0x150UL, input![0]);
            if (function == "ioctl_read_msr") { output![0] = Busy ? 1UL << 63 : Response; return true; }
            Assert.Equal("ioctl_write_msr", function);
            if (DenyWrite) return false;
            ulong request = input[1];
            Assert.NotEqual(0UL, request & (1UL << 63));
            int domain = (int)(request >> 40) & 0xFF;
            byte command = (byte)(request >> 32);
            uint data = (uint)request;
            Commands.Add((domain, command, data));
            if (Rejection != 0) { Response = (ulong)Rejection << 32; return true; }
            if (command == 0x11 && WriteRejection != 0) { Response = (ulong)WriteRejection << 32; return true; }
            if (command == 0x11 && !IgnoreWrites) Values[domain] = WrittenValueOverride ?? data;
            Response = command == 1 ? Capabilities : Values[domain];
            return true;
        }
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(-25, 0xFCC00000u)]
    [InlineData(-50, 0xF9A00000u)]
    [InlineData(-150, 0xECC00000u)]
    public void IntelOffset_UsesSignedFixedPoint(int mv, uint encoded)
    {
        Assert.Equal(encoded, IntelOcMailbox.EncodeOffset(mv));
        Assert.InRange(Math.Abs(IntelOcMailbox.DecodeOffset(encoded) - mv), 0m, 0.49m);
    }

    [Theory]
    [InlineData(-151)]
    [InlineData(1)]
    public void IntelOffset_RejectsOutsideEnvelope(int mv)
        => Assert.Throws<ArgumentOutOfRangeException>(() => IntelOcMailbox.EncodeOffset(mv));

    [Fact]
    public void IntelProbe_IssuesOnlyQueries()
    {
        var registers = new MailboxRegisters();
        using var mailbox = new IntelOcMailbox(registers.Execute);
        Assert.True(mailbox.Probe(0).VoltageSupported);
        Assert.Equal(new byte[] { 1, 0x10 }, registers.Commands.Select(c => c.Command));
        Assert.Equal(0x00012328u, registers.Values[0]);
    }

    [Fact]
    public void IntelApply_PreservesOtherVfFields_AndRestoresCapturedBaseline()
    {
        var registers = new MailboxRegisters();
        using var mailbox = new IntelOcMailbox(registers.Execute);
        var original = mailbox.Probe(0);
        var changed = mailbox.Apply(0, -25);
        Assert.Equal(IntelOcMailbox.EncodeOffset(-25) | original.Value, registers.Values[0]);
        Assert.Equal(registers.Values[0], changed.Value);
        mailbox.Apply(0, ratio: 45);
        Assert.Equal(45u, registers.Values[0] & 0xFF);
        Assert.Equal(changed.Value & ~0xFFu, registers.Values[0] & ~0xFFu);
        mailbox.Restore(0);
        Assert.Equal(original.Value, registers.Values[0]);
    }

    [Fact]
    public void IntelApply_IgnoredWriteIsFailure()
    {
        var registers = new MailboxRegisters { IgnoreWrites = true };
        using var mailbox = new IntelOcMailbox(registers.Execute);
        var error = Assert.Throws<InvalidOperationException>(() => mailbox.Apply(0, -25));
        Assert.Contains("readback differs", error.Message);
        Assert.Contains("Previous value unchanged", error.Message);
        Assert.Equal(0x00012328u, registers.Values[0]);
    }

    [Fact]
    public void IntelApply_RejectedUnchangedValueDoesNotIssueRestoreWrite()
    {
        var registers = new MailboxRegisters { WriteRejection = 0x13 };
        using var mailbox = new IntelOcMailbox(registers.Execute);
        var error = Assert.Throws<InvalidOperationException>(() => mailbox.Apply(0, -5));
        Assert.True(error.Data.Contains("MailboxWriteRejected"));
        Assert.True(error.Data.Contains("PreviousValueUnchanged"));
        Assert.DoesNotContain("Restore failed", error.Message);
        Assert.Single(registers.Commands, c => c.Command == 0x11);
        Assert.Equal(0x00012328u, registers.Values[0]);
    }

    [Fact]
    public void IntelApply_PartialWriteAndFailedRestoreAreReported()
    {
        var registers = new MailboxRegisters { WrittenValueOverride = IntelOcMailbox.EncodeOffset(-20) | 0x12328 };
        using var mailbox = new IntelOcMailbox(registers.Execute);
        var error = Assert.Throws<InvalidOperationException>(() => mailbox.Apply(0, -25));
        Assert.Contains("Restore failed", error.Message);
        Assert.NotEqual(0x00012328u, registers.Values[0]);
    }

    [Fact]
    public void IntelApply_LockedCapabilityNeverSendsWriteCommand()
    {
        var registers = new MailboxRegisters { Capabilities = 0 };
        using var mailbox = new IntelOcMailbox(registers.Execute);
        Assert.Throws<InvalidOperationException>(() => mailbox.Apply(0, -25));
        Assert.DoesNotContain(registers.Commands, c => c.Command == 0x11);
    }

    [Fact]
    public void IntelApply_RatioPastReportedMaximumNeverSendsWriteCommand()
    {
        var registers = new MailboxRegisters();
        using var mailbox = new IntelOcMailbox(registers.Execute);
        Assert.Throws<ArgumentOutOfRangeException>(() => mailbox.Apply(0, ratio: 65));
        Assert.DoesNotContain(registers.Commands, c => c.Command == 0x11);
    }

    [Fact]
    public void IntelMailbox_BusyFailsBeforeSendingCommand()
    {
        var registers = new MailboxRegisters { Busy = true };
        using var mailbox = new IntelOcMailbox(registers.Execute);
        Assert.Throws<TimeoutException>(() => mailbox.Probe(0));
        Assert.Empty(registers.Commands);
    }

    [Fact]
    public void IntelMailbox_CompletionErrorIsNotDecodedAsZeroVoltage()
    {
        var registers = new MailboxRegisters { Rejection = 0xFD };
        using var mailbox = new IntelOcMailbox(registers.Execute);
        Assert.Contains("0xFD", Assert.Throws<InvalidOperationException>(() => mailbox.Probe(0)).Message);
    }

    [Fact]
    public void IntelMailbox_DriverDenialIsFailure()
    {
        var registers = new MailboxRegisters { DenyWrite = true };
        using var mailbox = new IntelOcMailbox(registers.Execute);
        Assert.Throws<IOException>(() => mailbox.Probe(0));
    }

    [Theory]
    [InlineData(CpuCodeName.Renoir, false, 0x55)]
    [InlineData(CpuCodeName.Cezanne, false, 0x55)]
    [InlineData(CpuCodeName.Rembrandt, false, 0x4C)]
    [InlineData(CpuCodeName.Phoenix2, false, 0x4C)]
    [InlineData(CpuCodeName.StrixPoint, false, 0x4C)]
    [InlineData(CpuCodeName.StrixHalo, false, 0x4C)]
    [InlineData(CpuCodeName.DragonRange, true, 0x07)]
    [InlineData(CpuCodeName.GraniteRidge, true, 0x07)]
    [InlineData(CpuCodeName.Vermeer, true, 0x0B)]
    public void AmdCurve_RoutesActualCodeName(CpuCodeName cpu, bool psmu, int command)
        => Assert.Equal((psmu, (uint)command), RyzenSmuService.CurveCommand(cpu)!.Value);

    [Theory]
    [InlineData(CpuCodeName.Undefined)]
    [InlineData(CpuCodeName.Mendocino)]
    [InlineData(CpuCodeName.FireFlight)]
    [InlineData(CpuCodeName.RavenRidge)]
    [InlineData(CpuCodeName.ShimadaPeak)]
    [InlineData(CpuCodeName.KrackanPoint2)]
    public void AmdUnknownCurve_NeverGuessesByBroadFamily(CpuCodeName cpu)
        => Assert.Null(RyzenSmuService.CurveCommand(cpu));

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(-30, 0xFFFE2u)]
    [InlineData(-40, 0xFFFD8u)]
    public void AmdCurve_ZeroClearsTheEntireEncodedField(int points, uint expected)
        => Assert.Equal(expected, RyzenSmuService.EncodeCurve(points));

    [Fact]
    public void AmdClock_UnsupportedModernFamiliesAreNotOffered()
    {
        Assert.True(RyzenSmuService.SupportsClock(CpuCodeName.Cezanne));
        Assert.True(RyzenSmuService.SupportsClock(CpuCodeName.Rembrandt));
        Assert.True(RyzenSmuService.SupportsClock(CpuCodeName.DragonRange));
        Assert.False(RyzenSmuService.SupportsClock(CpuCodeName.ShimadaPeak));
        Assert.False(RyzenSmuService.SupportsClock(CpuCodeName.StrixPoint));
    }

    sealed class SmuRegisters(uint commandAddress, uint responseAddress, uint argumentAddress)
    {
        readonly Dictionary<uint, uint> _values = new() { [responseAddress] = 1 };
        internal readonly List<(uint Command, uint Value)> Commands = new();
        internal Func<uint, SmuStatus> Status = _ => SmuStatus.OK;
        internal bool Execute(string function, ulong[]? input, ulong[]? output)
        {
            uint address = (uint)input![0];
            if (function == "ioctl_read_smu_register")
            {
                output![0] = _values.GetValueOrDefault(address);
                return true;
            }
            Assert.Equal("ioctl_write_smu_register", function);
            uint value = (uint)input[1];
            _values[address] = value;
            if (address == commandAddress)
            {
                Commands.Add((value, _values.GetValueOrDefault(argumentAddress)));
                _values[responseAddress] = (uint)Status(value);
            }
            return true;
        }
    }

    [Theory]
    [InlineData(CpuCodeName.SummitRidge, 0x03B10528u, 0x03B10564u, 0x03B10598u, 0x23u, 0x39u, 0x24u)]
    [InlineData(CpuCodeName.Vermeer, 0x03B10530u, 0x03B1057Cu, 0x03B109C4u, 0x24u, 0x26u, 0x25u)]
    [InlineData(CpuCodeName.DragonRange, 0x03B10524u, 0x03B10570u, 0x03B10A40u, 0x5Du, 0x5Fu, 0x5Eu)]
    [InlineData(CpuCodeName.Phoenix, 0x03B10A20u, 0x03B10A80u, 0x03B10A88u, 0x17u, 0x19u, 0x18u)]
    public void AmdClock_UsesNativeMailboxSequenceAndRestoresAutomaticFrequency(
        CpuCodeName cpu, uint cmd, uint rsp, uint arg, uint enable, uint clock, uint disable)
    {
        var registers = new SmuRegisters(cmd, rsp, arg);
        using var smu = new RyzenSmuService(cpu, registers.Execute);
        Assert.Equal(SmuStatus.OK, smu.SetOcClock(4200));
        Assert.Equal(SmuStatus.OK, smu.DisableOc());
        Assert.Equal(new[] { (enable, 0u), (clock, 4200u), (disable, 0u) }, registers.Commands);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AmdClock_RecoversAfterRejectedFrequencyAndReportsFailedRecovery(bool recoverable)
    {
        var registers = new SmuRegisters(0x03B10524, 0x03B10570, 0x03B10A40)
        {
            Status = command => command == 0x5F || command == 0x5E && !recoverable ? SmuStatus.CmdRejectedPrereq : SmuStatus.OK,
        };
        using var smu = new RyzenSmuService(CpuCodeName.DragonRange, registers.Execute);
        if (recoverable) Assert.Equal(SmuStatus.CmdRejectedPrereq, smu.SetOcClock(4200));
        else Assert.Contains("disable OC failed", Assert.Throws<InvalidOperationException>(() => smu.SetOcClock(4200)).Message);
        Assert.Equal(new uint[] { 0x5D, 0x5F, 0x5E }, registers.Commands.Select(c => c.Command));
    }

    [Fact]
    public void AmdClock_UnknownRouteDoesNotWriteRegisters()
    {
        using var smu = new RyzenSmuService(CpuCodeName.ShimadaPeak, (_, _, _) => throw new Exception("Unexpected native access"));
        Assert.Equal(SmuStatus.UnknownCmd, smu.SetOcClock(4200));
    }

    [Theory]
    [InlineData(0u, 1, false)]
    [InlineData(4u, 1, false)]
    [InlineData(8u, 1, true)]
    [InlineData(16u, 1, false)]
    public void PawnOutput_TruncationCannotConfirmAZeroValue(uint bytes, int cells, bool expected)
        => Assert.Equal(expected, PawnIOWrapper.HasCompleteOutput(bytes, cells));

    [Theory]
    [InlineData("LMechrevo.IntelMSR.bin", "D6ED85D65AB17A22F813EF98207D6D537155EE2DED5976A21CB48413C9B92E5F")]
    [InlineData("LMechrevo.RyzenSMU.bin", "301D9CA397108E09F31BFBD5AC4C9BB4F352A5DE68532C32DB3BA7DDCDE93450")]
    public void SignedModules_MatchPinnedRelease(string name, string sha256)
    {
        using var resource = typeof(Program).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(resource);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(resource)));
    }

    [Fact]
    public void InvalidSettings_AreRejectedBeforeDriverAccess()
    {
        using var tuning = new RuntimeCpuTuning();
        Assert.False(tuning.Apply("intel-core", -25).Success);
        Assert.False(tuning.Apply("amd-co", -100).Success);
        Assert.False(tuning.HasChanges);
    }

    [Fact]
    public void DriverMissingForm_DisablesWrites()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var form = new CpuTuningForm(new("CPU fixture", "CpuTuneDriverMissing", "", Array.Empty<CpuTuningOption>()));
                var controls = form.Controls.Cast<Control>().SelectMany(Descendants).ToArray();
                Assert.False(controls.Single(c => c.Name == "cpuTuneValue").Enabled);
                Assert.Contains("PawnIO", controls.Single(c => c.Name == "cpuTuneStatus").Text);
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        Assert.Null(error);
    }

    static IEnumerable<Control> Descendants(Control control)
    {
        yield return control;
        foreach (Control child in control.Controls)
            foreach (Control nested in Descendants(child)) yield return nested;
    }
}
