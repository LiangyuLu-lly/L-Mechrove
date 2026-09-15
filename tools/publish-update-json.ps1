<#
.SYNOPSIS
    Publish the static update-check JSON for L-Mechrevo to Alibaba Cloud OSS.

.DESCRIPTION
    Writes ONE static object that the client already knows how to read:

      Bucket : lmechrevo  (华东1 杭州, ACL 公共读)
      Key    : lmechrevo-oss/api/update_check.php
      Client base URL : https://lmechrevo.oss-cn-hangzhou.aliyuncs.com/lmechrevo-oss

    The client is a STATIC reader: it appends /api/update_check.php to the configured
    base and parses the object as ordinary JSON. It cross-checks
    latest_version > current_version, so data.current_version MUST be the version
    BELOW the one being published or the update is never offered.

    Upload path: self-contained OSS REST Signature V1 (HMAC-SHA1), no ossutil and no
    SDK required. Requests set Content-Type: application/json; charset=utf-8 and a
    short Cache-Control so clients do not serve a stale check.

    The AccessKey is read from C:\Users\28717\.lmechrevo\oss.ak (two lines:
    AccessKeyId then AccessKeySecret). The secret is never printed, logged or written.
    PowerShell 5.1 compatible (no ternary, no null-coalescing).

.PARAMETER Version
    Version being published, e.g. 0.289.0-beta16 -> data.latest_version.

.PARAMETER DownloadUrl
    Direct package URL (e.g. the GitHub release asset) -> data.download_url.

.PARAMETER PackagePath
    Optional local package file. Its SHA-256 and byte size are computed locally and
    embedded (data.sha256 / data.size). The client only does STRONG verification when
    BOTH download_url and sha256 are present, so pass this on real releases.

.PARAMETER Notes
    Release notes text -> data.notes.

.PARAMETER DownloadPage
    Landing page -> data.download_page. Default https://l-mechrevo.onismy.cn/download.html.

.PARAMETER CurrentVersion
    The version the client should treat as current -> data.current_version.
    Default: read from the live published JSON before writing. Must be lower than
    -Version for the client to offer the update.

.PARAMETER WhatIf
.PARAMETER DryRun
    Build and print the JSON plus the intended request; do not upload and do not verify.

.PARAMETER VerifyUrl
    Public URL used for post-upload verification. Default is derived from the bucket
    host + object key.

.PARAMETER ObjectKey
    Override the object key (default lmechrevo-oss/api/update_check.php). Intended for
    a clearly-named test key; the live key is the default and should be what releases use.

.PARAMETER CacheControl
    Cache-Control metadata stored on the object. Default no-cache.

.PARAMETER CheckPrivilege
    Diagnostic only, read-only: signed ListBuckets (GET service root) and ListObjects
    (GET bucket root), then prints a privilege verdict. Performs no write of any kind.

.PARAMETER CredentialPath
    Credential file. Default C:\Users\28717\.lmechrevo\oss.ak.

.EXAMPLE
    .\tools\publish-update-json.ps1 -DryRun -Version 0.289.0-beta16 -CurrentVersion 0.289.0-beta15 -DownloadUrl "https://github.com/LiangyuLu-lly/L-Mechrevo/releases/download/v0.289.0-beta16/L-Mechrevo-0.289.0-beta16.exe"

.EXAMPLE
    .\tools\publish-update-json.ps1 -Version 0.289.0-beta16 -DownloadUrl "https://github.com/LiangyuLu-lly/L-Mechrevo/releases/download/v0.289.0-beta16/L-Mechrevo-0.289.0-beta16.exe" -PackagePath .\release\L-Mechrevo-beta16.exe -Notes "..."

.EXAMPLE
    .\tools\publish-update-json.ps1 -CheckPrivilege
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$DownloadUrl,
    [string]$PackagePath,
    [string]$Notes,
    [string]$DownloadPage = 'https://l-mechrevo.onismy.cn/download.html',
    [string]$CurrentVersion,
    [switch]$WhatIf,
    [switch]$DryRun,
    [string]$VerifyUrl,
    [string]$ObjectKey = 'lmechrevo-oss/api/update_check.php',
    [string]$CacheControl = 'no-cache',
    [switch]$CheckPrivilege,
    [string]$CredentialPath = 'C:\Users\28717\.lmechrevo\oss.ak'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# --- fixed target (华东1 杭州) -------------------------------------------------
$script:Bucket      = 'lmechrevo'
$script:ServiceHost = 'oss-cn-hangzhou.aliyuncs.com'
$script:BucketHost  = 'lmechrevo.oss-cn-hangzhou.aliyuncs.com'
$script:ExitCode    = 0

function Write-Log([string]$Text) { Write-Host $Text }

function Get-OssCredentials {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        throw ('OSS credential file not found: ' + $Path + ' (expected two lines: AccessKeyId, then AccessKeySecret)')
    }
    $lines = @([System.IO.File]::ReadAllLines($Path))
    if ($lines.Count -lt 2) {
        throw ('OSS credential file must contain two lines (AccessKeyId, AccessKeySecret): ' + $Path)
    }
    $id = $lines[0].Trim()
    $secret = $lines[1].Trim()
    if (-not $id -or -not $secret) {
        throw ('OSS credential file has an empty line: ' + $Path)
    }
    $short = $id.Substring(0, [Math]::Min(8, $id.Length)) + '...'
    return [pscustomobject]@{ Id = $id; Secret = $secret; IdShort = $short }
}

# OSS REST Signature V1. StringToSign = VERB + \n + Content-MD5 + \n + Content-Type
# + \n + Date + \n + CanonicalizedOSSHeaders + CanonicalizedResource
function New-OssStringToSign {
    param(
        [string]$Method,
        [string]$ContentMd5,
        [string]$ContentType,
        [string]$Date,
        [string]$CanonicalizedResource
    )
    return ($Method + "`n" + $ContentMd5 + "`n" + $ContentType + "`n" + $Date + "`n" + $CanonicalizedResource)
}

function New-OssRequest {
    param(
        [string]$Method,
        [string]$Url,
        [byte[]]$Body,
        [string]$ContentType = '',
        [System.Collections.Specialized.OrderedDictionary]$ExtraHeaders,
        [string]$CanonicalizedResource,
        [string]$AkId,
        [string]$AkSecret
    )

    $md5 = ''
    if ($Body -and $Body.Length -gt 0) {
        $algo = [System.Security.Cryptography.MD5]::Create()
        try { $md5 = [Convert]::ToBase64String($algo.ComputeHash($Body)) } finally { $algo.Dispose() }
    }

    # Date and the signed string must be byte-identical; format both from one UTC value.
    $dateValue = [DateTime]::UtcNow
    $dateStr = $dateValue.ToString('r', [System.Globalization.CultureInfo]::InvariantCulture)

    $stringToSign = New-OssStringToSign -Method $Method -ContentMd5 $md5 -ContentType $ContentType `
        -Date $dateStr -CanonicalizedResource $CanonicalizedResource

    $hmac = New-Object System.Security.Cryptography.HMACSHA1
    try {
        $hmac.Key = [System.Text.Encoding]::UTF8.GetBytes($AkSecret)
        $signature = [Convert]::ToBase64String(
            $hmac.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($stringToSign)))
    } finally { $hmac.Dispose() }

    $req = [System.Net.HttpWebRequest]::Create($Url)
    $req.Method = $Method
    $req.Date = $dateValue
    $req.Timeout = 120000
    $req.ReadWriteTimeout = 120000
    if ($ContentType) { $req.ContentType = $ContentType }
    if ($md5) { $req.Headers['Content-MD5'] = $md5 }
    if ($ExtraHeaders) {
        foreach ($k in $ExtraHeaders.Keys) { $req.Headers[[string]$k] = [string]$ExtraHeaders[$k] }
    }
    $req.Headers['Authorization'] = 'OSS ' + $AkId + ':' + $signature

    if ($Body -and $Body.Length -gt 0 -and ($Method -eq 'PUT' -or $Method -eq 'POST')) {
        $req.ContentLength = $Body.Length
        $rs = $req.GetRequestStream()
        try { $rs.Write($Body, 0, $Body.Length) } finally { $rs.Close() }
    }
    return $req
}

function Send-OssRequest {
    param([System.Net.HttpWebRequest]$Request)
    $resp = $null
    try {
        $resp = $Request.GetResponse()
    } catch [System.Net.WebException] {
        if ($_.Exception.Response) { $resp = $_.Exception.Response } else { throw }
    }
    $status = [int]$resp.StatusCode
    $reader = New-Object System.IO.StreamReader($resp.GetResponseStream(), [System.Text.Encoding]::UTF8)
    $body = $reader.ReadToEnd()
    $reader.Close()
    $headers = @{}
    foreach ($k in $resp.Headers.AllKeys) { $headers[$k] = $resp.Headers[$k] }
    $resp.Close()
    return [pscustomobject]@{ Status = $status; Body = $body; Headers = $headers }
}

function ConvertTo-Text {
    param($Content)
    if ($null -eq $Content) { return '' }
    if ($Content -is [byte[]]) { return [System.Text.Encoding]::UTF8.GetString($Content) }
    return [string]$Content
}

function Get-PublicJson {
    param([string]$Url)
    $sep = '?'
    if ($Url.IndexOf('?') -ge 0) { $sep = '&' }
    $busted = $Url + $sep + '_cb=' + [DateTime]::UtcNow.Ticks
    $resp = Invoke-WebRequest -Uri $busted -UseBasicParsing -Method GET
    $text = ConvertTo-Text -Content $resp.Content
    $json = $null
    try { $json = $text | ConvertFrom-Json } catch { }
    return [pscustomobject]@{ Status = [int]$resp.StatusCode; Text = $text; Json = $json; Headers = $resp.Headers }
}

function New-UpdateJson {
    param(
        [string]$Current,
        [string]$Latest,
        [string]$Url,
        [string]$Page,
        [string]$Note,
        [string]$FileName,
        [long]$Size,
        [string]$Sha256
    )
    $data = [ordered]@{
        current_version  = $Current
        latest_version   = $Latest
        update_available = $true
        download_url     = $Url
    }
    if ($FileName) { $data['filename'] = $FileName }
    if ($Size -gt 0) { $data['size'] = $Size }
    if ($Sha256) { $data['sha256'] = $Sha256 }
    $data['notes'] = $Note
    $data['release_date'] = (Get-Date -Format 'yyyy-MM-dd')
    $data['channel'] = 'beta'
    $data['download_page'] = $Page
    return ([ordered]@{ ok = $true; data = $data } | ConvertTo-Json -Depth 6 -Compress)
}

# OSS error bodies are XML like <Error><Code>...</Code><Message>...</Message></Error>.
# The Code distinguishes a genuine AccessDenied from a SignatureDoesNotMatch (a signing bug).
function Get-OssErrorCode {
    param([string]$Body)
    if (-not $Body) { return '(empty body)' }
    try {
        $x = [xml]$Body
        if ($x.Error.Code) { return [string]$x.Error.Code }
    } catch { }
    return '(unparsed body)'
}

function Invoke-CheckPrivilege {
    $cred = Get-OssCredentials -Path $CredentialPath
    Write-Log ('[auth] AccessKeyId=' + $cred.IdShort + ' secret=**** (read from ' + $CredentialPath + ')')
    Write-Log '[priv] read-only probe: OSS ListBuckets + ListObjects + GetBucketVersioning, no write performed'

    $verdict = 'unknown'

    # GET / on the service endpoint == ListBuckets (account-wide enumeration).
    $svcUrl = 'https://' + $script:ServiceHost + '/'
    $req = New-OssRequest -Method 'GET' -Url $svcUrl -CanonicalizedResource '/' -AkId $cred.Id -AkSecret $cred.Secret
    $r = Send-OssRequest -Request $req
    Write-Log ('[priv] ListBuckets GET ' + $svcUrl + ' -> HTTP ' + $r.Status)
    if ($r.Status -eq 200) {
        $names = @()
        try {
            $xml = [xml]$r.Body
            foreach ($b in $xml.ListAllMyBucketsResult.Buckets.Bucket) { $names += [string]$b.Name }
        } catch { }
        Write-Log ('[priv] ListBuckets SUCCEEDED - enumerates ' + $names.Count + ' bucket(s): ' + ($names -join ', '))
        $verdict = 'OVER-PRIVILEGED'
    } elseif ($r.Status -eq 403) {
        Write-Log ('[priv] ListBuckets denied (HTTP 403 code=' + (Get-OssErrorCode $r.Body) + ') - not account-wide')
    } else {
        Write-Log ('[priv] ListBuckets unexpected HTTP ' + $r.Status + ' code=' + (Get-OssErrorCode $r.Body))
    }

    # GET / on the bucket endpoint == ListObjects (bucket-wide enumeration).
    $bucketUrl = 'https://' + $script:BucketHost + '/'
    $req2 = New-OssRequest -Method 'GET' -Url $bucketUrl -CanonicalizedResource ('/' + $script:Bucket + '/') -AkId $cred.Id -AkSecret $cred.Secret
    $r2 = Send-OssRequest -Request $req2
    Write-Log ('[priv] ListObjects GET ' + $bucketUrl + ' -> HTTP ' + $r2.Status)
    if ($r2.Status -eq 200) {
        $n = 0
        try { $x = [xml]$r2.Body; $n = @($x.ListBucketResult.Contents).Count } catch { }
        Write-Log ('[priv] ListObjects SUCCEEDED - can enumerate objects in bucket (' + $n + ' key(s) in first page)')
        if ($verdict -eq 'unknown') { $verdict = 'OVER-PRIVILEGED' }
    } elseif ($r2.Status -eq 403) {
        Write-Log ('[priv] ListObjects denied (HTTP 403 code=' + (Get-OssErrorCode $r2.Body) + ')')
    } else {
        Write-Log ('[priv] ListObjects unexpected HTTP ' + $r2.Status + ' code=' + (Get-OssErrorCode $r2.Body))
    }

    # GET /?versioning -> tells us whether object history (rollback) is available.
    $verUrl = $bucketUrl + '?versioning'
    $req3 = New-OssRequest -Method 'GET' -Url $verUrl -CanonicalizedResource ('/' + $script:Bucket + '/?versioning') -AkId $cred.Id -AkSecret $cred.Secret
    $r3 = Send-OssRequest -Request $req3
    Write-Log ('[priv] GetBucketVersioning GET ' + $verUrl + ' -> HTTP ' + $r3.Status)
    if ($r3.Status -eq 200) {
        $vs = 'never-enabled (no Status element)'
        try {
            if ($r3.Body) {
                $x3 = [xml]$r3.Body
                if ($x3.VersioningConfiguration.Status) { $vs = [string]$x3.VersioningConfiguration.Status }
            }
        } catch { }
        Write-Log ('[priv] bucket versioning: ' + $vs)
    } elseif ($r3.Status -eq 403) {
        Write-Log ('[priv] versioning query denied (HTTP 403 code=' + (Get-OssErrorCode $r3.Body) + ') - cannot read versioning config with this key')
    } else {
        Write-Log ('[priv] versioning query unexpected HTTP ' + $r3.Status + ' code=' + (Get-OssErrorCode $r3.Body))
    }

    if ($verdict -eq 'OVER-PRIVILEGED') {
        Write-Log '[priv] VERDICT: OVER-PRIVILEGED - the AK exceeds the minimal PutObject scope; replace it with a RAM user scoped to tools\oss-writer-policy.json'
    } else {
        Write-Log '[priv] VERDICT: the AK could not enumerate buckets or objects (consistent with a least-privilege writer)'
    }
    return $verdict
}

# ------------------------------------------------------------------ main
try {
    if ($CheckPrivilege) {
        Invoke-CheckPrivilege | Out-Null
        exit $script:ExitCode
    }

    if (-not $Version) { throw 'publish needs -Version <v> (or use -CheckPrivilege)' }

    $isDry = ($DryRun -or $WhatIf)
    $cred = Get-OssCredentials -Path $CredentialPath
    Write-Log ('[auth] AccessKeyId=' + $cred.IdShort + ' secret=**** (read from ' + $CredentialPath + ')')

    if (-not $DownloadUrl) {
        if ($isDry) {
            Write-Log '[warn] -DownloadUrl not supplied for dry run; JSON will carry an empty download_url'
        } else {
            throw 'publish needs -DownloadUrl <url> (the direct package URL)'
        }
    }

    $fileName = ''
    $size = [long]0
    $sha256 = ''
    if ($PackagePath) {
        if (-not (Test-Path -LiteralPath $PackagePath)) { throw ('package not found: ' + $PackagePath) }
        $fi = Get-Item -LiteralPath $PackagePath
        $size = [long]$fi.Length
        $fileName = $fi.Name
        $sha256 = (Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Log ('[pkg] ' + $fileName + ' size=' + $size + ' sha256=' + $sha256)
    } else {
        Write-Log '[pkg] no -PackagePath: size/sha256 omitted (client stays on structural checks, not hash-verified)'
    }

    # Resolve data.current_version: explicit wins, else read the live published value.
    if (-not $CurrentVersion) {
        $liveUrl = 'https://' + $script:BucketHost + '/' + $ObjectKey
        try {
            $live = Get-PublicJson -Url $liveUrl
        } catch {
            throw ('cannot read the live JSON to default -CurrentVersion (' + $_.Exception.Message + '); pass -CurrentVersion explicitly')
        }
        if ($live.Json -and $live.Json.data) {
            if ($live.Json.data.latest_version) { $CurrentVersion = [string]$live.Json.data.latest_version }
            elseif ($live.Json.data.current_version) { $CurrentVersion = [string]$live.Json.data.current_version }
        }
        if (-not $CurrentVersion) {
            throw 'could not determine the currently published version; pass -CurrentVersion explicitly'
        }
        Write-Log ('[current] defaulted -CurrentVersion to live value ' + $CurrentVersion)
    }

    $json = New-UpdateJson -Current $CurrentVersion -Latest $Version -Url $DownloadUrl -Page $DownloadPage `
        -Note $Notes -FileName $fileName -Size $size -Sha256 $sha256
    $bodyBytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($json)

    $objectUrl = 'https://' + $script:BucketHost + '/' + $ObjectKey
    $canonicalResource = '/' + $script:Bucket + '/' + $ObjectKey
    if (-not $VerifyUrl) { $VerifyUrl = $objectUrl }

    $contentType = 'application/json; charset=utf-8'
    $headers = New-Object System.Collections.Specialized.OrderedDictionary
    $headers.Add('Cache-Control', $CacheControl)

    $md5Preview = ''
    $md5algo = [System.Security.Cryptography.MD5]::Create()
    try { $md5Preview = [Convert]::ToBase64String($md5algo.ComputeHash($bodyBytes)) } finally { $md5algo.Dispose() }

    Write-Log ('[json] ' + $json)
    Write-Log ('[json] ' + $bodyBytes.Length + ' bytes (utf-8, no BOM)')
    Write-Log ('[intended] PUT ' + $objectUrl)
    Write-Log ('[intended] Authorization: OSS ' + $cred.IdShort + ':****  (Signature V1 / HMAC-SHA1, recomputed at send time)')
    Write-Log ('[intended] Content-Type: ' + $contentType)
    Write-Log ('[intended] Cache-Control: ' + $CacheControl)
    Write-Log ('[intended] Content-MD5: ' + $md5Preview)
    Write-Log ('[intended] CanonicalizedResource: ' + $canonicalResource)

    if ($isDry) {
        Write-Log '[dry-run] no upload, no verification'
        exit $script:ExitCode
    }

    $req = New-OssRequest -Method 'PUT' -Url $objectUrl -Body $bodyBytes -ContentType $contentType `
        -ExtraHeaders $headers -CanonicalizedResource $canonicalResource -AkId $cred.Id -AkSecret $cred.Secret
    $r = Send-OssRequest -Request $req
    Write-Log ('[upload] HTTP ' + $r.Status + ' ETag=' + [string]$r.Headers['ETag'])
    if ($r.Status -ne 200) {
        throw ('upload failed: HTTP ' + $r.Status + ' ' + $r.Body)
    }

    Write-Log ('[verify] GET ' + $VerifyUrl + ' (cache-busted)')
    $v = Get-PublicJson -Url $VerifyUrl
    $d = $null
    if ($v.Json) { $d = $v.Json.data }
    if (-not $v.Json -or -not $d) { throw ('verification failed: public URL did not return parseable JSON: ' + $v.Text) }

    Write-Log ('[verify] HTTP ' + $v.Status)
    Write-Log ('[verify] ok=' + [string]$v.Json.ok + ' current_version=' + [string]$d.current_version + ' latest_version=' + [string]$d.latest_version + ' update_available=' + [string]$d.update_available)
    Write-Log ('[verify] download_url=' + [string]$d.download_url)
    Write-Log ('[verify] filename=' + [string]$d.filename + ' size=' + [string]$d.size + ' sha256=' + [string]$d.sha256)
    Write-Log ('[verify] channel=' + [string]$d.channel + ' release_date=' + [string]$d.release_date + ' download_page=' + [string]$d.download_page)
    Write-Log ('[verify] headers: Content-Type=' + [string]$v.Headers['Content-Type'] + ' Cache-Control=' + [string]$v.Headers['Cache-Control'] + ' Content-Length=' + [string]$v.Headers['Content-Length'] + ' ETag=' + [string]$v.Headers['ETag'] + ' Last-Modified=' + [string]$v.Headers['Last-Modified'])

    $mismatch = @()
    if ($v.Json.ok -ne $true) { $mismatch += 'ok' }
    if ([string]$d.current_version -ne $CurrentVersion) { $mismatch += ('current_version (got ' + [string]$d.current_version + ')') }
    if ([string]$d.latest_version -ne $Version) { $mismatch += ('latest_version (got ' + [string]$d.latest_version + ')') }
    if ([string]$d.update_available -ne 'True') { $mismatch += 'update_available' }
    if ([string]$d.download_url -ne $DownloadUrl) { $mismatch += ('download_url (got ' + [string]$d.download_url + ')') }
    if ($fileName) {
        if ([string]$d.filename -ne $fileName) { $mismatch += ('filename (got ' + [string]$d.filename + ')') }
        if ([long]$d.size -ne $size) { $mismatch += ('size (got ' + [string]$d.size + ')') }
        if ([string]$d.sha256 -ne $sha256) { $mismatch += ('sha256 (got ' + [string]$d.sha256 + ')') }
    }
    if ([string]$v.Headers['Cache-Control'] -ne $CacheControl) { $mismatch += ('Cache-Control (got ' + [string]$v.Headers['Cache-Control'] + ')') }

    if ($mismatch.Count -gt 0) {
        $script:ExitCode = 1
        throw ('verification FAILED, mismatched field(s): ' + ($mismatch -join '; '))
    }
    Write-Log '[verify] OK: live object matches the intended payload'
    Write-Log ('[done] published ' + $Version + ' to ' + $canonicalResource + ', exit=' + $script:ExitCode)
    exit $script:ExitCode
}
catch {
    Write-Log ('[error] ' + $_.Exception.Message)
    exit 1
}
