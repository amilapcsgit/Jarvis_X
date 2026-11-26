param([Parameter(Mandatory)][string] $Payload)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.IO.Compression.FileSystem
$caption = 'Jarvis_X Setup'
$homeDir = Join-Path $env:LOCALAPPDATA 'Jarvis'
$runtimes = Join-Path $homeDir 'runtimes'
$registry = 'https://registry.npmjs.org'
$ollamaUrl = 'http://127.0.0.1:11434'
$baseline = 'qwen3.5:4b'
$extras = @('qwen3.5:9b-q4_K_M', 'maternion/qwen3.5-3d-coder:4b', 'MichelRosselli/bonsai-27b:Q1_0')

function Confirm-Setup([string] $Message) {
    [Windows.Forms.MessageBox]::Show($Message, $caption, 'YesNo', 'Question') -eq 'Yes'
}
function Select-Folder([string] $Description, [string] $Initial) {
    $dialog = New-Object Windows.Forms.FolderBrowserDialog
    try {
        $dialog.Description = $Description
        $dialog.SelectedPath = $Initial
        if ($dialog.ShowDialog() -ne 'OK') { return $null }
        $dialog.SelectedPath
    } finally { $dialog.Dispose() }
}
function Find-Executable([string] $Name, [string[]] $Candidates) {
    foreach ($candidate in $Candidates) {
        if ($candidate -and (Test-Path -LiteralPath $candidate -PathType Leaf)) { return $candidate }
    }
    $command = Get-Command $Name -ErrorAction SilentlyContinue
    if ($command) { $command.Source }
}
function Update-Path {
    $paths = @($env:PATH, [Environment]::GetEnvironmentVariable('PATH','Machine'),
        [Environment]::GetEnvironmentVariable('PATH','User'), (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links'))
    $env:PATH = $paths -join ';'
}
function Install-Dependency([string] $Id) {
    if (!(Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        throw "Install Microsoft App Installer (WinGet) or install $Id manually, then run setup again."
    }
    Write-Host "Installing $Id through WinGet..."
    & winget.exe install --id $Id --exact --source winget --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "WinGet could not install $Id. Exit code $LASTEXITCODE." }
    Update-Path
}
function Browse-Executable([string] $Title, [string] $Name) {
    $dialog = New-Object Windows.Forms.OpenFileDialog
    try {
        $dialog.Title = $Title
        $dialog.Filter = "$Name|$Name"
        if ($dialog.ShowDialog() -eq 'OK') { $dialog.FileName }
    } finally { $dialog.Dispose() }
}
function Invoke-Checked([string] $Executable, [string[]] $Arguments, [string] $Failure) {
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Failure Exit code $LASTEXITCODE." }
}
function Set-UserVariable([string] $Name, [string] $Value) {
    [Environment]::SetEnvironmentVariable($Name, $Value, 'User')
    [Environment]::SetEnvironmentVariable($Name, $Value, 'Process')
}
function Test-Ollama {
    try { return [bool](Invoke-RestMethod "$ollamaUrl/api/version" -TimeoutSec 2 -ErrorAction Stop).version }
    catch { return $false }
}
function Wait-Ollama {
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if (Test-Ollama) { return }
        Start-Sleep -Seconds 1
    }
    throw 'Ollama did not answer on 127.0.0.1:11434. Start Ollama for Windows and run setup again.'
}
function Configure-Ollama([string] $Executable, [string] $ModelDirectory, [bool] $Profile) {
    $app = Join-Path (Split-Path $Executable) 'ollama app.exe'
    $changes = @{ OLLAMA_HOST = '127.0.0.1:11434'; OLLAMA_NO_CLOUD = '1' }
    if ($ModelDirectory) { $changes.OLLAMA_MODELS = $ModelDirectory }
    if ($Profile) {
        $changes.OLLAMA_FLASH_ATTENTION = '1'
        $changes.OLLAMA_KV_CACHE_TYPE = 'q8_0'
        $changes.OLLAMA_MAX_LOADED_MODELS = '1'
        $changes.OLLAMA_NUM_PARALLEL = '1'
    }
    $lines = $changes.GetEnumerator() | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Value)" }
    if (!(Confirm-Setup ("Apply these settings to your shared Ollama instance and restart it? This can interrupt other Ollama clients. Existing model files stay in place. A different model folder may require another download." + [Environment]::NewLine * 2 + ($lines -join [Environment]::NewLine)))) {
        if (Test-Ollama) { return }
        if (Test-Path -LiteralPath $app) { Start-Process -FilePath $app -WindowStyle Hidden }
        Wait-Ollama
        return
    }
    if (!(Test-Path -LiteralPath $app)) { throw 'The selected Ollama install has no Windows tray app. Use the official Windows distribution or configure and restart your service separately.' }
    if ($ModelDirectory) { New-Item -Path $ModelDirectory -ItemType Directory -Force | Out-Null }
    foreach ($name in $changes.Keys) { Set-UserVariable $name $changes[$name] }

    $processes = @(Get-Process -Name 'ollama app','ollama' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $app -or $_.Path -eq $Executable } |
        Sort-Object @{Expression={ $_.ProcessName -ne 'ollama app' }})
    foreach ($process in $processes) {
        $process.Refresh()
        if (!$process.HasExited) {
            Stop-Process -InputObject $process
            if (!$process.WaitForExit(5000)) { throw 'Ollama did not stop. Quit Ollama from its tray icon and retry setup.' }
        }
    }
    if (Test-Ollama) { throw 'Another Ollama service is using port 11434. Restart it with the displayed settings before rerunning setup.' }
    Start-Process -FilePath $app -WindowStyle Hidden
    Wait-Ollama
}
function Get-BlenderCandidates {
    $env:JARVIS_BLENDER
    Get-ChildItem (Join-Path $env:ProgramFiles 'Blender Foundation') -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'blender.exe' }
}
function Get-WebView {
    Get-ChildItem -Path @(
        (Join-Path $env:LOCALAPPDATA 'Microsoft\EdgeWebView\Application'),
        (Join-Path $env:ProgramFiles 'Microsoft\EdgeWebView\Application'),
        (Join-Path ${env:ProgramFiles(x86)} 'Microsoft\EdgeWebView\Application')
    ) -Filter msedgewebview2.exe -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
}
function New-Shortcut([string] $Folder, [string] $Target) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut((Join-Path $Folder 'Jarvis_X.lnk'))
    $link.TargetPath = $Target
    $link.WorkingDirectory = Split-Path $Target
    $link.IconLocation = "$Target,0"
    $link.Save()
}

try {
    $defaultInstall = Join-Path $env:LOCALAPPDATA 'Programs\Jarvis_X'
    New-Item -Path $defaultInstall -ItemType Directory -Force | Out-Null
    $installDir = Select-Folder 'Choose the folder where Jarvis_X will be installed.' $defaultInstall
    if (!$installDir) { exit 0 }
    $modelDir = $null
    if (Confirm-Setup 'Choose a different folder for shared Ollama models? Select No to keep the current folder.') {
        $modelDir = Select-Folder 'Choose the shared Ollama model folder.' $env:USERPROFILE
        if (!$modelDir) { exit 0 }
    }
    $profile = Confirm-Setup 'Offer the RTX 2080 shared Ollama profile? It enables Flash Attention, q8_0 cache, and one loaded model and request at a time.'
    $desktop = Confirm-Setup 'Create a desktop shortcut as well as the Start menu shortcut?'
    $models = @($extras | Where-Object { Confirm-Setup "Also download $_? This optional model requires several GB." })

    Update-Path
    $ollamaCandidates = @((Join-Path $env:LOCALAPPDATA 'Programs\Ollama\ollama.exe'))
    $ollama = Find-Executable 'ollama.exe' $ollamaCandidates
    if (!$ollama -and (Confirm-Setup 'Browse for an existing Ollama executable? Select No to install Ollama.')) {
        $ollama = Browse-Executable 'Locate Ollama' 'ollama.exe'
    }
    if (!$ollama) {
        Install-Dependency 'Ollama.Ollama'
        $ollama = Find-Executable 'ollama.exe' $ollamaCandidates
    }
    if (!$ollama) { throw 'Ollama was not found after setup.' }
    Configure-Ollama $ollama $modelDir $profile

    $blender = Find-Executable 'blender.exe' @(Get-BlenderCandidates)
    if (!$blender -and (Confirm-Setup 'Browse for an existing Blender executable? Select No to install Blender.')) {
        $blender = Browse-Executable 'Locate Blender' 'blender.exe'
    }
    if (!$blender) {
        Install-Dependency 'BlenderFoundation.Blender'
        $blender = Find-Executable 'blender.exe' @(Get-BlenderCandidates)
    }
    if (!$blender) { throw 'Blender was not found after setup.' }
    Invoke-Checked $blender @('--version') 'Blender did not start.'

    if (!(Get-WebView)) { Install-Dependency 'Microsoft.EdgeWebView2Runtime' }
    if (!(Get-WebView)) { throw 'WebView2 Runtime was not detected.' }
    if (!(Get-Command uvx.exe -ErrorAction SilentlyContinue) -or !(Get-Command uv.exe -ErrorAction SilentlyContinue)) { Install-Dependency 'astral-sh.uv' }
    $uv = (Get-Command uv.exe).Source
    $uvx = (Get-Command uvx.exe).Source

    $codeRoot = Join-Path $runtimes 'OpenCode'
    $code = Find-Executable 'opencode.exe' @($env:JARVIS_OPENCODE, (Join-Path $codeRoot 'node_modules\@opencode\cli\bin\opencode.exe'))
    if (!$code -and (Confirm-Setup 'Browse for an existing OpenCode 2.4.1 executable? Select No to install it.')) {
        $code = Browse-Executable 'Locate OpenCode 2.4.1' 'opencode.exe'
    }
    if (!$code -or ((& $code --version | Out-String) -notmatch '(?<![\d.])2\.4\.1(?![\d.])')) {
        if (!(Get-Command npm.cmd -ErrorAction SilentlyContinue)) { Install-Dependency 'OpenJS.NodeJS.LTS' }
        New-Item -Path $codeRoot -ItemType Directory -Force | Out-Null
        Invoke-Checked 'npm.cmd' @('install','--prefix',$codeRoot,'--registry',$registry,'@opencode/cli@2.4.1') 'OpenCode installation failed.'
        $code = Join-Path $codeRoot 'node_modules\@opencode\cli\bin\opencode.exe'
    }
    if (!(Test-Path -LiteralPath $code) -or ((& $code --version | Out-String) -notmatch '(?<![\d.])2\.4\.1(?![\d.])')) {
        throw 'OpenCode 2.4.1 was not detected.'
    }

    $exe = Join-Path $installDir 'Jarvis.exe'
    $scratch = Split-Path $Payload
    $extracted = Join-Path $scratch 'app'
    [IO.Compression.ZipFile]::ExtractToDirectory($Payload, $extracted)
    if (Get-Process -Name Jarvis -ErrorAction SilentlyContinue | Where-Object Path -EQ $exe) {
        throw 'Close this installed copy of Jarvis_X and run setup again.'
    }
    $env:JARVIS_UV_CACHE = Join-Path $runtimes 'uv-cache'
    $env:JARVIS_UV_PYTHON = Join-Path $runtimes 'python'
    $env:UV_CACHE_DIR = $env:JARVIS_UV_CACHE
    $env:UV_PYTHON_INSTALL_DIR = $env:JARVIS_UV_PYTHON
    $env:PYTHONUTF8 = '1'
    $env:ANONYMIZED_TELEMETRY = 'false'
    $env:DISABLE_TELEMETRY = 'true'
    $env:BLENDERMCP_ADDONS_DIR = Join-Path $extracted 'runtime\blender-scripts\addons'
    $env:JARVIS_RUN_SCOPE = Join-Path $scratch 'inactive-scope.json'
    New-Item -Path $env:BLENDERMCP_ADDONS_DIR -ItemType Directory -Force | Out-Null
    $connector = Join-Path $extracted 'blender_mcp_offline.py'
    $blenderArgs = @('--python','3.14','--from','mcp-for-blender==2.3.1','python',$connector)
    Invoke-Checked $uvx ($blenderArgs + 'install-addon') 'Blender add-on setup failed.'
    Invoke-Checked $uvx (@('--offline') + $blenderArgs + '--verify-guard') 'Blender connector verification failed.'
    Push-Location -LiteralPath $extracted
    try {
        Invoke-Checked $uvx @('--python','3.14','--from','windows-mcp==0.9.0','python','-c',"import runpy; runpy.run_path('desktop_mcp.py')") 'Desktop connector setup failed.'
    } finally { Pop-Location }
    $sdk = Join-Path $homeDir 'sdk\bpy-5.2.2'
    $sdkPython = Join-Path $sdk 'Scripts\python.exe'
    if (!(Test-Path -LiteralPath $sdkPython)) { Invoke-Checked $uv @('venv','--python','3.13',$sdk) 'Blender SDK environment setup failed.' }
    if (!(& $sdkPython -c "import importlib.util; print('yes' if importlib.util.find_spec('bpy') else 'no')")) {
        throw 'The Blender SDK interpreter did not start.'
    }
    $bpyVersion = & $sdkPython -c "import importlib.metadata as m; print(next((p.version for p in m.distributions() if p.name == 'bpy'), 'missing'))"
    if ($bpyVersion -ne '5.2.2') {
        Invoke-Checked $uv @('pip','install','--python',$sdkPython,'--index-url','https://pypi.org/simple','bpy==5.2.2') 'Blender SDK install failed.'
    }
    Invoke-Checked $sdkPython @((Join-Path $extracted 'sdk_probe.py')) 'Blender SDK save and reopen verification failed.'

    $installed = @((Invoke-RestMethod "$ollamaUrl/api/tags" -TimeoutSec 8).models | ForEach-Object name)
    if ($installed -notcontains 'qwen3.5-pilot:4b' -and $installed -notcontains $baseline) {
        $models = @($baseline) + $models
    }
    $env:OLLAMA_HOST = $ollamaUrl
    foreach ($tag in $models) {
        if ($installed -contains $tag) { continue }
        Invoke-Checked $ollama @('pull',$tag) "Could not download $tag. Run setup again to retry."
        $installed = @((Invoke-RestMethod "$ollamaUrl/api/tags" -TimeoutSec 8).models | ForEach-Object name)
        if ($installed -notcontains $tag) { throw "Ollama did not list $tag after download." }
    }

    New-Item -Path $installDir -ItemType Directory -Force | Out-Null
    Get-ChildItem -LiteralPath $extracted | Copy-Item -Destination $installDir -Recurse -Force
    $config = @{ JARVIS_OPENCODE = $code; JARVIS_BLENDER = $blender; JARVIS_UVX = $uvx
        JARVIS_UV_CACHE = $env:JARVIS_UV_CACHE; JARVIS_UV_PYTHON = $env:JARVIS_UV_PYTHON }
    $config | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installDir 'jarvis-install.json') -Encoding UTF8
    New-Shortcut ([Environment]::GetFolderPath('Programs')) $exe
    if ($desktop) { New-Shortcut ([Environment]::GetFolderPath('DesktopDirectory')) $exe }
    [void][Windows.Forms.MessageBox]::Show('Jarvis_X is ready.', $caption)
    Start-Process -FilePath $exe -WorkingDirectory $installDir
} catch {
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, $caption, 'OK', 'Error')
    Write-Error $_
    exit 1
}
