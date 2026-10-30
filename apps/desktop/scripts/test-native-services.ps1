[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$agent = Join-Path $PackageDirectory 'agent/DevTunnelToolkit.Agent.exe'
$cli = Join-Path $PackageDirectory 'cli/DevTunnelToolkit.Cli.exe'
$data = Join-Path ([IO.Path]::GetTempPath()) ('desktop-services-smoke-' + [guid]::NewGuid().ToString('N'))
$process = $null

function Call-Cli([string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $cli --data-dir $data @Arguments 2>$null; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    if ($code -ne 0) { throw ('Isolated service CLI failed: ' + ($Arguments -join ' ') + ', exit ' + $code) }
    return ($output | ConvertFrom-Json)
}

function Ready {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($process.HasExited) { throw 'The isolated service agent exited.' }
        try { return (Call-Cli @('status', '--json')) } catch { Start-Sleep -Milliseconds 200 }
    }
    throw 'The isolated service agent did not become ready.'
}

function Service-Ready([string]$Id) {
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $snapshot = Call-Cli @('status', '--json')
        $status = @($snapshot.serviceStatuses | Where-Object { $_.serviceId -eq $Id })
        if ($status.Count -eq 1 -and $status[0].state -eq 'Connected' -and $status[0].port -gt 0) { return $status[0] }
        Start-Sleep -Milliseconds 100
    }
    throw 'The isolated service did not become ready.'
}

function Read-Exact([IO.Stream]$Stream, [int]$Length) {
    $bytes = New-Object byte[] $Length
    $offset = 0
    while ($offset -lt $Length) {
        $read = $Stream.Read($bytes, $offset, $Length - $offset)
        if ($read -eq 0) { throw 'Proxy closed before returning the fixture response.' }
        $offset += $read
    }
    return ,$bytes
}

function Probe([string]$Protocol, [int]$Port, [int]$Destination) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $client.Connect('127.0.0.1', $Port)
        $stream = $client.GetStream(); $stream.ReadTimeout = 5000; $stream.WriteTimeout = 5000
        if ($Protocol -eq 'Http') {
            $request = [Text.Encoding]::ASCII.GetBytes("CONNECT 127.0.0.1:$Destination HTTP/1.1`r`nHost: 127.0.0.1:$Destination`r`n`r`n")
            $stream.Write($request, 0, $request.Length)
            $header = New-Object Text.StringBuilder
            while ($header.Length -lt 16384 -and !$header.ToString().EndsWith("`r`n`r`n")) {
                $value = $stream.ReadByte()
                if ($value -lt 0) { throw 'HTTP proxy closed before CONNECT response.' }
                [void]$header.Append([char]$value)
            }
            if ($header.ToString() -notmatch '^HTTP/1\.[01] 200 ') { throw 'CONNECT fixture was not accepted.' }
        }
        else {
            [byte[]]$greeting = @(5, 1, 0); $stream.Write($greeting, 0, $greeting.Length)
            $accepted = Read-Exact $stream 2
            if ($accepted[0] -ne 5 -or $accepted[1] -ne 0) { throw 'SOCKS greeting failed.' }
            [byte[]]$request = @(5, 1, 0, 1, 127, 0, 0, 1, ($Destination -shr 8), ($Destination -band 255))
            $stream.Write($request, 0, $request.Length)
            $reply = Read-Exact $stream 4
            if ($reply[0] -ne 5 -or $reply[1] -ne 0) { throw 'SOCKS CONNECT failed.' }
            if ($reply[3] -eq 1) { $null = Read-Exact $stream 6 }
            elseif ($reply[3] -eq 4) { $null = Read-Exact $stream 18 }
            else { throw 'Unexpected SOCKS bound-address type.' }
        }
        [byte[]]$challenge = 0..31
        $stream.Write($challenge, 0, $challenge.Length)
        $reply = Read-Exact $stream 32
        if ([Convert]::ToBase64String($reply) -ne [Convert]::ToBase64String($challenge)) { throw 'Proxy fixture payload mismatch.' }
    }
    finally { $client.Dispose() }
}

try {
    $process = Start-Process -FilePath $agent -ArgumentList @('--data-dir', "`"$data`"") -WindowStyle Hidden -PassThru
    $initial = Ready
    if (@($initial.accounts).Count -ne 0 -or @($initial.services).Count -ne 0) { throw 'Expected isolated empty state.' }
    $echo = Call-Cli @('diagnostic', 'echo-start')
    $ids = @()
    foreach ($protocol in @('Http', 'Socks5')) {
        $id = [guid]::NewGuid().ToString('N'); $ids += $id
        $profile = @{
            id = $id; name = 'Synthetic service ' + $protocol; protocol = $protocol
            localAddress = '127.0.0.1'; port = 0; autoStart = $true
            policy = @{ rules = @(@{ id = [guid]::NewGuid().ToString('N'); kind = 'Ip'; destination = '127.0.0.1'; ports = @([int]$echo.port); allowLoopback = $true }) }
        }
        $file = Join-Path $data ($id + '.json')
        [IO.File]::WriteAllText($file, ($profile | ConvertTo-Json -Depth 8))
        $null = Call-Cli @('service', 'import', $file)
        $null = Call-Cli @('service', 'start', $id)
        $status = Service-Ready $id
        Probe $protocol ([int]$status.port) ([int]$echo.port)
    }
    $snapshot = Call-Cli @('status', '--json')
    if (@($snapshot.accounts).Count -ne 0 -or @($snapshot.hosts).Count -ne 0 -or @($snapshot.services).Count -ne 2) {
        throw 'Independent services unexpectedly needed an account or publication.'
    }
    $null = & $cli --data-dir $data agent stop
    if ($LASTEXITCODE -ne 0 -or !$process.WaitForExit(10000)) { throw 'Service agent shutdown failed.' }
    $process.Dispose()
    $process = Start-Process -FilePath $agent -ArgumentList @('--data-dir', "`"$data`"") -WindowStyle Hidden -PassThru
    $persisted = Ready
    if (@($persisted.services).Count -ne 2) { throw 'Services were not persisted.' }
    foreach ($id in $ids) {
        $status = Service-Ready $id
        $null = Call-Cli @('service', 'stop', $id)
        $listener = New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback, [int]$status.port)
        try { $listener.Start() } finally { $listener.Stop() }
        $null = Call-Cli @('service', 'remove', $id)
    }
    if (@((Call-Cli @('service', 'list')).services).Count -ne 0) { throw 'Stopped services were not removed.' }
    Write-Output 'WINDOWS_SERVICES_OK: independent HTTP CONNECT and SOCKS5 traffic, no account, persistence, autostart, stop/remove and released listeners.'
}
finally {
    if ($null -ne $process) {
        if (!$process.HasExited) {
            $previous = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
            try { & $cli --data-dir $data agent stop 2>$null | Out-Null } finally { $ErrorActionPreference = $previous }
            if (!$process.WaitForExit(10000)) { $process.Kill(); $process.WaitForExit() }
        }
        $process.Dispose()
    }
    if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
}
