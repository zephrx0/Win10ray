$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    dotnet publish -c Release -p:PublishProfile=Windows --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    [xml]$project = Get-Content -LiteralPath (Join-Path $repository 'Win10ray.csproj') -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid client version.' }
    $output = Join-Path $repository ('artifacts\Win10ray-' + $version)
    $files = @(
        'Win10ray.exe', 'updater.exe', 'README.md',
        'xray\xray.exe', 'xray\geoip.dat', 'xray\geosite.dat', 'xray\LICENSE', 'xray\VERSION.txt',
        'tun\sing-box.exe', 'tun\wintun.dll', 'tun\libcronet.dll', 'tun\LICENSE', 'tun\COPYING', 'tun\LICENSE-Wintun', 'tun\VERSION.txt',
        'service\Win10ray.TunService.exe', 'Assets\Flags\SOURCE.txt', 'docs\images\connection.png'
    )
    $zip = Join-Path $repository ('artifacts\Win10ray-' + $version + '-win-x64.zip')
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $stream = [IO.File]::Open($zip, [IO.FileMode]::Create)
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relative in $files) {
            $path = Join-Path $output $relative
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing release file: $relative" }
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $path, $relative.Replace('\', '/'), [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose(); $stream.Dispose() }
    $checksum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($zip + '.sha256', $checksum + '  ' + [IO.Path]::GetFileName($zip) + "`n")
    Write-Host "Release archive: $zip"
    Write-Host "SHA256: $checksum"
}
finally { Pop-Location }
