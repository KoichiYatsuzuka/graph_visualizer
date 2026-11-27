<#
.SYNOPSIS
    SliderPanelBridge（C# の WinForms のスライダーの窓）をビルドし、Arrow の import[cs-dll] が読む 2 つの DLL を
    slider_panel/ に置く。

.DESCRIPTION
    Arrow の import[cs-dll] slider_panel.SliderPanelBridge は次の 2 つを読む（docs/grammar/09_imports.md の「cs-dll」）:
      slider_panel/SliderPanelBridge.dll         マネージド DLL（.NET のメタデータから Arrow の型スタブを作る）
      slider_panel/SliderPanelBridge_native.dll  NativeAOT の DLL（実行時に Arrow が呼ぶ export がある）
    手順:
      1. dotnet build（ArrowExports.g.cs を外す: -p:ArrowGenerated=false）でマネージド DLL を作る
      2. arrow --gen-cs-exports でそのメタデータから ArrowExports.g.cs（Arrow が呼ぶ export）を作る
      3. dotnet publish（NativeAOT・生成した export を含む）で NativeAOT の DLL を作る
      4. 2 つの DLL を slider_panel/ へ写す
    前提: .NET SDK 10 以降、Visual Studio 2022（Build Tools 可）の C++ ワークロード（NativeAOT のリンクに使う）、
    arrow（PATH か -ArrowPath）、Arrow のソース（test_lang）の bridge/ptr_protocol/ArPtrProtocol.cs
    （生成した export が参照するが Arrow の配布物に入っていない）。
    初回は NuGet から NativeAOT のコンパイラとランタイムのパッケージを取ってくる。

.PARAMETER ArrowPath
    arrow.exe のパス。省略すると PATH の arrow を使う。

.PARAMETER ArPtrProtocolPath
    Arrow のソースの bridge/ptr_protocol/ArPtrProtocol.cs のパス。省略すると、このリポジトリと同じ階層にある
    test_lang/bridge/ptr_protocol/ArPtrProtocol.cs を使う。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File slider_panel/build.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File slider_panel/build.ps1 -ArPtrProtocolPath D:\src\test_lang\bridge\ptr_protocol\ArPtrProtocol.cs
#>
param(
    [string]$ArrowPath = '',
    [string]$ArPtrProtocolPath = ''
)
$ErrorActionPreference = 'Stop'

$ProjectName = 'SliderPanelBridge'
$TargetFramework = 'net10.0-windows'   # csproj の TargetFramework と同じにする
$RuntimeIdentifier = 'win-x64'

<#
.SYNOPSIS
    arrow.exe のパスを返す。見つからなければ例外を投げる。
.PARAMETER RequestedPath
    利用者が指定したパス（空文字なら PATH から探す）。
.EXAMPLE
    $arrow = Resolve-ArrowPath -RequestedPath ''
#>
function Resolve-ArrowPath {
    param([string]$RequestedPath)
    if ($RequestedPath -ne '') {
        if (-not (Test-Path $RequestedPath)) { throw "arrow not found: $RequestedPath" }
        return $RequestedPath
    }
    $command = Get-Command arrow -ErrorAction SilentlyContinue
    if ($null -eq $command) { throw "arrow is not on PATH. Pass -ArrowPath." }
    return $command.Source
}

<#
.SYNOPSIS
    Arrow のソースの ArPtrProtocol.cs の絶対パスを返す。見つからなければ例外を投げる。
.PARAMETER RequestedPath
    利用者が指定したパス（空文字なら、このリポジトリと同じ階層の test_lang のものを探す）。
.PARAMETER RepoRoot
    このリポジトリ（graph_visualizer）のルートの絶対パス。
.EXAMPLE
    $ptrProtocol = Resolve-ArPtrProtocolPath -RequestedPath '' -RepoRoot (Split-Path -Parent $PSScriptRoot)
#>
function Resolve-ArPtrProtocolPath {
    param(
        [string]$RequestedPath,
        [Parameter(Mandatory)][string]$RepoRoot
    )
    $candidatePath = $RequestedPath
    if ($candidatePath -eq '') {
        $candidatePath = Join-Path (Split-Path -Parent $RepoRoot) 'test_lang\bridge\ptr_protocol\ArPtrProtocol.cs'
    }
    if (-not (Test-Path $candidatePath)) {
        throw "ArPtrProtocol.cs not found: $candidatePath. Pass -ArPtrProtocolPath <Arrow source>\bridge\ptr_protocol\ArPtrProtocol.cs."
    }
    return (Resolve-Path $candidatePath).Path
}

<#
.SYNOPSIS
    外部のコマンドを実行し、終了コードが 0 でなければ例外を投げる（出力はそのまま表示する）。
.DESCRIPTION
    PowerShell 5.1 は $ErrorActionPreference = 'Stop' のとき、外部のコマンドが stderr に書いただけで止まるので、
    この関数の中だけ Continue にして終了コードで判定する。
.PARAMETER FilePath
    実行するコマンド（dotnet・arrow.exe のパスなど）。
.PARAMETER Arguments
    コマンドの引数。
.PARAMETER Description
    失敗したときのメッセージに出す説明。
.EXAMPLE
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments @('--info') -Description 'dotnet --info'
#>
function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$Description
    )
    $ErrorActionPreference = 'Continue'
    & $FilePath @Arguments 2>&1 | ForEach-Object { "$_" } | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Description failed (exit $LASTEXITCODE)" }
}

<#
.SYNOPSIS
    SliderPanelBridge をビルドし、マネージド DLL と NativeAOT の DLL を ProjectDir に置く。
.PARAMETER ProjectDir
    slider_panel/ の絶対パス（.csproj があり、DLL の置き場所）。
.PARAMETER Arrow
    arrow.exe のパス（ArrowExports.g.cs の生成に使う）。
.PARAMETER PtrProtocolSource
    ArPtrProtocol.cs の絶対パス（csproj の ArPtrProtocolSource に渡す）。
.EXAMPLE
    Invoke-SliderPanelBuild -ProjectDir $PSScriptRoot -Arrow $arrow -PtrProtocolSource $ptrProtocol
#>
function Invoke-SliderPanelBuild {
    param(
        [Parameter(Mandatory)][string]$ProjectDir,
        [Parameter(Mandatory)][string]$Arrow,
        [Parameter(Mandatory)][string]$PtrProtocolSource
    )
    $projectFile = Join-Path $ProjectDir "$ProjectName.csproj"
    $outputDir = Join-Path $ProjectDir "bin\Release\$TargetFramework\$RuntimeIdentifier"
    $managedDll = Join-Path $outputDir "$ProjectName.dll"
    $nativeDll = Join-Path $outputDir "publish\$ProjectName.dll"
    $generatedExports = Join-Path $ProjectDir 'ArrowExports.g.cs'
    $ptrProtocolProperty = "-p:ArPtrProtocolSource=$PtrProtocolSource"

    Write-Host "=== 1/4 dotnet build (without the generated exports)" -ForegroundColor Cyan
    Invoke-CheckedCommand -FilePath 'dotnet' -Description 'dotnet build' -Arguments @(
        'build', $projectFile, '-r', $RuntimeIdentifier, '-c', 'Release', '-p:ArrowGenerated=false', $ptrProtocolProperty, '-nologo')
    if (-not (Test-Path $managedDll)) { throw "managed DLL not found: $managedDll" }

    Write-Host "=== 2/4 arrow --gen-cs-exports" -ForegroundColor Cyan
    Invoke-CheckedCommand -FilePath $Arrow -Description 'arrow --gen-cs-exports' -Arguments @(
        '--gen-cs-exports', $managedDll, '-o', $generatedExports)

    Write-Host "=== 3/4 dotnet publish (NativeAOT)" -ForegroundColor Cyan
    Invoke-CheckedCommand -FilePath 'dotnet' -Description 'dotnet publish' -Arguments @(
        'publish', $projectFile, '-r', $RuntimeIdentifier, '-c', 'Release', '--self-contained', $ptrProtocolProperty, '-nologo')
    if (-not (Test-Path $nativeDll)) { throw "NativeAOT DLL not found: $nativeDll" }

    Write-Host "=== 4/4 copy the DLLs to $ProjectDir" -ForegroundColor Cyan
    Copy-Item -Force -Path $nativeDll -Destination (Join-Path $ProjectDir "${ProjectName}_native.dll")
    Copy-Item -Force -Path $managedDll -Destination (Join-Path $ProjectDir "$ProjectName.dll")
}

# NuGet の初回のあいさつ文を出さない（このプロセスの中だけ）。vcvarsall.bat などが PATH の vswhere.exe を呼ぶので足す。
$env:DOTNET_NOLOGO = '1'
$vswhereDir = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer'
$env:PATH = "$vswhereDir;$env:PATH"

$arrow = Resolve-ArrowPath -RequestedPath $ArrowPath
$ptrProtocol = Resolve-ArPtrProtocolPath -RequestedPath $ArPtrProtocolPath -RepoRoot (Split-Path -Parent $PSScriptRoot)
Invoke-SliderPanelBuild -ProjectDir $PSScriptRoot -Arrow $arrow -PtrProtocolSource $ptrProtocol
Write-Host "OK: $(Join-Path $PSScriptRoot "${ProjectName}.dll"), $(Join-Path $PSScriptRoot "${ProjectName}_native.dll")"
