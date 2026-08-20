[CmdletBinding()]
param(
    [string]$PackagePath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\publish'),
    [string]$ZipPath,
    [switch]$ValidateScripts
)

$ErrorActionPreference = 'Stop'

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Path).Path)
}

function Assert-ChildPath([string]$Root, [string]$Path, [string]$Label) {
    $rootFull = (Get-FullPath $Root).TrimEnd('\') + '\'
    $pathFull = Get-FullPath $Path
    if (-not $pathFull.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label is outside the expected root: $pathFull"
    }
    return $pathFull
}

if (-not (Test-Path -LiteralPath $PackagePath -PathType Container)) {
    throw "Release package directory does not exist: $PackagePath"
}
$package = Get-FullPath $PackagePath
$manifestPath = Join-Path $package 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "Missing package manifest: $manifestPath"
}

$entries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in @(Get-Content -LiteralPath $manifestPath -Encoding utf8)) {
    if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
        throw "Malformed manifest entry: $line"
    }
    $hash = $matches[1].ToUpperInvariant()
    $relative = $matches[2].Replace('/', '\')
    if ([IO.Path]::IsPathRooted($relative)) { throw "Absolute path in manifest: $relative" }
    $candidate = Join-Path $package $relative
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Manifest file is missing: $relative" }
    [void](Assert-ChildPath $package $candidate "Manifest path")
    if ($entries.ContainsKey($relative)) { throw "Duplicate manifest entry: $relative" }
    $entries[$relative] = $hash
    $actual = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $hash) { throw "SHA-256 mismatch: $relative" }
}

$actualFiles = @(Get-ChildItem -LiteralPath $package -File -Recurse | Where-Object { $_.Name -ne 'SHA256SUMS.txt' })
if ($actualFiles.Count -ne $entries.Count) {
    throw "Manifest file count mismatch: actual=$($actualFiles.Count), manifest=$($entries.Count)"
}
foreach ($required in @('WorkRouter.Service.exe', 'WorkRouter.Launcher.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $package $required) -PathType Leaf)) {
        throw "Required release binary is missing: $required"
    }
}

if ($ValidateScripts) {
    $repositoryRoot = Split-Path -Parent $PSScriptRoot
    $scripts = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'scripts') -Filter '*.ps1' -File)
    foreach ($script in $scripts) {
        $tokens = $null
        $errors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$errors)
        if ($errors.Count -gt 0) {
            $details = $errors | ForEach-Object { "$($script.Name):$($_.Extent.StartLineNumber): $($_.Message)" }
            throw ($details -join [Environment]::NewLine)
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($ZipPath)) {
    if (-not (Test-Path -LiteralPath $ZipPath -PathType Leaf)) { throw "ZIP does not exist: $ZipPath" }
    $zip = Get-FullPath $ZipPath
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToUpperInvariant()
    Write-Host "ZIP SHA256  $hash  $([IO.Path]::GetFileName($zip))"
}

Write-Host "Release package verification passed: $package"
