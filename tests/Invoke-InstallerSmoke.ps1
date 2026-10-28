param([string]$InnoCompiler = '')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $InnoCompiler) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $InnoCompiler = $command.Source }
    else { $InnoCompiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
}
if (-not (Test-Path -LiteralPath $InnoCompiler)) { throw 'Inno Setup 6 compiler not found.' }
Push-Location $repository
try {
    $root = Join-Path $repository '.tools\installer-smoke'
    $output = Join-Path $root 'bin'
    dotnet build tests/XrayMetro.SmokeTests/XrayMetro.SmokeTests.csproj ('-p:OutputPath=' + $output + '\') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test build failed.' }
    [xml]$project = Get-Content -LiteralPath Win10ray.csproj -Raw
    $version = [string]$project.Project.PropertyGroup.Version
    $payload = Join-Path $root 'payload'
    $null = New-Item -ItemType Directory -Path $payload -Force
    Copy-Item -LiteralPath (Join-Path $output 'Win10ray.exe') -Destination (Join-Path $payload 'Win10ray.exe') -Force
    # TestBuild never executes this file or creates shortcuts/uninstall registry entries.
    [IO.File]::WriteAllText((Join-Path $payload 'updater.exe'), 'Test fixture, never executed')
    foreach ($mode in @('success', 'failure')) {
        $options = @('/Qp', ('/DAppVersion=' + $version), ('/DPayloadDirectory=' + $payload), ('/DBuildOutput=' + (Join-Path $root $mode)), '/DTestBuild=1')
        if ($mode -eq 'failure') { $options += '/DTestFailBeforeInstall=1' }
        & $InnoCompiler @options installer/Win10ray.iss
        if ($LASTEXITCODE -ne 0) { throw 'Fixture compilation failed.' }
    }
    & (Join-Path $output 'XrayMetro.SmokeTests.exe') --installer-test (Join-Path $root ('success\Win10ray-Setup-' + $version + '.exe')) (Join-Path $root ('failure\Win10ray-Setup-' + $version + '.exe'))
    if ($LASTEXITCODE -ne 0) { throw 'Installer integration test failed.' }
}
finally { Pop-Location }
