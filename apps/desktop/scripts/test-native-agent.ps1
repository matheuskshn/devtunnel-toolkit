[CmdletBinding()]
param([Parameter(Mandatory)][string]$PackageDirectory)

$ErrorActionPreference = 'Stop'
$agent = Join-Path $PackageDirectory 'agent/DevTunnelToolkit.Agent.exe'
$cli = Join-Path $PackageDirectory 'cli/DevTunnelToolkit.Cli.exe'
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$dataDirectory = Join-Path $temporaryRoot ('desktop-agent-smoke-' + [guid]::NewGuid().ToString('N'))
$process = $null
$restart = $null

function Get-ReadyState([Diagnostics.Process]$AgentProcess) {
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        if ($AgentProcess.HasExited) { throw 'The test agent exited before becoming ready.' }
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            $output = & $cli --data-dir $dataDirectory status --json 2>$null
            $code = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previousPreference }
        if ($code -eq 0) { return ($output | ConvertFrom-Json) }
        Start-Sleep -Milliseconds 200
    }
    throw 'The isolated test agent did not become ready.'
}

try {
    $process = Start-Process -FilePath $agent -ArgumentList @('--data-dir', "`"$dataDirectory`"") -WindowStyle Hidden -PassThru
    $state = Get-ReadyState $process
    if (@($state.accounts).Count -ne 0) { throw 'The test must start with an empty isolated workspace.' }
    foreach ($provider in @('MicrosoftCli', 'GitHubCli')) {
        $account = & $cli --data-dir $dataDirectory account add $provider 'CLI-smoke-fixture' | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $account.provider -ne $provider -or $account.clientId) {
            throw 'A CLI account without a custom client ID could not be saved.'
        }
    }
    $state = Get-ReadyState $process
    if (@($state.accounts).Count -ne 2) { throw 'The IPC did not return both saved providers.' }
    & $cli --data-dir $dataDirectory agent stop | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not $process.WaitForExit(10000)) { throw 'Graceful agent shutdown failed.' }
    $restart = Start-Process -FilePath $agent -ArgumentList @('--data-dir', "`"$dataDirectory`"") -WindowStyle Hidden -PassThru
    $persisted = Get-ReadyState $restart
    if (@($persisted.accounts).Count -ne 2) { throw 'The saved providers did not survive an agent restart.' }
    & $cli --data-dir $dataDirectory agent stop | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not $restart.WaitForExit(10000)) { throw 'Restarted agent did not shut down gracefully.' }
    Write-Output 'Windows named-pipe smoke passed: both CLI providers, persistence after restart and graceful shutdown. No login or remote operations.'
}
finally {
    foreach ($ownedProcess in @($process, $restart)) {
        if ($null -ne $ownedProcess) {
            if (-not $ownedProcess.HasExited) { Stop-Process -Id $ownedProcess.Id; $ownedProcess.WaitForExit() }
            $ownedProcess.Dispose()
        }
    }
    # Only the random test-owned directory is removed. Never use the user's default state.
    if (Test-Path -LiteralPath $dataDirectory) { Remove-Item -LiteralPath $dataDirectory -Recurse -Force }
}
