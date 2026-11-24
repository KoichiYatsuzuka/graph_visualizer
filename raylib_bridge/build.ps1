<#
.SYNOPSIS
    raylib_bridge.cpp を MSVC でビルドし、raylib_bridge/raylib_bridge.dll を作る。

.DESCRIPTION
    Arrow の import[cpp-dll] raylib_bridge.raylib_bridge は、ヘッダ raylib_bridge.h と同じディレクトリの
    raylib_bridge.dll を読む。この DLL は third_party/raylib/lib/raylib.lib（/MD 版）を静的にリンクするので、
    raylib.dll は要らない。中間ファイル（.obj・.lib・.exp）は raylib_bridge/build/ に置く。
    raylib が未インストールなら先に install_raylib.ps1 を走らせる。

.PARAMETER VcvarsallPath
    vcvarsall.bat のパス。省略すると vswhere で C++ ビルドツールの入った Visual Studio を探す。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File raylib_bridge/build.ps1
#>
param(
    [string]$VcvarsallPath = ''
)
$ErrorActionPreference = 'Stop'

<#
.SYNOPSIS
    vcvarsall.bat のパスを返す。見つからなければ例外を投げる。
.PARAMETER RequestedPath
    利用者が指定したパス（空文字なら vswhere で探す）。
.EXAMPLE
    $vcvarsall = Resolve-VcvarsallPath -RequestedPath ''
#>
function Resolve-VcvarsallPath {
    param([string]$RequestedPath)
    if ($RequestedPath -ne '') {
        if (-not (Test-Path $RequestedPath)) { throw "vcvarsall.bat not found: $RequestedPath" }
        return $RequestedPath
    }
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswherePath)) {
        throw "vswhere.exe not found. Install Visual Studio (Build Tools) with the C++ workload, or pass -VcvarsallPath."
    }
    $installationPath = & $vswherePath -latest -products * `
        -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $installationPath) {
        throw "no Visual Studio with the C++ x64 tools was found. Pass -VcvarsallPath."
    }
    $foundPath = Join-Path $installationPath 'VC\Auxiliary\Build\vcvarsall.bat'
    if (-not (Test-Path $foundPath)) { throw "vcvarsall.bat not found: $foundPath" }
    return $foundPath
}

<#
.SYNOPSIS
    MSVC の cl で raylib_bridge.dll をビルドする。失敗したら例外を投げる。
.PARAMETER Vcvarsall
    vcvarsall.bat のパス。
.PARAMETER BridgeDir
    raylib_bridge/ の絶対パス（.cpp・.h・.def があり、.dll の出力先）。
.PARAMETER RaylibDir
    third_party/raylib/ の絶対パス（include/・lib/ がある）。
.EXAMPLE
    Invoke-BridgeBuild -Vcvarsall $vcvarsall -BridgeDir $PSScriptRoot -RaylibDir $raylibDir
#>
function Invoke-BridgeBuild {
    param(
        [Parameter(Mandatory)][string]$Vcvarsall,
        [Parameter(Mandatory)][string]$BridgeDir,
        [Parameter(Mandatory)][string]$RaylibDir
    )
    $buildDir = Join-Path $BridgeDir 'build'
    New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
    # /MD: raylib.lib（公式の msvc16 版）は MSVCRT（/MD）でビルドされている。
    # GLFW（raylib.lib に同梱）が使う Windows のライブラリを末尾で足す。
    # vcvarsall.bat は PATH の vswhere.exe を呼ぶので、Installer のディレクトリを PATH に足しておく
    # （無いと「'vswhere.exe' is not recognized」が出る。ビルドには影響しない）。
    $vswhereDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
    $buildBatch = @"
@echo off
set "PATH=$vswhereDir;%PATH%"
call "$Vcvarsall" x64 >nul
if errorlevel 1 exit /b 1
cd /d "$BridgeDir"
cl /nologo /std:c++17 /O2 /MD /EHsc /W4 /utf-8 /I"$RaylibDir\include" /Fo"$buildDir\\" raylib_bridge.cpp /LD /Fe"$BridgeDir\raylib_bridge.dll" /link /DEF:raylib_bridge.def /IMPLIB:"$buildDir\raylib_bridge.lib" /LIBPATH:"$RaylibDir\lib" raylib.lib opengl32.lib gdi32.lib winmm.lib user32.lib shell32.lib
if errorlevel 1 exit /b 1
"@
    $batchPath = Join-Path ([IO.Path]::GetTempPath()) ("build_raylib_bridge_" + [Guid]::NewGuid().ToString('N') + '.bat')
    try {
        Set-Content -Path $batchPath -Value $buildBatch -Encoding ascii
        & cmd /c $batchPath
        if ($LASTEXITCODE -ne 0) { throw "raylib_bridge build failed (exit $LASTEXITCODE)" }
    }
    finally {
        Remove-Item -Force -Path $batchPath -ErrorAction SilentlyContinue
    }
}

$bridgeDir = $PSScriptRoot
$raylibDir = Join-Path (Split-Path -Parent $bridgeDir) 'third_party\raylib'
if (-not (Test-Path (Join-Path $raylibDir 'lib\raylib.lib'))) {
    throw "raylib is not installed in $raylibDir. Run raylib_bridge/install_raylib.ps1 first."
}
$vcvarsall = Resolve-VcvarsallPath -RequestedPath $VcvarsallPath
Invoke-BridgeBuild -Vcvarsall $vcvarsall -BridgeDir $bridgeDir -RaylibDir $raylibDir
Write-Host "OK: $(Join-Path $bridgeDir 'raylib_bridge.dll')"
