[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$Source,
    [switch]$Launch
)

$ErrorActionPreference = 'Stop'

function Get-FullPath([string]$Path) {
    return [IO.Path]::GetFullPath($Path)
}

function Assert-ProgramFilesPath([string]$Path, [string]$Label) {
    $root = (Get-FullPath $env:ProgramFiles).TrimEnd('\')
    $full = Get-FullPath $Path
    if (-not $full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Label znajduje się poza katalogiem Program Files: $full"
    }
    return $full
}

function Assert-ExactProgramFilesPath([string]$Path, [string]$Expected, [string]$Label) {
    $full = Assert-ProgramFilesPath $Path $Label
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($full, (Get-FullPath $Expected))) {
        throw "$Label ma nieoczekiwaną ścieżkę: $full"
    }
    return $full
}

function Get-MetadataBoolean($Metadata, [string]$Name, [bool]$Fallback) {
    if ($null -eq $Metadata) { return $Fallback }
    $property = $Metadata.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) { return $Fallback }
    try { return [Convert]::ToBoolean($property.Value) } catch { return $Fallback }
}

function Write-JsonAtomic([string]$Path, $Value) {
    $temporary = "$Path.tmp-$([guid]::NewGuid().ToString('N'))"
    try {
        $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $temporary -Encoding UTF8
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function Stop-ExistingRouterViaApi([string]$EndpointPath) {
    if (-not (Test-Path -LiteralPath $EndpointPath -PathType Leaf)) {
        throw "Brak endpoint.json dla istniejącej usługi; aktualizacja została przerwana fail-closed."
    }
    try {
        $endpoint = Get-Content -LiteralPath $EndpointPath -Raw | ConvertFrom-Json
        if ([string]::IsNullOrWhiteSpace([string]$endpoint.Url) -or [string]::IsNullOrWhiteSpace([string]$endpoint.Token)) {
            throw 'endpoint.json nie zawiera kompletnego adresu/tokenu.'
        }
        $status = Invoke-RestMethod -Method Get -Uri ($endpoint.Url.TrimEnd('/') + '/api/status') -Headers @{ 'X-WorkRouter-Token' = $endpoint.Token } -TimeoutSec 15
        $wasRouterRunning = [bool]$status.routerRunning
        $result = Invoke-RestMethod -Method Post -Uri ($endpoint.Url.TrimEnd('/') + '/api/router/stop') -Headers @{ 'X-WorkRouter-Token' = $endpoint.Token } -TimeoutSec 15
        if (-not [bool]$result.success) {
            throw "API zwróciło niepowodzenie: $($result.message)"
        }
        return $wasRouterRunning
    }
    catch {
        throw "Nie potwierdzono bezpiecznego zatrzymania routera przez autoryzowane API: $($_.Exception.Message)"
    }
}

function Wait-ServiceApi([string]$EndpointPath, [int]$TimeoutSeconds = 30) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        try {
            if (Test-Path -LiteralPath $EndpointPath -PathType Leaf) {
                $endpoint = Get-Content -LiteralPath $EndpointPath -Raw | ConvertFrom-Json
                if (-not [string]::IsNullOrWhiteSpace([string]$endpoint.Url) -and -not [string]::IsNullOrWhiteSpace([string]$endpoint.Token)) {
                    $status = Invoke-RestMethod -Method Get -Uri ($endpoint.Url.TrimEnd('/') + '/api/status') -Headers @{ 'X-WorkRouter-Token' = $endpoint.Token } -TimeoutSec 3
                    if ($null -ne $status.state) { return $endpoint }
                }
            }
        } catch {}
        Start-Sleep -Milliseconds 300
    } while ((Get-Date) -lt $deadline)
    throw 'Usługa WorkRouter nie udostępniła autoryzowanego API w wymaganym czasie.'
}

function Start-RouterViaApi($Endpoint) {
    $result = Invoke-RestMethod -Method Post -Uri ($Endpoint.Url.TrimEnd('/') + '/api/router/start') -Headers @{ 'X-WorkRouter-Token' = $Endpoint.Token } -TimeoutSec 30
    if (-not [bool]$result.success) { throw "Nie udało się przywrócić pracy routera po aktualizacji: $($result.message)" }
}

function Get-ServiceExecutablePath($ServiceInfo) {
    if ($null -eq $ServiceInfo -or [string]::IsNullOrWhiteSpace([string]$ServiceInfo.PathName)) { return $null }
    $raw = [string]$ServiceInfo.PathName
    if ($raw -match '^\s*"([^"]+)"') { return Get-FullPath $matches[1] }
    if ($raw -match '^\s*(.+?\.exe)(?:\s|$)') { return Get-FullPath $matches[1] }
    return Get-FullPath (($raw.Trim() -split '\s+', 2)[0])
}

function New-LauncherShortcut([string]$ShortcutPath, [string]$LauncherPath, [string]$WorkingDirectory) {
    $parent = Split-Path -Parent $ShortcutPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $LauncherPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.Description = 'Sterowanie izolowanym hotspotem WORK'
    $shortcut.Save()
}

if ([string]::IsNullOrWhiteSpace($Source)) {
    $scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
    $Source = Join-Path (Split-Path -Parent $scriptRoot) 'artifacts\publish'
}

$principal = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Instalator wymaga uruchomienia PowerShell jako administrator.'
}

$sourcePath = (Resolve-Path -LiteralPath $Source).Path
$programFilesRoot = (Get-FullPath $env:ProgramFiles).TrimEnd('\')
$installPath = Assert-ExactProgramFilesPath (Join-Path $programFilesRoot 'WorkRouter') (Join-Path $programFilesRoot 'WorkRouter') 'katalog instalacji'
$serviceName = 'WorkRouter'
$serviceExe = Join-Path $installPath 'WorkRouter.Service.exe'
$launcherExe = Join-Path $installPath 'WorkRouter.Launcher.exe'
$statePath = Join-Path $env:ProgramData 'WorkRouter'
$endpointPath = Join-Path $statePath 'endpoint.json'
$metadataPath = Join-Path $statePath 'installation.json'

$manifestPath = Join-Path $sourcePath 'SHA256SUMS.txt'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "Brak manifestu $manifestPath." }
$manifest = @(Get-Content -LiteralPath $manifestPath -Encoding utf8)
$manifestFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($line in $manifest) {
    if ($line -notmatch '^([0-9A-F]{64})  (.+)$') { throw "Nieprawidłowy wpis manifestu: $line" }
    $expectedHash = $matches[1]
    $relativePath = $matches[2].Replace('/', '\')
    if ([IO.Path]::IsPathRooted($relativePath)) { throw "Manifest zawiera ścieżkę absolutną: $relativePath" }
    $candidate = Get-FullPath (Join-Path $sourcePath $relativePath)
    $sourceRoot = (Get-FullPath $sourcePath).TrimEnd('\') + '\'
    if (-not $candidate.StartsWith($sourceRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "Manifest wychodzi poza pakiet: $relativePath" }
    if (-not $manifestFiles.Add($relativePath)) { throw "Powielony wpis manifestu: $relativePath" }
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) { throw "Brak pliku pakietu: $relativePath" }
    if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ne $expectedHash) {
        throw "Niezgodna suma SHA-256: $relativePath"
    }
}
$actualFiles = @(Get-ChildItem -LiteralPath $sourcePath -File -Recurse | Where-Object { $_.Name -ne 'SHA256SUMS.txt' })
if ($actualFiles.Count -ne $manifestFiles.Count) {
    throw "Liczba plików pakietu nie zgadza się z manifestem: $($actualFiles.Count)/$($manifestFiles.Count)."
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$existingServiceInfo = if ($existing) { Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue } else { $null }
if ($existingServiceInfo) {
    $configuredExecutable = Get-ServiceExecutablePath $existingServiceInfo
    if ($configuredExecutable -and -not [StringComparer]::OrdinalIgnoreCase.Equals($configuredExecutable, (Get-FullPath $serviceExe))) {
        throw "Istniejąca usługa WorkRouter wskazuje poza bieżącą instalację: $configuredExecutable"
    }
}

$previousMetadataRaw = $null
$previousMetadata = $null
if (Test-Path -LiteralPath $metadataPath -PathType Leaf) {
    $previousMetadataRaw = Get-Content -LiteralPath $metadataPath -Raw
    try { $previousMetadata = $previousMetadataRaw | ConvertFrom-Json } catch { throw 'Nieprawidłowy installation.json; aktualizacja została przerwana.' }
}
$isUpgrade = ($null -ne $existing) -or ($null -ne $previousMetadata)
$preexistingShare = Get-SmbShare -Name 'Firmowe' -ErrorAction SilentlyContinue
$preexistingAccount = Get-LocalUser -Name 'workshare' -ErrorAction SilentlyContinue
$legacyShareOwned = $null -ne $existing -and $null -ne $preexistingShare -and [StringComparer]::OrdinalIgnoreCase.Equals([string]$preexistingShare.Path, 'E:\Firmowe')
$legacyAccountOwned = $null -ne $existing -and $null -ne $preexistingAccount
$shareOwned = if ($isUpgrade) { Get-MetadataBoolean $previousMetadata 'ShareOwned' $legacyShareOwned } else { $null -eq $preexistingShare }
$accountOwned = if ($isUpgrade) { Get-MetadataBoolean $previousMetadata 'AccountOwned' $legacyAccountOwned } else { $null -eq $preexistingAccount }

$targetDescription = "WorkRouter ($installPath, $statePath, usługa, pakiet binarny i skróty)"
if (-not $PSCmdlet.ShouldProcess($targetDescription, 'Zainstaluj lub zaktualizuj')) { return }

$stagePath = Join-Path $programFilesRoot "WorkRouter.stage-$([guid]::NewGuid().ToString('N'))"
$backupPath = Join-Path $programFilesRoot "WorkRouter.backup-$([guid]::NewGuid().ToString('N'))"
Assert-ProgramFilesPath $stagePath 'katalog stagingu' | Out-Null
Assert-ProgramFilesPath $backupPath 'katalog kopii zapasowej' | Out-Null
$wasServiceRunning = $null -ne $existing -and $existing.Status -eq 'Running'
$serviceCreated = $false
$swapCompleted = $false
$stageCreated = $false
$backupCreated = $false
$metadataWritten = $false
$wasRouterRunning = $false

try {
    if ($existing) {
        $wasRouterRunning = Stop-ExistingRouterViaApi $endpointPath
        $service = Get-Service -Name $serviceName -ErrorAction Stop
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force
            $service.WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
        }
        $serviceProcessId = [int]((Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue).ProcessId)
        if ($serviceProcessId) {
            $processDeadline = (Get-Date).AddSeconds(30)
            while ((Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue) -and (Get-Date) -lt $processDeadline) { Start-Sleep -Milliseconds 200 }
            if (Get-Process -Id $serviceProcessId -ErrorAction SilentlyContinue) { throw "Proces usługi WorkRouter ($serviceProcessId) nie zakończył się w wymaganym czasie." }
        }
    }

    $oldLauncher = Join-Path $installPath 'WorkRouter.Launcher.exe'
    $launcherProcesses = @(Get-CimInstance Win32_Process -Filter "Name='WorkRouter.Launcher.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ExecutablePath -and [StringComparer]::OrdinalIgnoreCase.Equals((Get-FullPath $_.ExecutablePath), (Get-FullPath $oldLauncher)) })
    foreach ($launcherProcess in $launcherProcesses) {
        Stop-Process -Id $launcherProcess.ProcessId -Force -ErrorAction Stop
        Wait-Process -Id $launcherProcess.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
    }

    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    $stageCreated = $true
    foreach ($entry in @(Get-ChildItem -LiteralPath $sourcePath -Force)) {
        Copy-Item -LiteralPath $entry.FullName -Destination (Join-Path $stagePath $entry.Name) -Recurse -Force
    }
    $stagedServiceExe = Join-Path $stagePath 'WorkRouter.Service.exe'
    $stagedLauncherExe = Join-Path $stagePath 'WorkRouter.Launcher.exe'
    if (-not (Test-Path -LiteralPath $stagedServiceExe -PathType Leaf) -or -not (Test-Path -LiteralPath $stagedLauncherExe -PathType Leaf)) {
        throw 'Pakiet stagingu nie zawiera wymaganych plików wykonywalnych.'
    }

    if (Test-Path -LiteralPath $installPath) {
        Move-Item -LiteralPath $installPath -Destination $backupPath
        $backupCreated = $true
    }
    Move-Item -LiteralPath $stagePath -Destination $installPath
    $swapCompleted = $true
    $stageCreated = $false

    New-Item -ItemType Directory -Path $statePath -Force | Out-Null
    $currentSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $aclRules = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F', "*$currentSid`:(OI)(CI)RX")
    & icacls.exe $statePath /inheritance:r /grant:r $aclRules /C | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Nie udało się zabezpieczyć $statePath." }
    foreach ($stateChild in @(Get-ChildItem -LiteralPath $statePath -Force -Recurse -ErrorAction Stop)) {
        if ($stateChild.PSIsContainer) {
            & icacls.exe $stateChild.FullName /inheritance:r /grant:r $aclRules /C | Out-Null
        } else {
            $fileAclRules = @('*S-1-5-18:F', '*S-1-5-32-544:F', "*$currentSid`:RX")
            & icacls.exe $stateChild.FullName /inheritance:r /grant:r $fileAclRules /C | Out-Null
        }
        if ($LASTEXITCODE -ne 0) { throw "Nie udało się zabezpieczyć ACL dla $($stateChild.FullName)." }
    }

    $startupDirectory = [Environment]::GetFolderPath('Startup')
    $programs = [Environment]::GetFolderPath('Programs')
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    $menuShortcutPath = Join-Path $programs 'WorkRouter.lnk'
    $startupShortcutPath = Join-Path $startupDirectory 'WorkRouter.lnk'
    $desktopShortcutPath = Join-Path $desktop 'WorkRouter.lnk'
    New-LauncherShortcut $menuShortcutPath (Join-Path $installPath 'WorkRouter.Launcher.exe') $installPath
    New-LauncherShortcut $desktopShortcutPath (Join-Path $installPath 'WorkRouter.Launcher.exe') $installPath

    $metadata = [ordered]@{}
    if ($previousMetadata) { foreach ($property in $previousMetadata.PSObject.Properties) { $metadata[$property.Name] = $property.Value } }
    $metadata['LauncherPath'] = (Join-Path $installPath 'WorkRouter.Launcher.exe')
    $metadata['MenuShortcutPath'] = $menuShortcutPath
    $metadata['StartupShortcutPath'] = $startupShortcutPath
    $metadata['DesktopShortcutPath'] = $desktopShortcutPath
    $metadata['InstalledForSid'] = $currentSid
    $metadata['ShareName'] = 'Firmowe'
    $metadata['SharePath'] = 'E:\Firmowe'
    $metadata['ShareOwned'] = $shareOwned
    $metadata['AccountName'] = 'workshare'
    $metadata['AccountOwned'] = $accountOwned
    Write-JsonAtomic $metadataPath $metadata
    $metadataWritten = $true

    if (-not $existing) {
        & sc.exe create $serviceName binPath= "`"$serviceExe`"" start= delayed-auto DisplayName= 'WorkRouter Isolation Service' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Nie udało się utworzyć usługi WorkRouter.' }
        $serviceCreated = $true
    }
    & sc.exe description $serviceName 'Fail-closed isolated Windows Mobile Hotspot, WFP policy and SMB share controller.' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Nie udało się ustawić opisu usługi.' }
    & sc.exe failure $serviceName reset= 86400 actions= restart/3000/restart/10000/restart/30000 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Nie udało się ustawić odzyskiwania usługi.' }
    Start-Service -Name $serviceName
    $readyEndpoint = Wait-ServiceApi $endpointPath
    if ($wasRouterRunning) { Start-RouterViaApi $readyEndpoint }

    if ($backupCreated) {
        Remove-Item -LiteralPath $backupPath -Recurse -Force -ErrorAction SilentlyContinue
        $backupCreated = $false
    }
    Write-Host "WorkRouter zainstalowany w $installPath"
    if ($Launch) { Start-Process -FilePath (Join-Path $installPath 'WorkRouter.Launcher.exe') }
}
catch {
    $failure = $_
    try {
        $runningService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
        if ($runningService -and $runningService.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue }
        if ($serviceCreated) { & sc.exe delete $serviceName | Out-Null }
    } catch {}
    try {
        if ($swapCompleted -and (Test-Path -LiteralPath $installPath)) { Remove-Item -LiteralPath $installPath -Recurse -Force -ErrorAction SilentlyContinue }
        if ($backupCreated -and (Test-Path -LiteralPath $backupPath)) { Move-Item -LiteralPath $backupPath -Destination $installPath -Force }
        if ($stageCreated -and (Test-Path -LiteralPath $stagePath)) { Remove-Item -LiteralPath $stagePath -Recurse -Force -ErrorAction SilentlyContinue }
        if ($metadataWritten) {
            if ($null -ne $previousMetadataRaw) { Set-Content -LiteralPath $metadataPath -Value $previousMetadataRaw -Encoding UTF8 }
            elseif (Test-Path -LiteralPath $metadataPath) { Remove-Item -LiteralPath $metadataPath -Force -ErrorAction SilentlyContinue }
        }
        if ($wasServiceRunning -and (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
            Start-Service -Name $serviceName -ErrorAction SilentlyContinue
            if ($wasRouterRunning) {
                try { Start-RouterViaApi (Wait-ServiceApi $endpointPath) } catch {}
            }
        }
    } catch {}
    throw $failure
}
