[CmdletBinding()]
param([switch] $NoRestore)

$ErrorActionPreference = 'Stop'
$restore = @()
if ($NoRestore) { $restore += '--no-restore' }

Push-Location -LiteralPath $PSScriptRoot
try {
    $work = Join-Path $PSScriptRoot 'obj\installer'
    $publish = Join-Path $PSScriptRoot 'publish-installer'
    $app = Join-Path $work ('app-' + [Guid]::NewGuid().ToString('N'))
    $payload = Join-Path $work 'payload.zip'
    New-Item -Path $work -ItemType Directory -Force | Out-Null
    $arguments = @('-c','Release','-r','win-x64','--self-contained','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true')
    dotnet publish Jarvis.csproj @arguments @restore -o $app
    if ($LASTEXITCODE -ne 0) { throw 'Jarvis publish failed.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload }
    [IO.Compression.ZipFile]::CreateFromDirectory($app, $payload)
    dotnet publish installer\JarvisSetup.csproj @arguments @restore "-p:JarvisPayload=$payload" -p:DebugType=None -o $publish
    if ($LASTEXITCODE -ne 0) { throw 'Installer publish failed.' }
    Get-Item -LiteralPath (Join-Path $publish 'Jarvis_X_Setup.exe')
} finally { Pop-Location }
