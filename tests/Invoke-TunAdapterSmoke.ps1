#Requires -RunAsAdministrator
param(
    [string]$SingBox = (Join-Path $PSScriptRoot '..\tun\sing-box.exe'),
    [ValidateRange(1, 20)][int]$Iterations = 6
)

# Real Wintun/PnP regression test. No default routes, DNS interception or WFP filters.
$ErrorActionPreference = 'Stop'
$binary = (Resolve-Path -LiteralPath $SingBox).Path
$directory = Join-Path ([IO.Path]::GetTempPath()) ('Win10ray-AdapterTest-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $directory
try {
    for ($attempt = 1; $attempt -le $Iterations; $attempt++) {
        $name = 'Win10ray-' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
        $log = Join-Path $directory ($attempt.ToString() + '.log')
        $config = Join-Path $directory 'tun.json'
        $settings = @{
            log = @{ level = 'info'; output = $log; timestamp = $false }
            inbounds = @(@{
                type = 'tun'; tag = 'tun-in'; interface_name = $name
                address = @('172.29.0.1/30', 'fdfe:dcba:9876::1/126')
                mtu = 1500; auto_route = $false; strict_route = $false; dns_mode = 'disabled'
            })
            outbounds = @(@{ type = 'direct'; tag = 'direct' })
        }
        [IO.File]::WriteAllText($config, ($settings | ConvertTo-Json -Depth 10))
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $binary
        $info.Arguments = 'run -c "' + $config + '"'
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardError = $true
        $process = New-Object Diagnostics.Process
        $process.StartInfo = $info
        $watch = [Diagnostics.Stopwatch]::StartNew()
        try {
            $null = $process.Start()
            $stderr = $process.StandardError.ReadToEndAsync()
            $ready = $false
            while ($watch.ElapsedMilliseconds -lt 35000 -and -not $process.HasExited) {
                $text = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw } else { '' }
                if ($text -like '*INFO sing-box started (*') { $ready = $true; break }
                Start-Sleep -Milliseconds 100
            }
            if (-not $ready) {
                if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
                throw "Adapter startup failed on attempt $attempt. $text $($stderr.GetAwaiter().GetResult())"
            }
            Write-Output "PASS $attempt/$Iterations : $name, $($watch.ElapsedMilliseconds) ms"
        }
        finally {
            if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit() }
            $process.Dispose()
        }
    }
}
finally {
    # Only files created by this test, never an installed driver or another VPN's adapter.
    Get-ChildItem -LiteralPath $directory -File | Remove-Item -Force
    Remove-Item -LiteralPath $directory
}
