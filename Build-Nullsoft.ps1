param([string] $Compiler, [string] $OutputDirectory = 'publish-nullsoft')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    if (!$Compiler) {
        $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
        $Compiler = if ($command) { $command.Source } else { "${env:ProgramFiles(x86)}\NSIS\makensis.exe" }
    }
    if (!(Test-Path -LiteralPath $Compiler)) { throw 'Install NSIS or provide the makensis.exe path.' }
    $assets = @{
        'gemma-3-270m-it-q4_k_m.gguf' = '55AED1673D2F3014A739732C659C4995FC57ABF6E4E9C68462FB2CEEE820EA2F'
        'embeddinggemma-2-Q8_0.gguf' = '2188AC1DECA4B77DFFEFD603C2776A9D76D9D74EC01841392982EBB840B09135'
    }
    foreach ($name in $assets.Keys) {
        if ((Get-FileHash -LiteralPath "builtin/$name").Hash -ne $assets[$name]) { throw "Model hash mismatch for $name" }
    }
    if (!(Test-Path 'builtin/llama-server.exe')) { throw 'The Windows CPU runtime is missing.' }
    $stage = Join-Path $PSScriptRoot ('obj/nullsoft/' + [guid]::NewGuid())
    $out = New-Item -ItemType Directory -Force -Path $OutputDirectory
    dotnet publish Jarvis.csproj --no-restore -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $stage
    if ($LASTEXITCODE) { throw 'Jarvis publish failed.' }
    Copy-Item -LiteralPath 'Install-Missing-Dependencies.bat','LICENSE' -Destination $stage
    Copy-Item -LiteralPath 'licenses' -Destination $stage -Recurse
    $output = Join-Path $out.FullName 'Jarvis_X_Setup.exe'
    & $Compiler "/DPAYLOAD=$stage" "/DOUTPUT=$output" 'installer/Jarvis.nsi'
    if ($LASTEXITCODE) { throw 'Nullsoft build failed.' }
    Get-Item -LiteralPath $output
} finally { Pop-Location }
