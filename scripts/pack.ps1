# Packs src/QuickLook.Plugin.SaiViewer.dll into a .qlplugin archive.
# ASCII only: Windows PowerShell 5.1 parses BOM-less UTF-8 scripts as ANSI.
param(
    [string]$Dll = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Dll) { $Dll = Join-Path $root 'src\bin\Release\QuickLook.Plugin.SaiViewer.dll' }
if (-not $OutDir) { $OutDir = Join-Path $root 'out' }
$meta = Join-Path $root 'src\QuickLook.Plugin.Metadata.config'

if (-not (Test-Path $Dll)) { throw "DLL not found: $Dll  (run: dotnet build src\QuickLook.Plugin.SaiViewer.csproj -c Release)" }
if (-not (Test-Path $meta)) { throw "Metadata file not found: $meta" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$stage = Join-Path ([System.IO.Path]::GetTempPath()) ('sai2ql-' + [System.Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $stage | Out-Null

Copy-Item $Dll $stage
Copy-Item $meta (Join-Path $stage 'QuickLook.Plugin.Metadata.config')

$out = Join-Path $OutDir 'QuickLook.Plugin.SaiViewer.qlplugin'
if (Test-Path $out) { Remove-Item $out -Force }

Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $out)
Remove-Item $stage -Recurse -Force

Write-Host ("packed {0} ({1} bytes)" -f $out, (Get-Item $out).Length)
