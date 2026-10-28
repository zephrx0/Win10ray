$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$repository = Split-Path -Parent $PSScriptRoot
$cache = Join-Path $repository '.tools\dependencies'
$null = New-Item -ItemType Directory -Path $cache -Force

function Get-VerifiedArchive([string]$Url, [string]$Name, [string]$Sha256) {
    $path = Join-Path $cache $Name
    if (-not (Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Sha256) {
        Write-Host "Downloading $Name from the official GitHub release..."
        Invoke-WebRequest -Uri $Url -OutFile $path -UseBasicParsing
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $Sha256) {
        throw "SHA256 verification failed: $Name"
    }
    return $path
}

function Copy-Component([string]$SourceDirectory, [string]$Name, [string]$DestinationDirectory) {
    $matches = @(Get-ChildItem -LiteralPath $SourceDirectory -Recurse -File | Where-Object Name -eq $Name)
    if ($matches.Count -ne 1) { throw "Expected one $Name in the verified archive." }
    $null = New-Item -ItemType Directory -Path $DestinationDirectory -Force
    Copy-Item -LiteralPath $matches[0].FullName -Destination (Join-Path $DestinationDirectory $Name) -Force
}

$xray = Get-VerifiedArchive `
    'https://github.com/XTLS/Xray-core/releases/download/v26.10.21/Xray-windows-64.zip' `
    'Xray-26.10.21-windows-64.zip' `
    'FC58C39EE2F2717B4C54D28D374362389E5319B20AE44AEA76EA45A5243EC827'
$singBox = Get-VerifiedArchive `
    'https://github.com/SagerNet/sing-box/releases/download/v1.14.4/sing-box-1.14.4-windows-amd64.zip' `
    'sing-box-1.14.4-windows-amd64.zip' `
    '99185D75D314A07DBBA4A130C8AD986CBEB629A07927975636B341CA61F991DF'

$xraySource = Join-Path $cache 'xray-26.10.21'
$tunSource = Join-Path $cache 'sing-box-1.14.4'
Expand-Archive -LiteralPath $xray -DestinationPath $xraySource -Force
Expand-Archive -LiteralPath $singBox -DestinationPath $tunSource -Force
foreach ($name in @('xray.exe', 'geoip.dat', 'geosite.dat')) {
    Copy-Component $xraySource $name (Join-Path $repository 'xray')
}
Copy-Component $xraySource 'wintun.dll' (Join-Path $repository 'tun')
foreach ($name in @('sing-box.exe', 'libcronet.dll')) {
    Copy-Component $tunSource $name (Join-Path $repository 'tun')
}

$expected = @{
    'sing-box.exe' = '8F30682EE198855440D2A7FE070D5D3653793B2D01BB8C85EEA88F93BBE7C8B9'
    'wintun.dll' = 'E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE'
    'libcronet.dll' = '3217C6260FBCA5F16072E0B79735742F40109A63BB0FF88FD6B96DD6B54A2928'
}
foreach ($name in $expected.Keys) {
    $path = Join-Path $repository ('tun\' + $name)
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected[$name]) {
        throw "TUN component verification failed: $name"
    }
}
Write-Host 'Verified Xray-core and TUN components are ready.'
