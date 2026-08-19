[CmdletBinding(SupportsShouldProcess)]
param(
    [switch]$AllowIncompleteRouterCleanup
)

$ErrorActionPreference = 'Stop'

function Get-MetadataBoolean($Metadata, [string]$Name) {
    if ($null -eq $Metadata) { return $false }
    $property = $Metadata.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $false }
    try { return [Convert]::ToBoolean($property.Value) } catch { return $false }
}

function Get-ServiceExecutablePath($ServiceInfo) {
    if ($null -eq $ServiceInfo -or [string]::IsNullOrWhiteSpace([string]$ServiceInfo.PathName)) { return $null }
    $raw = [string]$ServiceInfo.PathName
    if ($raw -match '^\s*"([^"]+)"') { return [IO.Path]::GetFullPath($matches[1]) }
    if ($raw -match '^\s*(.+?\.exe)(?:\s|$)') { return [IO.Path]::GetFullPath($matches[1]) }
    return [IO.Path]::GetFullPath(($raw.Trim() -split '\s+', 2)[0])
}

function Assert-OwnedShortcut([string]$Path, [string]$ExpectedTarget) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($Path)
    if ([string]::IsNullOrWhiteSpace([string]$shortcut.TargetPath) -or
        -not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($shortcut.TargetPath), [IO.Path]::GetFullPath($ExpectedTarget))) {
        throw "Skrót nie należy do WorkRouter i nie zostanie usunięty: $Path"
    }
}

function Remove-OwnedShortcut([string]$Path, [string]$ExpectedTarget) {
    Assert-OwnedShortcut $Path $ExpectedTarget
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    Remove-Item -LiteralPath $Path -Force
}
$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Deinstalator wymaga uruchomienia PowerShell jako administrator.'
}

$serviceName = 'WorkRouter'
$installPath = Join-Path $env:ProgramFiles 'WorkRouter'
$statePath = Join-Path $env:ProgramData 'WorkRouter'
$endpointPath = Join-Path $statePath 'endpoint.json'
$metadataPath = Join-Path $statePath 'installation.json'
$serviceExe = Join-Path $installPath 'WorkRouter.Service.exe'
$launcherExe = Join-Path $installPath 'WorkRouter.Launcher.exe'
$metadata = $null
if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
    try { $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json }
    catch { throw 'Nieprawidłowy installation.json; odmowa odinstalowania bez wiarygodnych danych własności.' }
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$serviceInfo = if ($service) { Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction Stop } else { $null }
if ($serviceInfo) {
    $configuredExecutable = Get-ServiceExecutablePath $serviceInfo
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($configuredExecutable, [IO.Path]::GetFullPath($serviceExe))) {
        throw "Usługa WorkRouter wskazuje nieoczekiwany plik i nie zostanie usunięta: $configuredExecutable"
    }
}

$shareOwned = Get-MetadataBoolean $metadata 'ShareOwned'
$accountOwned = Get-MetadataBoolean $metadata 'AccountOwned'
$shareName = if ($metadata -and $metadata.PSObject.Properties['ShareName']) { [string]$metadata.ShareName } else { 'Firmowe' }
$sharePath = if ($metadata -and $metadata.PSObject.Properties['SharePath']) { [string]$metadata.SharePath } else { 'E:\Firmowe' }
$accountName = if ($metadata -and $metadata.PSObject.Properties['AccountName']) { [string]$metadata.AccountName } else { 'workshare' }
if (-not [StringComparer]::OrdinalIgnoreCase.Equals($shareName, 'Firmowe') -or
    -not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath($sharePath), [IO.Path]::GetFullPath('E:\Firmowe')) -or
    -not [StringComparer]::OrdinalIgnoreCase.Equals($accountName, 'workshare')) {
    throw 'Metadane własności udziału lub konta są niezgodne z kontraktem WorkRouter.'
}
if ($shareOwned) {
    $ownedShare = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
    if ($ownedShare -and -not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath([string]$ownedShare.Path), [IO.Path]::GetFullPath($sharePath))) {
        throw 'Udział Firmowe wskazuje inny katalog i nie zostanie usunięty.'
    }
}

$programsShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'WorkRouter.lnk'
$startupShortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'WorkRouter.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'WorkRouter.lnk'
if ($service -or (Test-Path -LiteralPath $statePath)) {
    foreach ($shortcutPath in @($programsShortcut, $startupShortcut, $desktopShortcut)) {
        Assert-OwnedShortcut $shortcutPath $launcherExe
    }
}

$targetDescription = "WorkRouter ($installPath, $statePath, usługa, udział SMB i konto workshare)"
if (-not $PSCmdlet.ShouldProcess($targetDescription, 'Pełne odinstalowanie')) {
    return
}

$routerStoppedCleanly = $false
$stateExists = Test-Path -LiteralPath $statePath
if (Test-Path -LiteralPath $endpointPath) {
    try {
        $endpoint = Get-Content -LiteralPath $endpointPath -Raw | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace([string]$endpoint.Url) -or [string]::IsNullOrWhiteSpace([string]$endpoint.Token)) {
            throw 'endpoint.json nie zawiera kompletnego adresu/tokenu.'
        }
        $result = Invoke-RestMethod -Method Post -Uri ($endpoint.Url.TrimEnd('/') + '/api/router/stop') -Headers @{ 'X-WorkRouter-Token' = $endpoint.Token } -TimeoutSec 15
        $routerStoppedCleanly = [bool]$result.success
    }
    catch {
        Write-Warning "Nie udało się zatrzymać routera przez API: $($_.Exception.Message)"
    }
}
elseif ($service -or $stateExists) {
    if (-not $AllowIncompleteRouterCleanup) {
        throw 'Brak endpoint.json przy istniejącej usłudze/stanie; odmowa usunięcia fail-closed. Napraw usługę i ponów próbę albo użyj -AllowIncompleteRouterCleanup.'
    }
    Write-Warning 'Wymuszono niepełne czyszczenie bez potwierdzenia API; persistent WFP może pozostać aktywny.'
    $routerStoppedCleanly = $false
}
else {
    Write-Host 'WorkRouter nie ma usługi ani stanu; nic do usunięcia.'
    return
}

if (-not $routerStoppedCleanly -and -not $AllowIncompleteRouterCleanup) {
    throw 'Nie potwierdzono bezpiecznego usunięcia aktywnej polityki routera.'
}

if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force
        $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Nie udało się usunąć usługi WorkRouter.' }
}

if ($shareOwned) {
    $share = Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue
    if ($share) {
        if (-not [StringComparer]::OrdinalIgnoreCase.Equals([IO.Path]::GetFullPath([string]$share.Path), [IO.Path]::GetFullPath($sharePath))) {
            throw 'Udział Firmowe wskazuje inny katalog i nie zostanie usunięty.'
        }
        Unblock-SmbShareAccess -Name $shareName -AccountName "$env:COMPUTERNAME\$accountName" -Force -ErrorAction SilentlyContinue | Out-Null
        Remove-SmbShare -Name $shareName -Force
    }
}

if ($accountOwned -and (Get-LocalUser -Name $accountName -ErrorAction SilentlyContinue)) {
    Remove-LocalUser -Name $accountName
}

$baseline = Join-Path $statePath 'firmowe-acl-baseline.txt'
if ((Test-Path -LiteralPath $baseline) -and (Test-Path -LiteralPath 'E:\Firmowe')) {
    $sddl = Get-Content -LiteralPath $baseline -Raw
    $acl = Get-Acl -LiteralPath 'E:\Firmowe'
    $acl.SetSecurityDescriptorSddlForm($sddl)
    Set-Acl -LiteralPath 'E:\Firmowe' -AclObject $acl
}

foreach ($shortcutPath in @($programsShortcut, $startupShortcut, $desktopShortcut)) {
    Remove-OwnedShortcut $shortcutPath $launcherExe
}

if (Test-Path -LiteralPath $installPath) { Remove-Item -LiteralPath $installPath -Recurse -Force }
if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -Recurse -Force }

Write-Host 'WorkRouter został odinstalowany. Zawartość E:\Firmowe pozostała nienaruszona.'
