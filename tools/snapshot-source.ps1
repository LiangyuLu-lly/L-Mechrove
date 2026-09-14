#Requires -Version 5.1
<#
.SYNOPSIS
    L-Mechrevo 对应源码快照工具。
.DESCRIPTION
    为当前干净的源码树创建带注释的版本标签，并把对应版本的源码归档导出为
    artifacts\source-snapshots\L-Mechrevo-<Version>-source.zip。

    用法：
        powershell -ExecutionPolicy Bypass -File tools\snapshot-source.ps1 -Version 1.2.3
        powershell -ExecutionPolicy Bypass -File tools\snapshot-source.ps1 -List
#>
[CmdletBinding(DefaultParameterSetName = 'Create')]
param(
    [Parameter(ParameterSetName = 'Create', Mandatory = $true, Position = 0)]
    [string]$Version,

    [Parameter(ParameterSetName = 'List', Mandatory = $true)]
    [switch]$List
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$gitDir = Join-Path $repoRoot '.git'
if (-not (Test-Path -LiteralPath $gitDir)) {
    Write-Host "错误：未在 $repoRoot 找到 git 仓库。" -ForegroundColor Red
    exit 1
}
$archiveDir = Join-Path (Join-Path $repoRoot 'artifacts') 'source-snapshots'

# ---- -List：列出已有快照 ----
if ($List) {
    Write-Host '现有源码快照（标签）：'
    $tags = & git -C $repoRoot tag --list 'v*' --sort=-creatordate
    if ($LASTEXITCODE -ne 0) {
        Write-Host '错误：无法读取标签列表。' -ForegroundColor Red
        exit 1
    }
    if (-not $tags) {
        Write-Host '  (无)'
    }
    else {
        foreach ($t in $tags) {
            $created = & git -C $repoRoot log -1 --format=%ci $t
            Write-Host ("  {0}  {1}" -f $t, $created)
        }
    }
    Write-Host ''
    if (Test-Path -LiteralPath $archiveDir) {
        Write-Host "归档目录：$archiveDir"
        $zips = @(Get-ChildItem -LiteralPath $archiveDir -Filter '*.zip' -File -ErrorAction SilentlyContinue)
        if ($zips.Count -eq 0) {
            Write-Host '  (无归档文件)'
        }
        else {
            foreach ($z in $zips) {
                Write-Host ("  {0}  {1:N0} 字节" -f $z.Name, $z.Length)
            }
        }
    }
    else {
        Write-Host "归档目录尚不存在：$archiveDir"
    }
    exit 0
}

# ---- 校验版本号 ----
if ($Version -notmatch '^[0-9A-Za-z][0-9A-Za-z._-]*$') {
    Write-Host "错误：版本号 '$Version' 无效，只允许字母、数字和 . _ -，且必须以字母或数字开头。" -ForegroundColor Red
    exit 1
}

# ---- 工作区必须干净 ----
$dirty = & git -C $repoRoot status --short
if ($LASTEXITCODE -ne 0) {
    Write-Host '错误：无法读取 git 状态。' -ForegroundColor Red
    exit 1
}
if ($dirty) {
    Write-Host '错误：工作区不干净，拒绝创建快照。git status --short 输出：' -ForegroundColor Red
    foreach ($line in $dirty) { Write-Host $line }
    exit 1
}

# ---- 标签不能已存在 ----
$tagName = 'v' + $Version
$tagRef = 'refs/tags/' + $tagName
& git -C $repoRoot rev-parse -q --verify $tagRef 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    Write-Host "错误：标签 $tagName 已存在，拒绝覆盖。" -ForegroundColor Red
    exit 1
}

# ---- 创建带注释的标签 ----
& git -C $repoRoot tag -a $tagName -m "L-Mechrevo $Version corresponding source snapshot"
if ($LASTEXITCODE -ne 0) {
    Write-Host "错误：创建标签 $tagName 失败。" -ForegroundColor Red
    exit 1
}

# ---- 导出干净归档 ----
if (-not (Test-Path -LiteralPath $archiveDir)) {
    New-Item -ItemType Directory -Path $archiveDir -Force | Out-Null
}
$fileName = 'L-Mechrevo-' + $Version + '-source.zip'
$archivePath = Join-Path $archiveDir $fileName

& git -C $repoRoot archive --format=zip -o $archivePath $tagName
if ($LASTEXITCODE -ne 0) {
    Write-Host '错误：导出归档失败，正在回滚本次创建的标签。' -ForegroundColor Red
    & git -C $repoRoot tag -d $tagName | Out-Null
    exit 1
}

$item = Get-Item -LiteralPath $archivePath
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
$sizeMB = [math]::Round($item.Length / 1MB, 2)

Write-Host ''
Write-Host '源码快照已生成：' -ForegroundColor Green
Write-Host "  版本标签 : $tagName"
Write-Host "  归档路径 : $($item.FullName)"
Write-Host ("  归档大小 : {0:N0} 字节 ({1} MB)" -f $item.Length, $sizeMB)
Write-Host "  SHA256   : $hash"
