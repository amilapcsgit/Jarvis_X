$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    New-Item -ItemType Directory -Force builtin, runtime/blender-scripts/addons | Out-Null
    $files = @{
        'gemma-3-270m-it-q4_k_m.gguf' = 'gguf-org/gemma-3-270m-it-gguf/resolve/0845b7e54ef2673cc691ab508e3ac6da7e392ad9'
        'embeddinggemma-2-Q8_0.gguf' = 'ggml-org/embeddinggemma-2-GGUF/resolve/6ea3e3a309d12a4779cfda98b8c19800d2d5da71'
    }
    foreach ($name in $files.Keys) {
        if (!(Test-Path "builtin/$name")) {
            curl.exe --fail --location --retry 3 -o "builtin/$name.tmp" "https://huggingface.co/$($files[$name])/$name"
            if ($LASTEXITCODE) { throw "Download failed for $name" }
            Move-Item -LiteralPath "builtin/$name.tmp" -Destination "builtin/$name"
        }
    }
    $release = Invoke-RestMethod 'https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/b12815'
    $asset = $release.assets | Where-Object name -eq 'llama-b12815-bin-win-cpu-x64.zip'
    if (!$asset.digest) { throw 'Runtime asset digest unavailable' }
    $archive = Join-Path $env:TEMP ('jarvis-llama-' + [guid]::NewGuid() + '.zip')
    try {
        curl.exe --fail --location --retry 3 -o $archive $asset.browser_download_url
        if ($LASTEXITCODE -or ('sha256:' + (Get-FileHash $archive).Hash).ToLowerInvariant() -ne $asset.digest) {
            throw 'Runtime archive integrity check failed'
        }
        Expand-Archive -LiteralPath $archive -DestinationPath builtin -Force
    } finally {
        if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive }
    }
    $env:BLENDERMCP_ADDONS_DIR = Join-Path $PSScriptRoot 'runtime/blender-scripts/addons'
    if (!(Get-Command uvx -ErrorAction SilentlyContinue)) {
        python -m pip install uv
        if ($LASTEXITCODE) { throw 'Could not install uv' }
    }
    uvx --python 3.14 --from mcp-for-blender==2.3.1 python blender_mcp_offline.py install-addon
    if ($LASTEXITCODE -or !(Test-Path 'runtime/blender-scripts/addons/blender_mcp.py')) { throw 'Blender bridge preparation failed' }
} finally { Pop-Location }
