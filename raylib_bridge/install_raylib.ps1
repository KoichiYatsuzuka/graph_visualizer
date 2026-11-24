<#
.SYNOPSIS
    raylib の公式ビルド済み配布物（Windows x64 / MSVC 用）をダウンロードして third_party/raylib/ に置く。

.DESCRIPTION
    raylib 5.5 の raylib-5.5_win64_msvc16.zip を GitHub Releases から取得し、SHA-256 を照合してから
    include/（raylib.h・rlgl.h・raymath.h）・lib/（raylib.lib など）・LICENSE を
    <リポジトリ>/third_party/raylib/ へ展開する。raylib_bridge.dll は raylib.lib を静的にリンクするので、
    実行時に raylib.dll は要らない。
    既に third_party/raylib/include/raylib.h があれば何もしない（-Force で入れ直す）。

.PARAMETER Force
    既存の third_party/raylib/ を消して入れ直す。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File raylib_bridge/install_raylib.ps1
#>
param(
    [switch]$Force
)
$ErrorActionPreference = 'Stop'

# 版を上げるときは 3 つとも書き換える（ハッシュは公式 zip の SHA-256）。
$RaylibVersion = '5.5'
$RaylibZipUrl = 'https://github.com/raysan5/raylib/releases/download/5.5/raylib-5.5_win64_msvc16.zip'
$RaylibZipSha256 = '8D046084D12353183E701EF4C9D276C21FCD3243C2A368091FABFB2769B8507C'

<#
.SYNOPSIS
    リポジトリの third_party/raylib/ の絶対パスを返す。
.EXAMPLE
    $installDir = Get-RaylibInstallDir   # C:\...\graph_visualizer\third_party\raylib
#>
function Get-RaylibInstallDir {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    return Join-Path $repoRoot 'third_party\raylib'
}

<#
.SYNOPSIS
    URL のファイルを保存先へダウンロードし、SHA-256 が期待値と違えば例外を投げる。
.PARAMETER Url
    ダウンロードする URL。
.PARAMETER DestinationPath
    保存先のファイルパス（上書きされる）。
.PARAMETER ExpectedSha256
    期待する SHA-256（16 進・大文字小文字は問わない）。
.EXAMPLE
    Save-VerifiedDownload -Url $RaylibZipUrl -DestinationPath $zipPath -ExpectedSha256 $RaylibZipSha256
#>
function Save-VerifiedDownload {
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$DestinationPath,
        [Parameter(Mandatory)][string]$ExpectedSha256
    )
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $previousProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'   # 進捗表示があると Windows PowerShell 5.1 の IWR は非常に遅い
    try {
        Invoke-WebRequest -Uri $Url -OutFile $DestinationPath -UseBasicParsing
    }
    finally {
        $ProgressPreference = $previousProgress
    }
    $actualSha256 = (Get-FileHash -Algorithm SHA256 -Path $DestinationPath).Hash
    if ($actualSha256 -ne $ExpectedSha256.ToUpperInvariant()) {
        throw "SHA-256 mismatch for $Url`n  expected: $ExpectedSha256`n  actual:   $actualSha256"
    }
}

<#
.SYNOPSIS
    raylib の zip を展開し、include/・lib/・LICENSE だけをインストール先へ写す。
.PARAMETER ZipPath
    raylib-<版>_win64_msvc16.zip のパス。
.PARAMETER InstallDir
    インストール先（third_party/raylib）。中身は置き換えられる。
.EXAMPLE
    Expand-RaylibArchive -ZipPath $zipPath -InstallDir (Get-RaylibInstallDir)
#>
function Expand-RaylibArchive {
    param(
        [Parameter(Mandatory)][string]$ZipPath,
        [Parameter(Mandatory)][string]$InstallDir
    )
    $extractDir = Join-Path ([IO.Path]::GetTempPath()) ("raylib_extract_" + [Guid]::NewGuid().ToString('N'))
    try {
        Expand-Archive -Path $ZipPath -DestinationPath $extractDir
        # zip の中は raylib-5.5_win64_msvc16/{include,lib,LICENSE,...} の 1 階層
        $packageRoot = Get-ChildItem -Path $extractDir -Directory | Select-Object -First 1
        if ($null -eq $packageRoot -or -not (Test-Path (Join-Path $packageRoot.FullName 'include\raylib.h'))) {
            throw "unexpected archive layout: include/raylib.h not found in $ZipPath"
        }
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
        foreach ($entryName in @('include', 'lib', 'LICENSE')) {
            Copy-Item -Recurse -Force -Path (Join-Path $packageRoot.FullName $entryName) -Destination $InstallDir
        }
    }
    finally {
        Remove-Item -Recurse -Force -Path $extractDir -ErrorAction SilentlyContinue
    }
}

$installDir = Get-RaylibInstallDir
$installedHeader = Join-Path $installDir 'include\raylib.h'
if ((Test-Path $installedHeader) -and -not $Force) {
    Write-Host "raylib is already installed: $installDir (use -Force to reinstall)"
    exit 0
}
if (Test-Path $installDir) {
    Remove-Item -Recurse -Force -Path $installDir
}

$zipPath = Join-Path ([IO.Path]::GetTempPath()) "raylib-$RaylibVersion-win64-msvc16.zip"
try {
    Write-Host "downloading raylib $RaylibVersion ..."
    Save-VerifiedDownload -Url $RaylibZipUrl -DestinationPath $zipPath -ExpectedSha256 $RaylibZipSha256
    Expand-RaylibArchive -ZipPath $zipPath -InstallDir $installDir
}
finally {
    Remove-Item -Force -Path $zipPath -ErrorAction SilentlyContinue
}
Write-Host "OK: raylib $RaylibVersion installed to $installDir"
