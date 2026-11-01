<#
.SYNOPSIS
Builds faena for Windows: dist\faena\faena.exe and the zip dist\faena-<version>-windows.zip.

.DESCRIPTION
Publishes src\Faena (Release, win-x64, self-contained, single file). Playwright's driver stays
loose beside the exe (.playwright\), which it needs. Without -Navegador, Chromium is downloaded
on first use into %LOCALAPPDATA%\Faena\navegador (or tramoya's is borrowed); with it, the browsers
are copied into dist\faena\navegador\ and the folder becomes a portable kit that needs no download.
Code signing happens outside this script (signtool).

Works on Windows PowerShell 5.1.

.PARAMETER Navegador
A folder that already holds the Playwright browsers this build needs (for example
%LOCALAPPDATA%\Tramoya\navegador): the newest chromium-*, chromium_headless_shell-* and winldd-*
are copied into the kit.

.PARAMETER SinZip
Leave dist\faena\ and do not zip it (fast builds while developing).
#>
[CmdletBinding()]
param(
    [string]$Navegador,
    [switch]$SinZip
)

$ErrorActionPreference = 'Stop'

$raiz = $PSScriptRoot
$distRoot = Join-Path $raiz 'dist'
$dist = Join-Path $distRoot 'faena'
$csproj = Join-Path $raiz 'src\Faena\Faena.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    $env:PATH = 'C:\Program Files\dotnet;' + $env:PATH
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet not found (neither on the PATH nor in C:\Program Files\dotnet).'
}

# Version from Directory.Build.props, the single place where it lives.
$props = Get-Content (Join-Path $raiz 'Directory.Build.props') -Raw
if ($props -notmatch '<Version>\s*([^<\s]+)\s*</Version>') {
    throw 'Directory.Build.props has no <Version>.'
}
$version = $Matches[1]

Write-Host "== faena ${version}: dotnet publish (Release, win-x64, single file) =="
if (Test-Path $dist) {
    Remove-Item $dist -Recurse -Force
}
New-Item -ItemType Directory -Force $dist | Out-Null
& dotnet publish $csproj -c Release -r win-x64 -o $dist
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed (code $LASTEXITCODE)."
}
# The embedded pdbs are inside the exe already; loose ones only add weight.
Get-ChildItem $dist -Filter *.pdb | Remove-Item -Force

if ($Navegador) {
    Write-Host "== Chromium from $Navegador =="
    $destino = Join-Path $dist 'navegador'
    New-Item -ItemType Directory -Force $destino | Out-Null
    foreach ($patron in 'chromium-*', 'chromium_headless_shell-*', 'winldd-*') {
        $carpeta = Get-ChildItem $Navegador -Directory -Filter $patron | Sort-Object Name -Descending | Select-Object -First 1
        if (-not $carpeta) {
            throw "There is no $patron folder in $Navegador."
        }
        Copy-Item $carpeta.FullName (Join-Path $destino $carpeta.Name) -Recurse
    }
}

$leeme = @"
faena $version

Le encargas una tarea en una web o en un programa de Windows y la hace sola, en segundo plano.
Pregunta antes de enviar, borrar o pagar nada, y lo que sale bien lo aprende como habilidad.

  faena.exe                      abre la ventana
  faena.exe hacer "..." --url    desde la consola (faena.exe --help)

Necesita un modelo: Ollama en este equipo (qwen3-coder:30b) o la variable ANTHROPIC_API_KEY.
Las tareas y las habilidades se guardan en %USERPROFILE%\Faena.
"@
Set-Content -Path (Join-Path $dist 'LEEME.txt') -Value $leeme -Encoding UTF8

# The window embeds its fonts (SIL OFL 1.1); their licences travel with the kit.
$licencias = Join-Path $dist 'licencias'
New-Item -ItemType Directory -Force $licencias | Out-Null
Copy-Item (Join-Path $raiz 'src\Faena\ui\fonts\OFL-*.txt') $licencias
Copy-Item (Join-Path $raiz 'LICENSE') (Join-Path $licencias 'LICENSE-faena.txt')

if (-not $SinZip) {
    $zip = Join-Path $distRoot "faena-$version-windows.zip"
    if (Test-Path $zip) {
        Remove-Item $zip -Force
    }
    Write-Host "== $zip =="
    Compress-Archive -Path $dist -DestinationPath $zip
}
Write-Host "Done: $dist"
