[CmdletBinding()]
param([switch] $PromoteShortcut, [switch] $DownloadDependencies, [switch] $RegisterCodexMcp)

$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:PYTHONUTF8 = '1'
$env:BLENDERMCP_ADDONS_DIR = Join-Path $PSScriptRoot 'runtime\blender-scripts\addons'
$env:ANONYMIZED_TELEMETRY = 'false'
$addon = Join-Path $env:BLENDERMCP_ADDONS_DIR 'blender_mcp.py'
$target = Join-Path $PSScriptRoot 'publish-workspace\Jarvis.exe'

if ($PromoteShortcut) {
    if (!(Test-Path -LiteralPath $target -PathType Leaf)) { throw 'Build Jarvis 2 and complete the release checks first.' }
    $desktop = [Environment]::GetFolderPath('DesktopDirectory')
    $shortcutPath = Join-Path $desktop 'Jarvis.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $previous = Join-Path $PSScriptRoot 'publish\Jarvis.exe'
    if ((Test-Path -LiteralPath $shortcutPath) -and $shortcut.TargetPath -notin @($previous, $target)) {
        throw "The Jarvis shortcut points to a different app: $($shortcut.TargetPath)"
    }
    if (Test-Path -LiteralPath $shortcutPath) {
        $backup = Join-Path $desktop 'Jarvis (previous).lnk'
        if (!(Test-Path -LiteralPath $backup) -and $shortcut.TargetPath -eq $previous) {
            Copy-Item -LiteralPath $shortcutPath -Destination $backup
        }
    }
    $shortcut.TargetPath = $target
    $shortcut.WorkingDirectory = Split-Path $target
    $shortcut.Description = 'Jarvis 2 local AI workspace'
    $shortcut.Save()
    Write-Output $shortcutPath
    return
}

[string[]] $uvOptions = @()
if (!$DownloadDependencies) { $uvOptions += '--offline' }
if (!(Test-Path -LiteralPath $addon)) {
    uvx @uvOptions --python 3.14 --from mcp-for-blender==2.3.1 python blender_mcp_offline.py install-addon
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $addon)) { throw 'Blender add-on installation failed.' }
}
uvx @uvOptions --python 3.14 --from mcp-for-blender==2.3.1 python blender_mcp_offline.py --verify-guard
if ($LASTEXITCODE -ne 0) { throw 'The Blender connector could not verify its local network guard.' }
$env:JARVIS_RUN_SCOPE = Join-Path $PSScriptRoot 'inactive-setup-scope.json'
uvx @uvOptions --python 3.14 --from windows-mcp==0.9.0 python -c 'import runpy; runpy.run_path("desktop_mcp.py")'
if ($LASTEXITCODE -ne 0) { throw 'Windows-MCP 0.9.0 is unavailable in the offline cache.' }

$sdk = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Jarvis\sdk\bpy-5.2.2'
$sdkPython = Join-Path $sdk 'Scripts\python.exe'
if (!(Test-Path -LiteralPath $sdkPython)) {
    uv venv @uvOptions --python 3.13 $sdk
    if ($LASTEXITCODE) { throw 'Could not create the Blender Python 3.13 SDK environment.' }
}
& $sdkPython -c 'import bpy; assert bpy.app.version == (5, 2, 2)' 2>$null
if ($LASTEXITCODE) {
    uv pip install @uvOptions --python $sdkPython --index-url https://pypi.org/simple 'bpy==5.2.2'
    if ($LASTEXITCODE) { throw 'Could not install the Blender Python SDK. Use -DownloadDependencies if its wheel is missing from the cache.' }
}
& $sdkPython .\sdk_probe.py
if ($LASTEXITCODE) { throw 'Blender Python SDK save and reopen probe failed.' }

dotnet publish Jarvis.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish-workspace
if ($LASTEXITCODE -ne 0) { throw 'Jarvis publish failed.' }
if ($RegisterCodexMcp) {
    $server = Join-Path (Split-Path $target) 'jarvis_mcp.py'
    $uvx = (Get-Command uvx -ErrorAction Stop).Source
    codex mcp add jarvis-local -- $uvx --offline --python 3.14 --from mcp-for-blender==2.3.1 python $server
    if ($LASTEXITCODE) { throw 'Codex MCP registration failed.' }
}
Write-Output $target
