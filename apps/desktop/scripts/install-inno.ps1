[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)

$ErrorActionPreference = 'Stop'
$version = '7.1.0'
$expectedSha256 = '0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f'
$download = Join-Path $env:TEMP "innosetup-$version-x64.exe"
$compiler = Join-Path $Destination 'ISCC.exe'
if (Test-Path -LiteralPath $compiler -PathType Leaf) { return }

$url = "https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-$version-x64.exe"
Invoke-WebRequest -Uri $url -OutFile $download
if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $expectedSha256) {
    throw 'The Inno Setup download did not match its pinned SHA-256 digest.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $download
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Pyrsys B\.V\.') {
    throw 'The Inno Setup download does not have the expected valid publisher signature.'
}

New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$install = Start-Process -FilePath $download -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/DIR=`"$Destination`"") -Wait -PassThru
if ($install.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    throw 'The verified Inno Setup compiler could not be installed.'
}
Write-Output $compiler
