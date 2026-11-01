[CmdletBinding(DefaultParameterSetName = 'Enable')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Enable')][switch]$Enable,
    [Parameter(Mandatory, ParameterSetName = 'Disable')][switch]$Disable,
    [string]$DataDirectory
)
$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this installer as your regular desktop user without elevation.'
}
$packageDirectory = Split-Path -Parent $PSScriptRoot
$agent = Join-Path $packageDirectory 'agent/DevTunnelToolkit.Agent.exe'
if (-not (Test-Path -LiteralPath $agent -PathType Leaf)) { throw 'The packaged agent executable is missing.' }
$mode = if ($Disable) { 'disable' } else { 'enable' }
$agentArguments = @('--configure-startup', $mode)
if ($DataDirectory) { $agentArguments += @('--data-dir', $DataDirectory) }
# The agent shares startup ownership checks and rollback with the desktop UI.
& $agent @agentArguments
if ($LASTEXITCODE -ne 0) { throw 'The agent could not update startup registration.' }
