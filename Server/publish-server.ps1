# Creates the NOX server ready to copy on a Windows or Linux machine (no .NET to install):
#   publish\NOX-Server-win-x64\     Server.exe + appsettings.json + README.txt
#   publish\NOX-Server-linux-x64\   Server     + appsettings.json + README.txt
# Usage, from the repository root:  powershell -ExecutionPolicy Bypass -File Zkt\Server\publish-server.ps1
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $root 'Zkt\Server\Server.csproj'

foreach ($runtime in 'win-x64', 'linux-x64') {
    $out = Join-Path $root "publish\NOX-Server-$runtime"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    # One executable (runtime and native SQLite included) next to appsettings.json and README.txt.
    dotnet publish $project -c Release -r $runtime --self-contained true -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
        -p:DebugType=None -p:DebugSymbols=false -o $out
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $runtime failed" }
    # web.config (IIS) and the static web assets manifest are not used by this package.
    Remove-Item (Join-Path $out 'web.config'), (Join-Path $out 'Server.staticwebassets.endpoints.json') -ErrorAction SilentlyContinue
    Write-Host "Ready: $out"
}
