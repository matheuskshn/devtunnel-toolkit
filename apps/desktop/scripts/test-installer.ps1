[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [Parameter(Mandatory)][string]$ExpectedVersion
)

$ErrorActionPreference = 'Stop'
$uninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{F5DB0F65-513E-4B13-990E-5069ADFA75DA}_is1'
foreach ($hive in @('HKCU:', 'HKLM:')) {
    if (Test-Path -LiteralPath "$hive\$uninstallKey") {
        throw 'Installer smoke requires a clean runner. An existing Desktop installation must not be modified or uninstalled by this test.'
    }
}
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$installDirectory = Join-Path $temporaryRoot ("desktop-installer-smoke-" + [guid]::NewGuid().ToString('N'))
$app = Join-Path $installDirectory 'DevTunnelToolkit.App.exe'
$agent = Join-Path $installDirectory 'agent/DevTunnelToolkit.Agent.exe'
$cli = Join-Path $installDirectory 'cli/DevTunnelToolkit.Cli.exe'
$uninstaller = Join-Path $installDirectory 'unins000.exe'

try {
    $setup = Start-Process -FilePath $InstallerPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=`"$installDirectory`"") -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw "Installer exited with code $($setup.ExitCode)." }
    foreach ($path in @($app, $agent, $cli, $uninstaller)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Installed file missing: $path" }
    }
    $manifest = Get-Content -LiteralPath (Join-Path $installDirectory 'build-manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.version -ne $ExpectedVersion -or $manifest.runtimeIdentifier -ne 'win-x64') {
        throw 'The installer did not install the expected build.'
    }
    & $agent --help
    if ($LASTEXITCODE -ne 0) { throw 'The installed agent could not start.' }
    & $cli --help
    if ($LASTEXITCODE -ne 0) { throw 'The installed CLI could not start.' }
    & (Join-Path $PSScriptRoot 'test-native-agent.ps1') -PackageDirectory $installDirectory
}
finally {
    if (Test-Path -LiteralPath $uninstaller -PathType Leaf) {
        $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
        if ($uninstall.ExitCode -ne 0) { throw "Uninstaller exited with code $($uninstall.ExitCode)." }
    }
}
if (Test-Path -LiteralPath $app -PathType Leaf) { throw 'Uninstall left the app executable behind.' }
