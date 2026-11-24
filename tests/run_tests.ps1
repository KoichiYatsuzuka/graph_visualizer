<#
.SYNOPSIS
    tests/test_*.ar を arrow で 1 本ずつ実行し、終了コードで合否を出す。

.DESCRIPTION
    各テストは期待と違えば raise して非 0 で終わるように書く。1 本でも失敗すれば、このスクリプトも
    終了コード 1 で終わる。arrow は PATH から探す（-ArrowPath で指定もできる）。
    raylib_bridge.dll が無ければ、raylib_bridge のテストは失敗する（先に install_raylib.ps1 → build.ps1）。

.PARAMETER ArrowPath
    arrow.exe のパス。省略すると PATH の arrow を使う。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests/run_tests.ps1
#>
param(
    [string]$ArrowPath = ''
)
$ErrorActionPreference = 'Stop'

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
    1 本のテストを実行し、成功なら $true を返す（出力はそのまま表示する）。
.PARAMETER Arrow
    arrow.exe のパス。
.PARAMETER TestFile
    実行する .ar ファイル。
.PARAMETER RepoRoot
    実行中のカレントディレクトリにするリポジトリのルート（どこから呼んでも同じ条件で走らせるため）。
.EXAMPLE
    $passed = Invoke-ArrowTest -Arrow $arrow -TestFile $file -RepoRoot $repoRoot
#>
function Invoke-ArrowTest {
    param(
        [Parameter(Mandatory)][string]$Arrow,
        [Parameter(Mandatory)][IO.FileInfo]$TestFile,
        [Parameter(Mandatory)][string]$RepoRoot
    )
    Write-Host "=== $($TestFile.Name)"
    Push-Location $RepoRoot
    try {
        # Out-Host: テストの出力を関数の戻り値に混ぜない
        & $Arrow $TestFile.FullName | Out-Host
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    if ($exitCode -eq 0) {
        Write-Host "PASS: $($TestFile.Name)"
        return $true
    }
    Write-Host "FAIL: $($TestFile.Name) (exit $exitCode)"
    return $false
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$arrow = Resolve-ArrowPath -RequestedPath $ArrowPath
$testFiles = @(Get-ChildItem -Path $PSScriptRoot -Filter 'test_*.ar' | Sort-Object Name)
if ($testFiles.Count -eq 0) { throw "no tests/test_*.ar found" }

$failedNames = @()
foreach ($testFile in $testFiles) {
    $passed = Invoke-ArrowTest -Arrow $arrow -TestFile $testFile -RepoRoot $repoRoot
    if (-not $passed) { $failedNames += $testFile.Name }
}

Write-Host ""
Write-Host "$($testFiles.Count - $failedNames.Count) / $($testFiles.Count) passed"
if ($failedNames.Count -gt 0) {
    Write-Host "failed: $($failedNames -join ', ')"
    exit 1
}
exit 0
