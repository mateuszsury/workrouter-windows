[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path -LiteralPath (Split-Path -Parent $PSScriptRoot)).Path
$artifactsRoot = Join-Path $repoRoot 'artifacts\publish'

if (Test-Path -LiteralPath $artifactsRoot) {
    $resolvedArtifacts = (Resolve-Path -LiteralPath $artifactsRoot).Path
    $expectedArtifacts = [IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts\publish'))
    if (-not [string]::Equals($resolvedArtifacts, $expectedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Niebezpieczna ścieżka czyszczenia artefaktów: $resolvedArtifacts"
    }
    Remove-Item -LiteralPath $resolvedArtifacts -Recurse -Force
}
New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null

dotnet test (Join-Path $repoRoot 'WorkRouter.sln') -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Testy nie przeszły.' }

dotnet publish (Join-Path $repoRoot 'src\WorkRouter.Service\WorkRouter.Service.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=false -o $artifactsRoot
if ($LASTEXITCODE -ne 0) { throw 'Publikacja usługi nie powiodła się.' }

dotnet publish (Join-Path $repoRoot 'src\WorkRouter.Launcher\WorkRouter.Launcher.csproj') -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $artifactsRoot
if ($LASTEXITCODE -ne 0) { throw 'Publikacja launchera nie powiodła się.' }

$artifactPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$hashes = Get-ChildItem -LiteralPath $artifactsRoot -File -Recurse |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object FullName |
    ForEach-Object {
        if (-not $_.FullName.StartsWith($artifactPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Artefakt poza katalogiem publikacji: $($_.FullName)"
        }
        $relative = $_.FullName.Substring($artifactPrefix.Length)
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $relative"
    }
Set-Content -LiteralPath (Join-Path $artifactsRoot 'SHA256SUMS.txt') -Value $hashes -Encoding utf8

Write-Host "Gotowy pakiet: $artifactsRoot"
