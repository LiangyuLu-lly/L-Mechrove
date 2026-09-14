using System.Security.AccessControl;
using System.Security.Principal;

namespace MechrevoLite.Helpers;

/// <summary>
/// 判断程序镜像是否位于「只有管理员 / SYSTEM 可写」的位置。
///
/// 为什么需要它：注册以 SYSTEM + 最高权限运行的开机计划任务时，任务里存的是磁盘上的
/// 镜像路径。如果该路径所在目录普通用户可写（绿色解压到桌面/下载目录是这类工具的常态），
/// 那么任何以当前用户身份运行的中完整性进程只要替换掉这个 EXE，下次开机 Task Scheduler
/// 就会以 SYSTEM 执行被替换的镜像 —— 无需任何 UAC 交互的本地提权，而且是持久化的。
///
/// 判定策略刻意保守：只有在**确认**位置受保护时才返回 true；读取 ACL 失败、
/// 存在授予非管理员写权限的 ACE，都返回 false。宁可少注册一个便利性任务，
/// 也不要留一条提权链。
/// </summary>
internal static class ExecutableTrust
{
    /// <summary>被认为「等同于管理员」的知名 SID。</summary>
    static readonly string[] TrustedSids =
    {
        "S-1-5-32-544",   // BUILTIN\Administrators
        "S-1-5-18",       // NT AUTHORITY\SYSTEM
        "S-1-5-19",       // NT AUTHORITY\LOCAL SERVICE
        "S-1-5-20",       // NT AUTHORITY\NETWORK SERVICE
        // NT SERVICE\TrustedInstaller：Windows 组件安装器，Program Files 下必然存在
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",
    };

    /// <summary>能够替换掉一个可执行文件的权限位。</summary>
    const FileSystemRights ImageReplacingRights =
        FileSystemRights.WriteData |          // 覆写文件内容
        FileSystemRights.AppendData |         // 在目录里创建文件
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership |
        FileSystemRights.WriteAttributes |
        FileSystemRights.WriteExtendedAttributes;

    /// <summary>一条访问控制项的最小快照，便于把判定逻辑做成纯函数来测试。</summary>
    internal readonly record struct AccessRuleSnapshot(
        string IdentitySid,
        FileSystemRights Rights,
        AccessControlType Type,
        bool InheritOnly);

    /// <summary>
    /// 这组 ACE 里是否存在「授予非管理员身份改写镜像」的项。
    /// InheritOnly 的 ACE 只对新建的子对象生效，不授予对当前对象的访问权，
    /// 因此必须排除——否则 Program Files 里那条 CREATOR OWNER 会让所有位置都被判成不安全。
    /// </summary>
    internal static bool GrantsImageReplacementToNonAdministrators(IEnumerable<AccessRuleSnapshot> rules)
    {
        foreach (AccessRuleSnapshot rule in rules)
        {
            if (rule.Type != AccessControlType.Allow) continue;
            if (rule.InheritOnly) continue;
            if ((rule.Rights & ImageReplacingRights) == 0) continue;
            if (IsTrustedIdentity(rule.IdentitySid)) continue;
            return true;
        }
        return false;
    }

    internal static bool IsTrustedIdentity(string? sid) =>
        !string.IsNullOrWhiteSpace(sid) &&
        Array.Exists(TrustedSids, trusted => string.Equals(trusted, sid, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 当前程序镜像是否位于受保护位置。目录和文件本身都必须通过检查：
    /// 替换一个 EXE 既可以直接覆写文件，也可以删掉再新建，所以两者都要看。
    /// </summary>
    internal static bool IsCurrentImageInProtectedLocation(out string reason)
    {
        string imagePath;
        try
        {
            imagePath = Application.ExecutablePath.Trim();
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                reason = "无法确定当前程序镜像路径";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = "无法确定当前程序镜像路径：" + ex.Message;
            return false;
        }

        return IsProtectedLocation(imagePath, out reason);
    }

    internal static bool IsProtectedLocation(string imagePath, out string reason)
    {
        string? directory;
        try { directory = Path.GetDirectoryName(Path.GetFullPath(imagePath)); }
        catch (Exception ex)
        {
            reason = $"无法解析镜像路径 {imagePath}：{ex.Message}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            reason = $"无法解析镜像所在目录：{imagePath}";
            return false;
        }

        if (!TryReadDirectoryRules(directory, out List<AccessRuleSnapshot> directoryRules, out string error))
        {
            reason = $"无法读取目录权限 {directory}：{error}";
            return false;
        }
        if (GrantsImageReplacementToNonAdministrators(directoryRules))
        {
            reason = $"目录 {directory} 允许非管理员创建或删除文件";
            return false;
        }

        if (File.Exists(imagePath))
        {
            if (!TryReadFileRules(imagePath, out List<AccessRuleSnapshot> fileRules, out error))
            {
                reason = $"无法读取文件权限 {imagePath}：{error}";
                return false;
            }
            if (GrantsImageReplacementToNonAdministrators(fileRules))
            {
                reason = $"文件 {imagePath} 允许非管理员改写";
                return false;
            }
        }

        reason = $"目录 {directory} 仅管理员可写";
        return true;
    }

    static bool TryReadDirectoryRules(string directory, out List<AccessRuleSnapshot> rules, out string error)
    {
        try
        {
            DirectorySecurity security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
            rules = Snapshot(security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            rules = [];
            error = ex.Message;
            return false;
        }
    }

    static bool TryReadFileRules(string path, out List<AccessRuleSnapshot> rules, out string error)
    {
        try
        {
            FileSecurity security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            rules = Snapshot(security.GetAccessRules(true, true, typeof(SecurityIdentifier)));
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            rules = [];
            error = ex.Message;
            return false;
        }
    }

    static List<AccessRuleSnapshot> Snapshot(AuthorizationRuleCollection collection)
    {
        var rules = new List<AccessRuleSnapshot>(collection.Count);
        foreach (AuthorizationRule rule in collection)
        {
            if (rule is not FileSystemAccessRule access) continue;
            rules.Add(new AccessRuleSnapshot(
                access.IdentityReference.Value,
                access.FileSystemRights,
                access.AccessControlType,
                (access.PropagationFlags & PropagationFlags.InheritOnly) != 0));
        }
        return rules;
    }
}
