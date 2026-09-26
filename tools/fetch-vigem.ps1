# Downloads the ViGEmBus installer that build.cmd embeds, and verifies it (pinned SHA-256 + Authenticode signer).
$ErrorActionPreference = 'Stop'
$version = '1.22.0'
$sha256  = '89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A'
$vendor  = Join-Path $PSScriptRoot '..\vendor'
$file    = Join-Path $vendor "ViGEmBus_${version}_x64_x86_arm64.exe"
$url     = "https://github.com/nefarius/ViGEmBus/releases/download/v$version/ViGEmBus_${version}_x64_x86_arm64.exe"

New-Item -ItemType Directory -Force $vendor | Out-Null
if (-not (Test-Path $file)) {
    Write-Host "Downloading $url"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest $url -OutFile $file -UseBasicParsing
}
$hash = (Get-FileHash $file -Algorithm SHA256).Hash
if ($hash -ne $sha256) { Remove-Item $file; throw "ViGEmBus installer hash mismatch ($hash)" }
$sig = Get-AuthenticodeSignature $file
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'Nefarius Software Solutions') { throw "ViGEmBus installer signature check failed: $($sig.Status)" }
$license = Join-Path $vendor 'ViGEmBus-LICENSE.txt'
if (-not (Test-Path $license)) {
    Invoke-WebRequest 'https://raw.githubusercontent.com/nefarius/ViGEmBus/master/LICENSE' -OutFile $license -UseBasicParsing
}
Write-Host "ViGEmBus $version installer OK"
