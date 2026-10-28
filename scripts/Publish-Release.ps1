param([string]$InnoCompiler = '')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $InnoCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompiler = $command.Source }
    else {
        foreach ($candidate in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")) {
            if (Test-Path -LiteralPath $candidate) { $InnoCompiler = $candidate; break }
        }
    }
}
if (-not $InnoCompiler -or -not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Install Inno Setup 6 or pass -InnoCompiler with the full path to ISCC.exe.' }
Push-Location $repository
try {
    dotnet publish -c Release -p:PublishProfile=Windows --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    [xml]$project = Get-Content -LiteralPath (Join-Path $repository 'Win10ray.csproj') -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid client version.' }
    $output = Join-Path $repository ('artifacts\Win10ray-' + $version)
    & $InnoCompiler /Qp ('/DAppVersion=' + $version) ('/DPayloadDirectory=' + $output) (Join-Path $repository 'installer\Win10ray.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $installer = Join-Path $repository ('artifacts\Win10ray-Setup-' + $version + '.exe')
    $checksum = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($installer + '.sha256', $checksum + '  ' + [IO.Path]::GetFileName($installer) + "`n")
    Write-Host "Release installer: $installer"
    Write-Host "SHA256: $checksum"
}
finally { Pop-Location }
