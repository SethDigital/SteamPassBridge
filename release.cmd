@echo off
rem Builds and packages dist\SteamPassBridge-<version>.zip (exe + README + LICENSE).
setlocal
call "%~dp0build.cmd" || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop';" ^
  "$root='%~dp0'.TrimEnd('\');" ^
  "$ver=(Get-Item \"$root\bin\SteamPassBridge.exe\").VersionInfo.ProductVersion;" ^
  "$stage=Join-Path $root \"dist\SteamPassBridge-$ver\";" ^
  "Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue; New-Item -ItemType Directory -Force $stage | Out-Null;" ^
  "Copy-Item \"$root\bin\SteamPassBridge.exe\",\"$root\README.md\" $stage; Copy-Item \"$root\LICENSE\" \"$stage\LICENSE.txt\"; Copy-Item \"$root\THIRD-PARTY-NOTICES.txt\" $stage;" ^
  "$zip=\"$stage.zip\"; Remove-Item $zip -ErrorAction SilentlyContinue;" ^
  "Compress-Archive -Path \"$stage\*\" -DestinationPath $zip;" ^
  "$hash=(Get-FileHash $zip -Algorithm SHA256).Hash; Set-Content \"$zip.sha256\" \"$hash  $(Split-Path $zip -Leaf)\";" ^
  "Write-Host \"Created $zip\"; Write-Host \"SHA256 $hash\""
