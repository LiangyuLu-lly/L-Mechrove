<#
.SYNOPSIS
    Publish / unpublish L-Mechrevo release metadata on the update server.

.DESCRIPTION
    Reverse-engineered admin API of https://l-mechrevo.onismy.cn (panel: /la-md/).

      * GET  /api/auth.php?action=check          -> { ok, logged_in, csrf }
      * POST /api/auth.php                       -> multipart action=login&password=<cred>
                                                    sets LMSESSID cookie, returns { ok, csrf }
      * GET  /api/downloads.php                  -> public list of releases (JSON)
      * POST /api/downloads.php                  -> multipart action=create|update|delete (needs X-CSRF-Token)
      * GET  /api/update_check.php?version=...&channel=...  -> public verification endpoint

    Server-side facts this script relies on:
      * create needs version + (file XOR external_url); channel is stable|beta.
      * file upload allowlist: exe / zip / 7z / rar / msi / gz. filename/size/sha256 are
        computed by the server (form fields for them are ignored).
      * update is a partial patch: omitted fields keep their value; non-empty external_url
        switches the record to mirror mode (old uploaded file is deleted); uploading a file
        switches back and deletes the previous file.
      * delete removes the record AND its uploaded file.
      * update_check picks the record with the highest numeric version (channel preferred,
        otherwise cross-channel fallback), so rollback = delete/demote newer records.

    The password is read from C:\Users\28717\.lmechrevo\admin.cred (never printed).
    PS 5.1 compatible.

.PARAMETER Version
    Version string to publish, e.g. 0.290.0-beta1. Required for publish and for
    -Unpublish -Version.

.PARAMETER File
    Path to the package to upload (exe/zip/7z/rar/msi/gz). Alternative to -DownloadUrl.

.PARAMETER DownloadUrl
    External mirror URL (e.g. Lanzou). "url|pwd" is accepted by the web layer for
    Lanzou extraction codes. Alternative to -File.

.PARAMETER Sha256
    Expected SHA-256 of the file being published. When -File is given and the local
    file does not match, publishing aborts. Server-computed hash is compared afterwards.
    Cannot be set for external-URL releases (server stores no sha256 for those).

.PARAMETER Size
    Expected size in bytes of -File. Local file mismatch aborts the publish.

.PARAMETER Notes
    Release notes (multiline allowed). Only sent when the switch is present.

.PARAMETER Channel
    stable | beta. Default on create: stable. On update the existing channel is kept
    unless this parameter is passed.

.PARAMETER ReleaseDate
    YYYY-MM-DD. Default on create: today (server default). On update the existing date
    is kept unless passed.

.PARAMETER DownloadPage
    Accepted for compatibility only: update_check.php always returns the static
    <BaseUrl>/download.html; the server API cannot change it.

.PARAMETER Unpublish
    Delete release record(s). With -Version deletes that version; with -All deletes
    every release (returns update_check to "no release").

.PARAMETER All
    With -Unpublish: delete all release records.

.PARAMETER Check
    Read-only mode: prints current releases plus a public update_check response.

.PARAMETER BaseUrl
    Site base URL. Default https://l-mechrevo.onismy.cn.

.PARAMETER CredentialPath
    Credential file. Default C:\Users\28717\.lmechrevo\admin.cred.

.EXAMPLE
    .\tools\publish-server-metadata.ps1 -Check

.EXAMPLE
    .\tools\publish-server-metadata.ps1 -Version 0.290.0-beta1 -Channel beta -File .\dist\setup.exe -Notes "notes here"

.EXAMPLE
    .\tools\publish-server-metadata.ps1 -Version 0.290.0-beta1 -Channel beta -DownloadUrl "https://example.com/pkg.zip"

.EXAMPLE
    .\tools\publish-server-metadata.ps1 -Unpublish -Version 0.290.0-beta1

.EXAMPLE
    .\tools\publish-server-metadata.ps1 -Unpublish -All
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$File,
    [string]$DownloadUrl,
    [string]$Sha256,
    [long]$Size = -1,
    [string]$Notes,
    [ValidateSet('stable','beta')][string]$Channel,
    [string]$ReleaseDate,
    [string]$DownloadPage,
    [switch]$Unpublish,
    [switch]$All,
    [switch]$Check,
    [string]$BaseUrl = 'https://l-mechrevo.onismy.cn',
    [string]$CredentialPath = 'C:\Users\28717\.lmechrevo\admin.cred'
)

$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$script:BaseUrl = $BaseUrl.TrimEnd('/')
$script:Cookies = New-Object System.Net.CookieContainer
$script:Csrf = ''
$script:ExitCode = 0

function Write-Sec([string]$Text) { Write-Host $Text }

function Redact-Secret([string]$s) {
    if ([string]::IsNullOrEmpty($s)) { return '(none)' }
    if ($s.Length -le 2) { return '**' }
    return $s.Substring(0, 2) + '**(' + $s.Length + ' chars)'
}

function Invoke-ApiRequest {
    param(
        [string]$Method = 'GET',
        [string]$Path,
        [System.Collections.Specialized.OrderedDictionary]$Fields,
        [string]$UploadPath,
        [bool]$UseCsrf = $true
    )

    $url = $script:BaseUrl + $Path
    if ($Method -eq 'GET') {
        $sep = '?'
        if ($url.IndexOf('?') -ge 0) { $sep = '&' }
        $url = '{0}{1}_t={2}' -f $url, $sep, [DateTime]::UtcNow.Ticks
    }

    $req = [System.Net.HttpWebRequest]::Create($url)
    $req.Method = $Method
    $req.CookieContainer = $script:Cookies
    $req.UserAgent = 'LMechevo-PublishTool/1.0'
    $req.Timeout = 600000
    $req.ReadWriteTimeout = 600000
    if ($UseCsrf -and $script:Csrf) {
        $req.Headers['X-CSRF-Token'] = $script:Csrf
    }

    $enc = New-Object System.Text.UTF8Encoding($false)
    $crlf = "`r`n"
    $boundary = '----OMO' + [Guid]::NewGuid().ToString('N')

    if ($Method -eq 'POST') {
        $req.ContentType = 'multipart/form-data; boundary=' + $boundary

        $head = New-Object System.IO.MemoryStream
        $fieldNames = @()
        if ($Fields) {
            foreach ($k in $Fields.Keys) {
                $part = '--' + $boundary + $crlf +
                        'Content-Disposition: form-data; name="' + $k + '"' + $crlf + $crlf
                $b = $enc.GetBytes($part)
                $head.Write($b, 0, $b.Length)
                $b = $enc.GetBytes([string]$Fields[$k])
                $head.Write($b, 0, $b.Length)
                $b = $enc.GetBytes($crlf)
                $head.Write($b, 0, $b.Length)
                $fieldNames += $k
            }
        }

        $fileLen = 0
        $fileHeadBytes = $null
        $fileTailBytes = $null
        if ($UploadPath) {
            $fi = Get-Item -LiteralPath $UploadPath
            $fileLen = $fi.Length
            $part = '--' + $boundary + $crlf +
                    'Content-Disposition: form-data; name="file"; filename="' + $fi.Name + '"' + $crlf +
                    'Content-Type: application/octet-stream' + $crlf + $crlf
            $fileHeadBytes = $enc.GetBytes($part)
            $fileTailBytes = $enc.GetBytes($crlf)
            $fieldNames += ('file(' + $fi.Name + ', ' + $fileLen + ' bytes)')
        }

        $tailBytes = $enc.GetBytes('--' + $boundary + '--' + $crlf)
        $headBytes = $head.ToArray()
        $fileExtra = 0
        if ($fileHeadBytes) { $fileExtra = $fileHeadBytes.Length + $fileTailBytes.Length }
        $req.ContentLength = $headBytes.Length + $fileLen + $fileExtra + $tailBytes.Length

        Write-Sec ('[req] POST {0} fields=[{1}] csrf={2}' -f $Path, ($fieldNames -join ', '), (Redact-Secret $script:Csrf))

        $rs = $req.GetRequestStream()
        try {
            $rs.Write($headBytes, 0, $headBytes.Length)
            if ($fileHeadBytes) {
                $rs.Write($fileHeadBytes, 0, $fileHeadBytes.Length)
                $fs = [System.IO.File]::OpenRead($UploadPath)
                try {
                    $buf = New-Object byte[] 1048576
                    while (($n = $fs.Read($buf, 0, $buf.Length)) -gt 0) { $rs.Write($buf, 0, $n) }
                } finally { $fs.Close() }
                $rs.Write($fileTailBytes, 0, $fileTailBytes.Length)
            }
            $rs.Write($tailBytes, 0, $tailBytes.Length)
        } finally { $rs.Close() }
    } else {
        Write-Sec ('[req] GET {0}' -f $Path)
    }

    $resp = $null
    try {
        $resp = $req.GetResponse()
    } catch [System.Net.WebException] {
        if ($_.Exception.Response) { $resp = $_.Exception.Response } else { throw }
    }

    $status = [int]$resp.StatusCode
    $reader = New-Object System.IO.StreamReader($resp.GetResponseStream(), [System.Text.Encoding]::UTF8)
    $body = $reader.ReadToEnd()
    $reader.Close()
    $resp.Close()

    $json = $null
    try { $json = $body | ConvertFrom-Json } catch { }
    if ($Method -eq 'POST' -and $Fields -and $Fields['password']) {
        Write-Sec ('[resp] HTTP {0} (login response body suppressed)' -f $status)
    } else {
        Write-Sec ('[resp] HTTP {0} {1}' -f $status, $body)
    }
    return [pscustomobject]@{ Status = $status; Body = $body; Json = $json }
}

function Connect-Admin {
    $r = Invoke-ApiRequest -Path '/api/auth.php?action=check' -UseCsrf $false
    if ($r.Json -and $r.Json.logged_in -eq $true) {
        $script:Csrf = [string]$r.Json.csrf
        Write-Sec ('[auth] existing session ok, csrf={0}' -f (Redact-Secret $script:Csrf))
        return
    }
    if (-not (Test-Path -LiteralPath $CredentialPath)) {
        throw ('credential file not found: ' + $CredentialPath)
    }
    $cred = [System.IO.File]::ReadAllText($CredentialPath).Trim()
    if (-not $cred) { throw 'credential file is empty' }
    Write-Sec ('[auth] logging in, credential {0} from {1}' -f (Redact-Secret $cred), $CredentialPath)

    $fields = New-Object System.Collections.Specialized.OrderedDictionary
    $fields.Add('action', 'login')
    $fields.Add('password', $cred)
    $login = Invoke-ApiRequest -Method 'POST' -Path '/api/auth.php' -Fields $fields -UseCsrf $false
    if (-not $login.Json -or $login.Json.ok -ne $true) {
        throw ('login failed (HTTP ' + $login.Status + '): ' + $login.Body)
    }
    $script:Csrf = [string]$login.Json.csrf
    Write-Sec ('[auth] login OK, csrf={0}' -f (Redact-Secret $script:Csrf))
}

function Get-Releases {
    $r = Invoke-ApiRequest -Path '/api/downloads.php'
    if (-not $r.Json -or $r.Json.ok -ne $true) { throw ('release list failed: ' + $r.Body) }
    if ($null -eq $r.Json.data) { return @() }
    return @($r.Json.data)
}

function Get-UpdateCheck([string]$ClientVersion, [string]$CheckChannel) {
    $qv = [Uri]::EscapeDataString($ClientVersion)
    $qc = [Uri]::EscapeDataString($CheckChannel)
    $r = Invoke-ApiRequest -Path ('/api/update_check.php?version=' + $qv + '&channel=' + $qc) -UseCsrf $false
    if (-not $r.Json -or $r.Json.ok -ne $true) { throw ('update_check failed: ' + $r.Body) }
    return $r.Json.data
}

function Show-ReleaseSummary($releases) {
    if (-not $releases -or $releases.Count -eq 0) {
        Write-Sec '[state] no releases published'
        return
    }
    Write-Sec ('[state] {0} release record(s):' -f $releases.Count)
    foreach ($p in $releases) {
        $src = 'file=' + $p.file_path
        if (-not $p.file_path) { $src = 'url=' + $p.external_url }
        $notes = [string]$p.notes
        $notes = $notes -replace "`r", ' ' -replace "`n", ' '
        if ($notes.Length -gt 40) { $notes = $notes.Substring(0, 40) + '...' }
        $shortSha = '(none)'
        if ($p.sha256) { $shortSha = ([string]$p.sha256).Substring(0, 16) + '...' }
        Write-Sec ('  - id={0} version={1} channel={2} date={3} notes={4} sha256={5} size={6} {7}' -f `
            $p.id, $p.version, $p.channel, $p.release_date, $notes, $shortSha, $p.size, $src)
    }
}

function Show-UpdateCheck([string]$ClientVersion, [string]$CheckChannel) {
    Write-Sec ('[verify] update_check.php version={0} channel={1}' -f $ClientVersion, $CheckChannel)
    $d = Get-UpdateCheck -ClientVersion $ClientVersion -CheckChannel $CheckChannel
    Write-Sec ('[verify] latest_version={0} channel={1} channel_fallback={2} is_latest={3} update_available={4} download_url={5} filename={6} size={7} sha256={8}' -f `
        $d.latest_version, $d.channel, $d.channel_fallback, $d.is_latest, $d.update_available, $d.download_url, $d.filename, $d.size, $d.sha256)
    return $d
}

# ---------------------------------------------------------------- main

try {
    if ($DownloadPage) {
        Write-Sec ('[note] -DownloadPage is informational only; update_check.php always returns ' + $script:BaseUrl + '/download.html')
    }

    Connect-Admin

    if ($Check) {
        $list = Get-Releases
        Show-ReleaseSummary $list
        $cv = '0.0.1'
        if ($Version) { $cv = $Version }
        $ch = 'stable'
        if ($Channel) { $ch = $Channel }
        Show-UpdateCheck -ClientVersion $cv -CheckChannel $ch | Out-Null
        Write-Sec '[done] read-only check'
        exit $script:ExitCode
    }

    if ($Unpublish) {
        $list = Get-Releases
        if ($All) {
            $targets = $list
        } elseif ($Version) {
            $targets = @($list | Where-Object { $_.version -eq $Version })
        } else {
            throw 'with -Unpublish pass -Version <v> or -All'
        }
        if (-not $targets -or $targets.Count -eq 0) {
            Write-Sec '[unpublish] nothing matched, nothing deleted'
            exit $script:ExitCode
        }
        foreach ($t in $targets) {
            Write-Sec ('[unpublish] deleting id={0} version={1} channel={2} date={3}' -f $t.id, $t.version, $t.channel, $t.release_date)
            $fields = New-Object System.Collections.Specialized.OrderedDictionary
            $fields.Add('action', 'delete')
            $fields.Add('id', $t.id)
            $r = Invoke-ApiRequest -Method 'POST' -Path '/api/downloads.php' -Fields $fields
            if (-not $r.Json -or $r.Json.ok -ne $true) {
                $script:ExitCode = 1
                Write-Sec ('[unpublish] FAILED for id={0}: {1}' -f $t.id, $r.Body)
            }
        }
        Show-ReleaseSummary (Get-Releases)
        $ch = 'stable'
        if ($Channel) { $ch = $Channel }
        Show-UpdateCheck -ClientVersion '0.0.1' -CheckChannel $ch | Out-Null
        Write-Sec ('[done] unpublish finished, exit={0}' -f $script:ExitCode)
        exit $script:ExitCode
    }

    # ---- publish ----
    if (-not $Version) { throw 'publish needs -Version <v> (or use -Check / -Unpublish)' }

    $existing = @(Get-Releases | Where-Object { $_.version -eq $Version })
    if ($existing.Count -gt 1) {
        Write-Sec ('[warn] {0} records share version {1}; updating the first one' -f $existing.Count, $Version)
    }

    if ($existing.Count -eq 0 -and -not $File -and -not $DownloadUrl) {
        throw 'new release needs -File <path> or -DownloadUrl <url>'
    }
    if ($File -and $DownloadUrl) {
        Write-Sec '[warn] both -File and -DownloadUrl given; the server upload wins, but the web UI forbids this combination - prefer one'
    }

    if ($File) {
        if (-not (Test-Path -LiteralPath $File)) { throw ('file not found: ' + $File) }
        $fi = Get-Item -LiteralPath $File
        if ($Sha256) {
            $local = (Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash
            if ($local -ne $Sha256.Replace(' ', '').ToUpperInvariant()) {
                throw ('SHA-256 mismatch for ' + $File + ': local=' + $local + ' expected=' + $Sha256 + ' - refusing to publish')
            }
            Write-Sec ('[precheck] local SHA-256 matches -Sha256 ({0})' -f (Redact-Secret $local))
        }
        if ($PSBoundParameters.ContainsKey('Size')) {
            if ($Size -le 0) { throw '-Size must be a positive byte count' }
            if ($fi.Length -ne $Size) {
                throw ('size mismatch for ' + $File + ': local=' + $fi.Length + ' expected=' + $Size + ' - refusing to publish')
            }
            Write-Sec ('[precheck] local size matches -Size ({0} bytes)' -f $fi.Length)
        }
    } elseif ($Sha256 -or $PSBoundParameters.ContainsKey('Size')) {
        Write-Sec '[note] -Sha256/-Size are not stored for external-URL releases; server keeps them null. They were not sent.'
    }

    $fields = New-Object System.Collections.Specialized.OrderedDictionary
    if ($existing.Count -gt 0) {
        $fields.Add('action', 'update')
        $fields.Add('id', $existing[0].id)
        Write-Sec ('[publish] updating existing record id={0}' -f $existing[0].id)
    } else {
        $fields.Add('action', 'create')
        Write-Sec '[publish] creating new record'
    }
    $fields.Add('version', $Version)
    if ($PSBoundParameters.ContainsKey('Channel')) {
        $fields.Add('channel', $Channel)
    } elseif ($existing.Count -eq 0) {
        $fields.Add('channel', 'stable')
    }
    if ($PSBoundParameters.ContainsKey('ReleaseDate')) {
        $fields.Add('release_date', $ReleaseDate)
    } elseif ($existing.Count -eq 0) {
        $fields.Add('release_date', (Get-Date -Format 'yyyy-MM-dd'))
    }
    if ($PSBoundParameters.ContainsKey('Notes')) { $fields.Add('notes', $Notes) }
    if ($DownloadUrl) { $fields.Add('external_url', $DownloadUrl) }

    $r = Invoke-ApiRequest -Method 'POST' -Path '/api/downloads.php' -Fields $fields -UploadPath $File
    if (-not $r.Json -or $r.Json.ok -ne $true) {
        $script:ExitCode = 1
        throw ('publish failed (HTTP ' + $r.Status + '): ' + $r.Body)
    }

    $saved = $r.Json.data
    Write-Sec ('[publish] saved id={0} version={1} channel={2} date={3} filename={4} size={5} sha256={6} external_url={7}' -f `
        $saved.id, $saved.version, $saved.channel, $saved.release_date, $saved.filename, $saved.size, $saved.sha256, $saved.external_url)

    if ($File -and $Sha256 -and $saved.sha256) {
        if ($saved.sha256.ToUpperInvariant() -ne $Sha256.Replace(' ', '').ToUpperInvariant()) {
            $script:ExitCode = 2
            Write-Sec '[verify] FAIL: server-computed sha256 differs from -Sha256'
        } else {
            Write-Sec '[verify] server-computed sha256 matches -Sha256'
        }
    }
    if ($File -and $PSBoundParameters.ContainsKey('Size') -and $saved.size) {
        if ([long]$saved.size -ne $Size) {
            $script:ExitCode = 2
            Write-Sec '[verify] FAIL: server-reported size differs from -Size'
        } else {
            Write-Sec '[verify] server-reported size matches -Size'
        }
    }

    Show-ReleaseSummary (Get-Releases)

    $ch = 'stable'
    if ($PSBoundParameters.ContainsKey('Channel')) { $ch = $Channel }
    elseif ($saved.channel) { $ch = $saved.channel }
    $d = Show-UpdateCheck -ClientVersion $Version -CheckChannel $ch
    if ($d.latest_version -ne $Version) {
        $script:ExitCode = 2
        Write-Sec ('[verify] NOTE: latest_version is {0}, not {1} (a higher version exists on the server)' -f $d.latest_version, $Version)
    } else {
        Write-Sec '[verify] published version is the channel latest'
        $d2 = Show-UpdateCheck -ClientVersion '0.0.1' -CheckChannel $ch
        if ($d2.update_available -ne $true) {
            Write-Sec '[verify] note: synthetic client 0.0.1 did not report update_available=true (version comparison quirk)'
        }
    }

    Write-Sec ('[done] publish finished, exit={0}' -f $script:ExitCode)
    exit $script:ExitCode
}
catch {
    Write-Sec ('[error] ' + $_.Exception.Message)
    exit 1
}
