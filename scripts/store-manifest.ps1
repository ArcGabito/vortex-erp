# Pone en Package.appxmanifest la identidad que Partner Center reservó para la app (nombre del paquete, editor y
# nombre visible del editor). La Store vuelve a firmar el paquete con su certificado: no hace falta uno propio y
# Windows no muestra la advertencia de SmartScreen.
#
#   ./scripts/store-manifest.ps1 -Manifest src/VortexErp/Platforms/Windows/Package.appxmanifest `
#       -IdentityName 12345ArcGabito.VortexERP -Publisher "CN=00000000-0000-0000-0000-000000000000" `
#       -PublisherDisplayName "ArcGabito"
param(
    [Parameter(Mandatory)] [string] $Manifest,
    [Parameter(Mandatory)] [string] $IdentityName,
    [Parameter(Mandatory)] [string] $Publisher,
    [Parameter(Mandatory)] [string] $PublisherDisplayName
)
$ErrorActionPreference = 'Stop'

$path = (Resolve-Path $Manifest).Path
[xml] $xml = Get-Content $path -Raw
$ns = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
$ns.AddNamespace('m', 'http://schemas.microsoft.com/appx/manifest/foundation/windows10')

$identity = $xml.SelectSingleNode('/m:Package/m:Identity', $ns)
$identity.SetAttribute('Name', $IdentityName)
$identity.SetAttribute('Publisher', $Publisher)
$xml.SelectSingleNode('/m:Package/m:Properties/m:PublisherDisplayName', $ns).InnerText = $PublisherDisplayName

$settings = New-Object System.Xml.XmlWriterSettings
$settings.Indent = $true
$settings.Encoding = New-Object System.Text.UTF8Encoding($true)
$writer = [System.Xml.XmlWriter]::Create($path, $settings)
$xml.Save($writer)
$writer.Close()
Write-Host "Identidad: $IdentityName ($Publisher, $PublisherDisplayName)"
