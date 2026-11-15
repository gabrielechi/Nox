# Builds NOX.app for Apple Silicon from Windows and packs it in a .zip that keeps the Unix
# permissions (the executable bit is required on macOS). The app is not signed by Apple:
# see LEGGIMI.txt for the one-time commands to run on the Mac.
# Usage, from the repository root:  powershell -ExecutionPolicy Bypass -File Zkt\Client.MacOS\macOS\publish-from-windows.ps1
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$project = Join-Path $root 'Zkt\Client.MacOS\Client.MacOS.csproj'
$out = Join-Path $root 'publish\NOX-macOS-arm64'
$app = Join-Path $out 'NOX.app'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c Release -r osx-arm64 --self-contained true -p:DebugType=None -o (Join-Path $out 'bin')
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$macos = Join-Path $app 'Contents\MacOS'
$resources = Join-Path $app 'Contents\Resources'
New-Item -ItemType Directory -Force $macos, $resources | Out-Null
Copy-Item (Join-Path $out 'bin\*') $macos -Recurse
Remove-Item (Join-Path $out 'bin') -Recurse -Force
Copy-Item (Join-Path $root 'Zkt\Client.MacOS\Assets\NOX.icns') $resources
$plist = (Get-Content (Join-Path $root 'Zkt\Client.MacOS\macOS\Info.plist') -Raw).Replace('__VERSION__', $version)
[IO.File]::WriteAllText((Join-Path $app 'Contents\Info.plist'), $plist, (New-Object Text.UTF8Encoding $false))
Copy-Item (Join-Path $root 'Zkt\Client.MacOS\macOS\LEGGIMI.txt') $out

Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# Zip with Unix permissions: 0755 for the executable and native libraries, 0644 for the rest.
# Only the files under $folder whose relative path matches $include are packed.
function New-UnixZip([string] $zip, [string] $folder, [string] $include) {
    if (Test-Path $zip) { Remove-Item $zip -Force }
    $archive = [IO.Compression.ZipFile]::Open($zip, 'Create')
    try {
        foreach ($file in Get-ChildItem $folder -Recurse -File) {
            $name = $file.FullName.Substring($folder.Length + 1).Replace('\', '/')
            if ($name -notlike $include) { continue }
            $entry = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $name, 'Optimal')
            $executable = $name -like 'NOX.app/Contents/MacOS/*' -and ($file.Extension -eq '' -or $file.Extension -eq '.dylib')
            $mode = if ($executable) { 0x81ED } else { 0x81A4 }   # regular file + rwxr-xr-x / rw-r--r--
            $entry.ExternalAttributes = $mode -shl 16
        }
    }
    finally { $archive.Dispose() }

    # Windows writes "version made by = MS-DOS" in the central directory, and macOS then ignores the
    # Unix permissions above (NOX would be extracted without the executable bit and refuse to open).
    # Mark every entry as made on Unix (host byte = 3).
    $bytes = [IO.File]::ReadAllBytes($zip)
    $eocd = -1
    for ($i = $bytes.Length - 22; $i -ge 0; $i--) {
        if ([BitConverter]::ToUInt32($bytes, $i) -eq 0x06054b50) { $eocd = $i; break }
    }
    if ($eocd -lt 0) { throw 'End of central directory not found' }
    $count = [BitConverter]::ToUInt16($bytes, $eocd + 10)
    $position = [BitConverter]::ToUInt32($bytes, $eocd + 16)
    for ($n = 0; $n -lt $count; $n++) {
        if ([BitConverter]::ToUInt32($bytes, $position) -ne 0x02014b50) { throw 'Invalid central directory entry' }
        $bytes[$position + 5] = 3
        $nameLength = [BitConverter]::ToUInt16($bytes, $position + 28)
        $extraLength = [BitConverter]::ToUInt16($bytes, $position + 30)
        $commentLength = [BitConverter]::ToUInt16($bytes, $position + 32)
        $position += 46 + $nameLength + $extraLength + $commentLength
    }
    [IO.File]::WriteAllBytes($zip, $bytes)
}

# 1. Zip to hand out directly (app + LEGGIMI.txt), with the version in the name.
$zip = Join-Path (Split-Path $out) "NOX-macOS-arm64-$version.zip"
New-UnixZip $zip $out '*'

# 2. The three files to attach to the GitHub release, with the fixed names used by NOX and
#    install-nox.sh: NOX-macOS-arm64.zip (NOX.app only), its .sha256 and install-nox.sh.
$release = Join-Path (Split-Path $out) "NOX-macOS-arm64-release-$version"
if (Test-Path $release) { Remove-Item $release -Recurse -Force }
New-Item -ItemType Directory -Force $release | Out-Null
$releaseZip = Join-Path $release 'NOX-macOS-arm64.zip'
New-UnixZip $releaseZip $out 'NOX.app/*'
$hash = (Get-FileHash $releaseZip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$releaseZip.sha256", "$hash  NOX-macOS-arm64.zip`n", (New-Object Text.UTF8Encoding $false))
Copy-Item (Join-Path $root 'Zkt\Client.MacOS\macOS\install-nox.sh') $release

Write-Host "Pronto: $zip"
Write-Host "Da allegare alla release GitHub: $release"
