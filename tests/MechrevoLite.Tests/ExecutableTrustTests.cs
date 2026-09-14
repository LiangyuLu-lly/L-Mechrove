using System.Security.AccessControl;
using MechrevoLite.Helpers;

namespace MechrevoLite.Tests;

/// <summary>
/// SYSTEM + 最高权限开机任务的前置门禁。任务里存的是磁盘上的镜像路径，
/// 如果该位置普通用户可写，替换 EXE 就等于无 UAC 的持久化 SYSTEM 提权。
/// </summary>
public class ExecutableTrustTests
{
    const string Administrators = "S-1-5-32-544";
    const string LocalSystem = "S-1-5-18";
    const string TrustedInstaller =
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    const string CreatorOwner = "S-1-3-0";
    const string Users = "S-1-5-32-545";
    const string SomeUser = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    static ExecutableTrust.AccessRuleSnapshot Allow(
        string sid, FileSystemRights rights, bool inheritOnly = false) =>
        new(sid, rights, AccessControlType.Allow, inheritOnly);

    static ExecutableTrust.AccessRuleSnapshot Deny(string sid, FileSystemRights rights) =>
        new(sid, rights, AccessControlType.Deny, false);

    [Theory]
    [InlineData(Administrators)]
    [InlineData(LocalSystem)]
    [InlineData(TrustedInstaller)]
    public void WellKnownAdministrativeIdentitiesAreTrusted(string sid) =>
        Assert.True(ExecutableTrust.IsTrustedIdentity(sid));

    [Theory]
    [InlineData(Users)]
    [InlineData(SomeUser)]
    [InlineData(CreatorOwner)]
    [InlineData("")]
    [InlineData(null)]
    public void EveryOtherIdentityIsUntrusted(string? sid) =>
        Assert.False(ExecutableTrust.IsTrustedIdentity(sid));

    /// <summary>
    /// 典型的受保护布局：管理员和 SYSTEM 可写，普通用户只读，
    /// CREATOR OWNER 的完全控制是 inherit-only（只对新建子对象生效）。
    /// </summary>
    [Fact]
    public void ProgramFilesLikeLayoutIsAccepted()
    {
        var rules = new[]
        {
            Allow(Administrators, FileSystemRights.FullControl),
            Allow(LocalSystem, FileSystemRights.FullControl),
            Allow(TrustedInstaller, FileSystemRights.FullControl),
            Allow(Users, FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory),
            Allow(CreatorOwner, FileSystemRights.FullControl, inheritOnly: true),
        };

        Assert.False(ExecutableTrust.GrantsImageReplacementToNonAdministrators(rules));
    }

    /// <summary>
    /// 便携式解压到用户目录：当前用户拥有完全控制，必须判定为不受保护。
    /// </summary>
    [Fact]
    public void UserWritableLayoutIsRejected()
    {
        var rules = new[]
        {
            Allow(Administrators, FileSystemRights.FullControl),
            Allow(LocalSystem, FileSystemRights.FullControl),
            Allow(SomeUser, FileSystemRights.FullControl),
        };

        Assert.True(ExecutableTrust.GrantsImageReplacementToNonAdministrators(rules));
    }

    /// <summary>
    /// 逐个权限位验证：只要能覆写内容、新建、删除、改 ACL 或夺取所有权，就足以替换镜像。
    /// </summary>
    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.Modify)]
    [InlineData(FileSystemRights.Write)]
    public void AnyImageReplacingRightGrantedToAUserIsRejected(FileSystemRights rights) =>
        Assert.True(ExecutableTrust.GrantsImageReplacementToNonAdministrators(
            [Allow(SomeUser, rights)]));

    // 注意：ListDirectory 与 ReadData 是同一个位值，Traverse 与 ExecuteFile 也是，不能重复列。
    [Theory]
    [InlineData(FileSystemRights.ReadData)]
    [InlineData(FileSystemRights.ReadAndExecute)]
    [InlineData(FileSystemRights.ReadPermissions)]
    [InlineData(FileSystemRights.ReadAttributes)]
    [InlineData(FileSystemRights.ReadExtendedAttributes)]
    public void ReadOnlyRightsGrantedToAUserAreFine(FileSystemRights rights) =>
        Assert.False(ExecutableTrust.GrantsImageReplacementToNonAdministrators(
            [Allow(SomeUser, rights)]));

    /// <summary>Deny 项不授予任何权限，不能被当成风险。</summary>
    [Fact]
    public void DenyRulesAreNotTreatedAsGrants() =>
        Assert.False(ExecutableTrust.GrantsImageReplacementToNonAdministrators(
            [Deny(SomeUser, FileSystemRights.FullControl)]));

    /// <summary>
    /// inherit-only 的 ACE 不授予对当前对象的访问权。忽略它是 Program Files
    /// 能通过检查的关键——否则那条 CREATOR OWNER 会让所有位置都被判成不安全。
    /// </summary>
    [Fact]
    public void InheritOnlyGrantsDoNotApplyToTheObjectItself()
    {
        Assert.False(ExecutableTrust.GrantsImageReplacementToNonAdministrators(
            [Allow(SomeUser, FileSystemRights.FullControl, inheritOnly: true)]));
        Assert.True(ExecutableTrust.GrantsImageReplacementToNonAdministrators(
            [Allow(SomeUser, FileSystemRights.FullControl, inheritOnly: false)]));
    }

    [Fact]
    public void EmptyRuleSetIsNotConsideredWritable() =>
        Assert.False(ExecutableTrust.GrantsImageReplacementToNonAdministrators([]));

    // ---- 端到端：真实路径 ----

    /// <summary>
    /// 用户临时目录必然是当前用户可写的，必须被拒绝并给出可读的原因。
    /// </summary>
    [Fact]
    public void TemporaryDirectoryIsReportedAsUnprotected()
    {
        string directory = Path.Combine(Path.GetTempPath(), "L-Mechrevo-trust-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            bool protectedLocation = ExecutableTrust.IsProtectedLocation(
                Path.Combine(directory, "L-Mechrevo.exe"), out string reason);

            Assert.False(protectedLocation);
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>无法读取权限时必须判定为不受保护，而不是放行。</summary>
    [Fact]
    public void UnreadableLocationFailsClosed()
    {
        bool protectedLocation = ExecutableTrust.IsProtectedLocation(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing", "L-Mechrevo.exe"),
            out string reason);

        Assert.False(protectedLocation);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
