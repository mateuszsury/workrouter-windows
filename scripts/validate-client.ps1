[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ipaddress]$WorkGateway,

    [Parameter(Mandatory)]
    [ipaddress[]]$HomeTargets,

    [int[]]$HomePorts = @(22, 80, 443, 445, 8123)
)

$ErrorActionPreference = 'Stop'
$failures = [Collections.Generic.List[string]]::new()

function Test-ExpectedTcp {
    param(
        [string]$Label,
        [ipaddress]$Address,
        [int]$Port,
        [bool]$Expected
    )

    $actual = Test-NetConnection -ComputerName $Address.IPAddressToString -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue
    $passed = $actual -eq $Expected
    $mark = if ($passed) { 'PASS' } else { 'FAIL' }
    Write-Host ("[{0}] {1}: {2}:{3} expected={4} actual={5}" -f $mark, $Label, $Address, $Port, $Expected, $actual)
    if (-not $passed) { $failures.Add("$Label $Address`:$Port") }
}

Test-ExpectedTcp -Label 'Internet' -Address ([ipaddress]'1.1.1.1') -Port 443 -Expected $true
Test-ExpectedTcp -Label 'SMB WorkRouter' -Address $WorkGateway -Port 445 -Expected $true

foreach ($target in $HomeTargets) {
    foreach ($port in $HomePorts) {
        Test-ExpectedTcp -Label 'Domowy LAN zablokowany' -Address $target -Port $port -Expected $false
    }
}

$workRoute = Get-NetRoute -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.NextHop -eq $WorkGateway.IPAddressToString } |
    Sort-Object RouteMetric |
    Select-Object -First 1
if ($workRoute) {
    Write-Host "[PASS] Znaleziono trasę przez bramę WORK: $($workRoute.DestinationPrefix)"
} else {
    Write-Host '[FAIL] Nie znaleziono trasy IPv4 przez bramę WORK.'
    $failures.Add('route-work')
}

$globalIpv6 = Get-NetIPAddress -AddressFamily IPv6 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike 'fe80:*' -and $_.IPAddress -ne '::1' }
if ($globalIpv6) {
    Write-Warning 'Laptop ma globalny adres IPv6 (np. z VPN/innego interfejsu). Zweryfikuj osobno, że nie daje on dostępu do domowego LAN-u.'
} else {
    Write-Host '[PASS] Brak globalnego IPv6 poza link-local na aktywnych interfejsach.'
}

if ($failures.Count -gt 0) {
    Write-Error ("Walidacja FAIL: " + ($failures -join ', '))
    exit 1
}

Write-Host 'Walidacja PASS dla wykonanych prób. Powtórz ją po połączeniu z firmowym VPN-em.'
