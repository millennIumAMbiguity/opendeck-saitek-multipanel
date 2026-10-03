# Builds the plugin and installs it into OpenDeck's plugin folder (Windows).
#   .\scripts\install.ps1             Native AOT build (needs Visual Studio's "Desktop development with C++")
#   .\scripts\install.ps1 -NoAot      regular self-contained build instead
#   .\scripts\install.ps1 -NoInstall  only stage into artifacts\
# Restart OpenDeck afterwards to load the new build.
param(
    [string]$PluginsDir = (Join-Path $env:APPDATA 'opendeck\plugins'),
    [switch]$NoAot,
    [switch]$NoInstall
)
$ErrorActionPreference = 'Stop'

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$plugin = 'io.github.millenniumambiguity.saitekmultipanel.sdPlugin'
$staging = Join-Path $root "artifacts\$plugin"

if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
Copy-Item -Recurse (Join-Path $root "plugin\$plugin") $staging

# Native AOT links with MSVC; the ILCompiler targets locate it through vswhere.
$vsInstaller = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if ((Test-Path $vsInstaller) -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH += ";$vsInstaller" }

$aot = if ($NoAot) { 'false' } else { 'true' }
dotnet publish (Join-Path $root 'src/SaitekMultiPanel/SaitekMultiPanel.csproj') -c Release -r win-x64 "-p:PublishAot=$aot" -o (Join-Path $staging 'bin\win-x64') --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Get-ChildItem (Join-Path $staging 'bin') -Recurse -Filter *.pdb | Remove-Item

if ($NoInstall) { Write-Host "Built $staging"; return }

# The running plugin locks its exe; stop it so the copy succeeds.
Get-Process 'saitek-multipanel' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
$target = Join-Path $PluginsDir $plugin
if (Test-Path $target) { Remove-Item -Recurse -Force $target }
Copy-Item -Recurse $staging $target
Write-Host "Installed $target. Restart OpenDeck to load it."
