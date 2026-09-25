using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MechrevoLite.Update;

/// <summary>
/// 更新包准入策略（纯函数，全部 fail-closed）。
///
/// 背景：检测与下载走粉丝站 <c>l-mechrevo.onismy.cn</c>。服务端 JSON 仍可能被改，
/// 所以"元数据合法 + 包体哈希匹配"必须由客户端自己强制：任何一条不满足都要拒绝
/// 下载/安装，绝不降级成"只做结构校验"。
/// </summary>
internal static class UpdatePolicy
{
    /// <summary>下载包体上限：远超正常体积的响应一律拒绝（防止把磁盘写满或拿到垃圾文件）。</summary>
    internal const long MaxPackageBytes = 400L * 1024 * 1024;

    /// <summary>允许下载更新包的 host：自有域名直链。回环地址另行放行（本地联调）。</summary>
    internal static readonly string[] AllowedDownloadHosts =
    {
        "stats.l-mechrevo.cn",
        "l-mechrevo.cn",
    };

    // 版本 token：可选前导 v、首字符必须是字母/数字，其余只允许字母数字与 . _ + -（拒绝空格/路径/HTML）。
    static readonly Regex SaneVersionToken = new(@"^[vV]?[0-9A-Za-z][0-9A-Za-z._+\-]*$", RegexOptions.Compiled);

    /// <summary>sha256 必须是 64 位十六进制；缺省、长度不符、含非十六进制字符一律不算数。</summary>
    internal static bool IsValidSha256(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 64) return false;
        foreach (char c in value)
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    /// <summary>
    /// 版本串是否像样：只含字母数字与 . _ + -，至少一位数字，不以分隔符开头/结尾，不含 ".."，长度 ≤ 64。
    /// 明显是垃圾（HTML、空白、路径、纯字母）的 latest_version 会被拒。
    /// </summary>
    internal static bool IsSaneVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        string value = version.Trim();
        if (value.Length > 64) return false;
        if (!SaneVersionToken.IsMatch(value)) return false;
        if (!value.Any(char.IsAsciiDigit)) return false;
        if (value[0] is '.' or '-' or '+' or '_' || value[^1] is '.' or '-' or '+' or '_') return false;
        if (value.Contains("..")) return false;
        return true;
    }

    /// <summary>下载地址只接受 https；http 仅放行回环地址（本地联调桩服务器用）。</summary>
    internal static bool IsAcceptableDownloadUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps
        || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    /// <summary>host 是否在允许列表内；回环地址放行（仅测试用途）。</summary>
    internal static bool IsAllowedDownloadHost(Uri uri)
    {
        if (uri.IsLoopback) return true;
        foreach (string host in AllowedDownloadHosts)
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>下载地址是否通过"协议 + host 白名单"。<paramref name="reason"/> 为拒绝原因。</summary>
    internal static bool TryAcceptDownloadUrl(string? url, out Uri? uri, out string reason)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(url)) { reason = "服务端没有提供下载地址"; return false; }
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
        {
            reason = "下载地址不是合法的 URL";
            return false;
        }
        if (!IsAcceptableDownloadUri(parsed))
        {
            reason = $"下载地址必须使用 HTTPS（当前 {parsed.Scheme}）";
            return false;
        }
        if (!IsAllowedDownloadHost(parsed))
        {
            reason = $"下载地址主机不在允许列表内（{parsed.Host}）";
            return false;
        }
        uri = parsed;
        reason = "";
        return true;
    }

    /// <summary>
    /// 是否允许自动安装这个更新：更新可用、版本号像样、下载地址通过协议+host 白名单、sha256 合法、体积在允许范围。
    /// 失败时 <paramref name="reason"/> 是给用户看的原因（fail-closed 的单一判定入口）。
    /// </summary>
    internal static bool TryAcceptOffer(UpdateInfo info, out string reason)
    {
        if (!info.UpdateAvailable) { reason = "没有可用的更新"; return false; }
        if (!IsSaneVersion(info.LatestVersion)) { reason = "服务端版本号不合法"; return false; }
        if (!TryAcceptDownloadUrl(info.DownloadUrl, out _, out reason)) return false;
        if (!IsValidSha256(info.Sha256))
        {
            reason = "服务端未提供有效的 SHA-256 校验值，已拒绝自动安装";
            return false;
        }
        if (!IsAcceptablePackageSize(info.Size))
        {
            reason = "服务端未提供有效的体积声明，已拒绝自动安装";
            return false;
        }
        reason = "";
        return true;
    }

    /// <summary>JSON 声明的体积必须存在、为正、且不超过 <see cref="MaxPackageBytes"/>。</summary>
    internal static bool IsAcceptablePackageSize(long? size) =>
        size is long bytes && bytes > 0 && bytes <= MaxPackageBytes;

    /// <summary>落盘长度必须等于 JSON 声明，且声明本身在允许范围内。缺声明或对不上一律拒绝。</summary>
    internal static bool MatchesDeclaredSize(long actualLength, long? declared) =>
        IsAcceptablePackageSize(declared) && declared == actualLength;

    /// <summary>
    /// Authenticode fail-closed：<c>WinVerifyTrust</c> 验签，再 <c>CryptQueryObject</c>+<c>GetCertHash</c>
    /// 取签名者证书。无签名、验签失败、取不到证书一律拒绝。不发明证书。
    /// </summary>
    internal static bool TryAcceptAuthenticode(string path, out string reason)
    {
        reason = "更新包没有有效的 Authenticode 签名，拒绝安装";
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;
        try
        {
            if (!WinVerifyTrustFile(path)) return false;
            if (!TryGetCertHash(path, out byte[] hash) || hash.Length == 0) return false;
            reason = "";
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    const int CertQueryObjectFile = 1;
    const int CertQueryContentPkcs7SignedEmbed = 1 << 10;
    const int CertQueryFormatAll = 0xE;
    const int CertHashPropId = 3;
    const int X509AsnEncoding = 1;
    const int Pkcs7AsnEncoding = 65536;
    const int CertFindAny = 0;
    const uint WtdUiNone = 2;
    const uint WtdChoiceFile = 1;
    const uint WtdStateActionVerify = 1;
    const uint WtdStateActionClose = 2;
    const uint WtdRevocationCheckNone = 0x10;
    const uint WtdCacheOnlyUrlRetrieval = 0x1000;

    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern unsafe int WinVerifyTrust(IntPtr hwnd, Guid* pgActionID, WinTrustData* pWvtData);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CryptQueryObject(
        int dwObjectType,
        [MarshalAs(UnmanagedType.LPWStr)] string pvObject,
        int dwExpectedContentTypeFlags,
        int dwExpectedFormatTypeFlags,
        int dwFlags,
        out int pdwMsgAndCertEncodingType,
        out int pdwContentType,
        out int pdwFormatType,
        out IntPtr phCertStore,
        out IntPtr phMsg,
        out IntPtr ppvContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CertFreeCertificateContext(IntPtr pCertContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CertCloseStore(IntPtr hCertStore, int dwFlags);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptMsgClose(IntPtr hCryptMsg);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern IntPtr CertFindCertificateInStore(
        IntPtr hCertStore, int dwCertEncodingType, int dwFindFlags, int dwFindType, IntPtr pvFindPara, IntPtr pPrevCertContext);

    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CertGetCertificateContextProperty(IntPtr pCertContext, int dwPropId, byte[]? pvData, ref int pcbData);

    [StructLayout(LayoutKind.Sequential)]
    struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    static unsafe bool WinVerifyTrustFile(string path)
    {
        Guid action = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        fixed (char* pPath = path)
        {
            var fileInfo = new WinTrustFileInfo
            {
                cbStruct = (uint)sizeof(WinTrustFileInfo),
                pcwszFilePath = (IntPtr)pPath,
            };
            var data = new WinTrustData
            {
                cbStruct = (uint)sizeof(WinTrustData),
                dwUIChoice = WtdUiNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = (IntPtr)(&fileInfo),
                dwStateAction = WtdStateActionVerify,
                dwProvFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval,
            };
            int status = WinVerifyTrust(IntPtr.Zero, &action, &data);
            data.dwStateAction = WtdStateActionClose;
            WinVerifyTrust(IntPtr.Zero, &action, &data);
            return status == 0;
        }
    }

    static bool TryGetCertHash(string path, out byte[] hash)
    {
        hash = [];
        if (!CryptQueryObject(
                CertQueryObjectFile, path,
                CertQueryContentPkcs7SignedEmbed, CertQueryFormatAll, 0,
                out int encoding, out _, out _,
                out IntPtr store, out IntPtr msg, out IntPtr certContext))
            return false;
        try
        {
            if (certContext == IntPtr.Zero && store != IntPtr.Zero)
                certContext = CertFindCertificateInStore(
                    store, encoding != 0 ? encoding : X509AsnEncoding | Pkcs7AsnEncoding,
                    0, CertFindAny, IntPtr.Zero, IntPtr.Zero);
            if (certContext == IntPtr.Zero) return false;
            int size = 0;
            CertGetCertificateContextProperty(certContext, CertHashPropId, null, ref size);
            if (size <= 0) return false;
            hash = new byte[size];
            return CertGetCertificateContextProperty(certContext, CertHashPropId, hash, ref size) && hash.Length > 0;
        }
        finally
        {
            if (certContext != IntPtr.Zero) CertFreeCertificateContext(certContext);
            if (msg != IntPtr.Zero) CryptMsgClose(msg);
            if (store != IntPtr.Zero) CertCloseStore(store, 0);
        }
    }

}
