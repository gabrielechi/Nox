# NOX - installazione dall'ultima release GitHub
#
# Uso (PowerShell):
#   irm https://raw.githubusercontent.com/gabrielechi/Nox/main/install.ps1 | iex
# Uso (Prompt dei comandi / Win+R):
#   powershell -c "irm https://raw.githubusercontent.com/gabrielechi/Nox/main/install.ps1 | iex"
#
# Cosa fa: legge l'ultima release di gabrielechi/Nox, scarica l'installer NOX-Setup-x.y.exe,
# verifica l'impronta SHA-256 pubblicata da GitHub e avvia l'installazione.
# Lo script gira dentro un blocco & { } e usa "return": non chiude la finestra di PowerShell.

& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    } catch { }

    $repo = 'gabrielechi/Nox'
    $userAgent = 'NOX-Install-Script'

    function Write-Step([string] $text) { Write-Host "  $text" }
    function Write-Fail([string] $text) { Write-Host "  $text" -ForegroundColor Red }

    Write-Host ''
    Write-Host '  NOX - installazione' -ForegroundColor Green
    Write-Host ''

    # 1. Ultima release pubblicata (bozze e pre-release escluse da GitHub).
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/latest" -UserAgent $userAgent
    } catch {
        Write-Fail "Impossibile contattare GitHub: $($_.Exception.Message)"
        return
    }

    # 2. Installer allegato: solo NOX-Setup-*.exe scaricato da questo repository, con impronta SHA-256.
    $asset = $release.assets | Where-Object { $_.name -match '^NOX-Setup-[0-9A-Za-z.\-]+\.exe$' } | Select-Object -First 1
    if (-not $asset) {
        Write-Fail "Nessun installer trovato nella release $($release.tag_name)."
        return
    }
    if ($asset.browser_download_url -notlike "https://github.com/$repo/releases/download/*") {
        Write-Fail 'Indirizzo di download inatteso: installazione annullata.'
        return
    }
    if ("$($asset.digest)" -notmatch '^sha256:([0-9a-fA-F]{64})$') {
        Write-Fail 'La release non pubblica l''impronta SHA-256 dell''installer: installazione annullata.'
        return
    }
    $expected = $Matches[1]

    # 3. Download nella cartella temporanea dell'utente.
    $folder = Join-Path $env:TEMP 'NOX\Updates'
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $file = Join-Path $folder $asset.name
    $sizeMb = [math]::Round($asset.size / 1MB, 1)
    Write-Step "Versione: $($release.tag_name)"
    Write-Step "Download di $($asset.name) ($sizeMb MB)..."
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $file -UserAgent $userAgent -UseBasicParsing
    } catch {
        if (Test-Path $file) { Remove-Item $file -Force }
        Write-Fail "Download non riuscito: $($_.Exception.Message)"
        return
    }

    # 4. Verifica dell'impronta.
    $actual = (Get-FileHash -Path $file -Algorithm SHA256).Hash
    if ($actual -ne $expected) {
        Remove-Item $file -Force
        Write-Fail 'Il file scaricato non corrisponde a quello pubblicato ed e'' stato eliminato.'
        return
    }
    Write-Step 'File verificato (SHA-256).'

    # 5. Avvio dell'installer.
    Write-Host '  Avvio dell''installazione...' -ForegroundColor Green
    Start-Process -FilePath $file
}
