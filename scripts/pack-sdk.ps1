<#
.SYNOPSIS
    Empaqueta el SDK del hub (VortexApi.Client) desde WSL en el feed local del ERP: packages/vortex-sdk.

.DESCRIPTION
    Para quien programa en Windows con el hub clonado dentro de WSL. Cada ejecución genera una versión -dev nueva;
    la app toma siempre la más alta (VortexApi.Client 0.*-*).

.EXAMPLE
    ./scripts/pack-sdk.ps1
    ./scripts/pack-sdk.ps1 -Distro Ubuntu -HubDir ~/src/vortex-api
#>
param(
    [string]$Distro = 'archlinux',
    [string]$HubDir = '~/vortex-api'
)

$ErrorActionPreference = 'Stop'

$feed = (Resolve-Path (Join-Path $PSScriptRoot '..\packages\vortex-sdk')).Path -replace '\\', '/'
$wslFeed = (wsl -d $Distro -- wslpath -a $feed).Trim()
if ($LASTEXITCODE -ne 0) { throw "No se pudo traducir la ruta del feed en la distro '$Distro'." }

wsl -d $Distro -- bash -lc "VORTEX_FEED='$wslFeed' $HubDir/scripts/pack-sdk.sh"
if ($LASTEXITCODE -ne 0) { throw "Falló el empaquetado del SDK (exit $LASTEXITCODE)." }
