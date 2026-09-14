using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

public class ProcessInstancePolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("startup", false)]
    [InlineData("cpu", true)]
    [InlineData("gpu", true)]
    [InlineData("uv", true)]
    [InlineData("services", true)]
    [InlineData("--gpu-oc-helper", false)]
    public void LegacyElevationActionsAreTheOnlyHandoffRoles(string? action, bool expected) =>
        Assert.Equal(expected, ProcessHelper.IsLegacyHandoffAction(action));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void ExistingInstanceCanContinueOnlyAfterAConfirmedHandoffExit(
        bool handoff, bool ownerExited, bool expected) =>
        Assert.Equal(expected,
            ProcessHelper.ShouldContinueAfterExistingInstance(handoff, ownerExited));

    [Theory]
    [InlineData("L-Mechrevo.exe --gpu-oc-helper pipe token sid", true)]
    [InlineData("L-Mechrevo.exe cpu", false)]
    [InlineData("L-Mechrevo.exe", false)]
    [InlineData(null, false)]
    public void GpuOverclockHelperCommandLineIsNotAnApplicationOwner(
        string? commandLine, bool expected) =>
        Assert.Equal(expected, ProcessHelper.IsGpuOverclockHelperCommandLine(commandLine));
}
