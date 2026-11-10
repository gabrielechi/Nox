#!/bin/bash
# Installs (or updates) NOX on a Mac with Apple Silicon, straight from the Terminal:
#
#     curl -fsSL https://raw.githubusercontent.com/gabrielechi/Nox/main/install-nox.sh | bash
#
# Uninstall:
#
#     curl -fsSL https://raw.githubusercontent.com/gabrielechi/Nox/main/install-nox.sh | bash -s -- --uninstall
#
# Files downloaded with curl are not marked with com.apple.quarantine, so Gatekeeper does not
# block the ad hoc signed app. The script asks the GitHub API for the newest published release of
# gabrielechi/Nox that carries NOX-macOS-arm64.zip (releases with only the Windows setup are
# skipped), downloads that zip, checks it against the SHA-256 digest published by GitHub, copies
# NOX.app into /Applications (or ~/Applications when /Applications is not writable) and opens it.
# Settings in ~/Library/Application Support/NOX are never touched.
#
# The script itself lives in the root of the main branch of gabrielechi/Nox (the command above),
# so it does not depend on which release is marked "Latest". A copy is also attached to each Mac
# release, because NOX downloads it from there when it updates itself.
#
# NOX itself runs this script to update (MainWindow.Updates.cs): it has already downloaded and
# verified the zip, passes it with NOX_ZIP_FILE/NOX_ZIP_SHA256, installs over its own bundle
# (NOX_INSTALL_DIR) and quits; the script waits for that process (NOX_WAIT_PID) to end first.
#
# Options (environment variables):
#   NOX_BASE_URL         downloads NOX-macOS-arm64.zip and its .sha256 from this HTTPS folder
#                        instead of the GitHub releases (e.g. a test server)
#   NOX_SKIP_CHECKSUM=1  with NOX_BASE_URL, installs even without a .sha256 file (not recommended)
#   NOX_NO_OPEN=1        does not open NOX at the end
#   NOX_INSTALL_DIR      installs into this folder instead of /Applications
#   NOX_ZIP_FILE         installs this local zip instead of downloading it (needs NOX_ZIP_SHA256)
#   NOX_ZIP_SHA256       expected SHA-256 of NOX_ZIP_FILE
#   NOX_WAIT_PID         waits for this process (the NOX being updated) to exit before installing
#   NOX_LOG              appends all output to this file
#   NOX_GUI=1            shows errors in a macOS alert (no Terminal when NOX starts the update)
#
# Everything lives inside main(), called on the last line: if the download of this script is
# cut short, bash runs nothing.

main() {
    set -euo pipefail
    export PATH="/usr/bin:/bin:/usr/sbin:/sbin:$PATH"

    local REPO="gabrielechi/Nox"
    local RELEASES_API="https://api.github.com/repos/$REPO/releases?per_page=30"
    local DOWNLOAD_PREFIX="https://github.com/$REPO/releases/download/"

    local ASSET="NOX-macOS-arm64.zip"
    local APP_NAME="NOX.app"

    if [ -n "${NOX_LOG:-}" ]; then
        mkdir -p "$(dirname "$NOX_LOG")" && exec >>"$NOX_LOG" 2>&1
        echo "---- $(date '+%Y-%m-%d %H:%M:%S') install-nox.sh ${1:-}"
    fi

    say()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
    warn() { printf '\033[1;33mAttenzione:\033[0m %s\n' "$*" >&2; }
    die() {
        printf '\033[1;31mErrore:\033[0m %s\n' "$*" >&2
        if [ "${NOX_GUI:-}" = "1" ]; then
            osascript -e 'on run argv' \
                -e 'display alert "Aggiornamento di NOX non riuscito" message (item 1 of argv) as critical' \
                -e 'end run' "$*" >/dev/null 2>&1 || true
        fi
        exit 1
    }

    [ "$(uname -s)" = "Darwin" ] || die "questo script funziona solo su macOS."
    # hw.optional.arm64 is 1 on Apple Silicon even when the shell runs under Rosetta.
    [ "$(sysctl -n hw.optional.arm64 2>/dev/null || echo 0)" = "1" ] \
        || die "NOX per macOS richiede un Mac con processore Apple Silicon (M1 o successivi)."

    quit_running_nox() {
        if pgrep -x NOX >/dev/null 2>&1; then
            say "Chiudo NOX in esecuzione..."
            osascript -e 'tell application "NOX" to quit' >/dev/null 2>&1 || true
            local i
            for i in 1 2 3 4 5 6 7 8 9 10; do
                pgrep -x NOX >/dev/null 2>&1 || return 0
                sleep 0.5
            done
            pkill -x NOX 2>/dev/null || true
            sleep 1
        fi
    }

    if [ "${1:-}" = "--uninstall" ]; then
        quit_running_nox
        local removed=0 dir dirs=("/Applications" "$HOME/Applications")
        [ -z "${NOX_INSTALL_DIR:-}" ] || dirs=("$NOX_INSTALL_DIR")
        for dir in "${dirs[@]}"; do
            if [ -d "$dir/$APP_NAME" ]; then
                rm -rf "${dir:?}/$APP_NAME" || die "non riesco a rimuovere $dir/$APP_NAME."
                say "Rimosso $dir/$APP_NAME"
                removed=1
            fi
        done
        [ "$removed" = 1 ] || say "NOX non risulta installato."
        say "Le impostazioni restano in ~/Library/Application Support/NOX (cancellale a mano se non servono)."
        return 0
    fi
    [ -z "${1:-}" ] || die "opzione sconosciuta: $1 (unica opzione: --uninstall)"

    local TMP
    TMP="$(mktemp -d -t nox-install)"
    # shellcheck disable=SC2064
    trap "rm -rf '$TMP'" EXIT

    local ZIP expected actual
    if [ -n "${NOX_ZIP_FILE:-}" ]; then
        # Update started by NOX: the zip is already on disk, checked again here before installing.
        ZIP="$NOX_ZIP_FILE"
        [ -f "$ZIP" ] || die "file di aggiornamento non trovato: $ZIP"
        expected="$(printf '%s' "${NOX_ZIP_SHA256:-}" | tr 'A-F' 'a-f')"
        [ "${#expected}" = 64 ] || die "manca lo SHA-256 atteso del file di aggiornamento (NOX_ZIP_SHA256)."
        actual="$(shasum -a 256 "$ZIP" | awk '{print $1}')"
        [ "$expected" = "$actual" ] || die "lo SHA-256 del file di aggiornamento non corrisponde. Aggiornamento annullato."
        say "SHA-256 verificato."
    elif [ -n "${NOX_BASE_URL:-}" ]; then
        # Test server: a plain HTTPS folder with the zip and its .sha256.
        local BASE_URL="${NOX_BASE_URL%/}"
        case "$BASE_URL" in
            https://*) ;;
            *) die "l'indirizzo di download deve essere HTTPS: $BASE_URL" ;;
        esac
        ZIP="$TMP/$ASSET"

        say "Scarico NOX..."
        curl -fL --proto '=https' --tlsv1.2 --progress-bar -o "$ZIP" "$BASE_URL/$ASSET" \
            || die "download non riuscito da $BASE_URL/$ASSET"

        if curl -fsSL --proto '=https' --tlsv1.2 -o "$TMP/$ASSET.sha256" "$BASE_URL/$ASSET.sha256"; then
            expected="$(awk '{print tolower($1); exit}' "$TMP/$ASSET.sha256")"
            actual="$(shasum -a 256 "$ZIP" | awk '{print $1}')"
            [ "${#expected}" = 64 ] || die "il file $ASSET.sha256 non contiene un hash SHA-256 valido."
            [ "$expected" = "$actual" ] || die "lo SHA-256 dello zip non corrisponde: file corrotto o manomesso. Installazione annullata."
            say "SHA-256 verificato."
        elif [ "${NOX_SKIP_CHECKSUM:-}" = "1" ]; then
            warn "nessun file $ASSET.sha256: installo senza verificare l'integrità (NOX_SKIP_CHECKSUM=1)."
        else
            die "manca $BASE_URL/$ASSET.sha256: non posso verificare il download."
        fi
    else
        # GitHub: newest published release (not draft, not pre-release) that carries the Mac zip.
        say "Cerco l'ultima versione per Mac..."
        curl -fsSL --proto '=https' --tlsv1.2 -H 'Accept: application/vnd.github+json' \
            -A 'NOX-Install-Script' -o "$TMP/releases.json" "$RELEASES_API" \
            || die "impossibile contattare GitHub ($RELEASES_API)."

        # The JSON is read with JavaScript for Automation (osascript), present on every macOS.
        # Output: tag, zip URL, SHA-256 digest published by GitHub, URL of the .sha256 asset (one per line).
        local found TAG URL DIGEST SUM_URL
        found="$(osascript -l JavaScript -e '
function run(argv) {
    ObjC.import("Foundation");
    var text = $.NSString.stringWithContentsOfFileEncodingError(argv[0], $.NSUTF8StringEncoding, null);
    var releases = JSON.parse(ObjC.unwrap(text));
    var asset = argv[1], prefix = argv[2];
    for (var i = 0; i < releases.length; i++) {
        var release = releases[i];
        if (release.draft || release.prerelease) continue;
        var zip = null, sum = null;
        (release.assets || []).forEach(function (a) {
            var url = a.browser_download_url;
            if (typeof url !== "string" || url.indexOf(prefix) !== 0) return;
            if (a.name === asset) zip = a;
            if (a.name === asset + ".sha256") sum = a;
        });
        if (!zip) continue;
        var digest = /^sha256:([0-9a-fA-F]{64})$/.exec(zip.digest || "");
        return [release.tag_name, zip.browser_download_url,
                digest ? digest[1].toLowerCase() : "", sum ? sum.browser_download_url : ""].join("\n");
    }
    return "";
}' "$TMP/releases.json" "$ASSET" "$DOWNLOAD_PREFIX")" || die "risposta di GitHub non leggibile."
        { IFS= read -r TAG; IFS= read -r URL; IFS= read -r DIGEST; IFS= read -r SUM_URL; } <<<"$found" || true
        [ -n "${URL:-}" ] || die "nessuna release pubblicata di $REPO contiene $ASSET."
        case "$URL" in
            "$DOWNLOAD_PREFIX"*) ;;
            *) die "indirizzo di download inatteso: $URL" ;;
        esac

        if [ -n "${DIGEST:-}" ]; then
            expected="$DIGEST"
        elif [ -n "${SUM_URL:-}" ]; then
            curl -fsSL --proto '=https' --tlsv1.2 -o "$TMP/$ASSET.sha256" "$SUM_URL" \
                || die "download non riuscito di $ASSET.sha256."
            expected="$(awk '{print tolower($1); exit}' "$TMP/$ASSET.sha256")"
        else
            die "la release $TAG non pubblica lo SHA-256 di $ASSET: installazione annullata."
        fi
        [ "${#expected}" = 64 ] || die "SHA-256 atteso non valido per la release $TAG."

        ZIP="$TMP/$ASSET"
        say "Scarico NOX $TAG..."
        curl -fL --proto '=https' --tlsv1.2 --progress-bar -o "$ZIP" "$URL" \
            || die "download non riuscito da $URL"
        actual="$(shasum -a 256 "$ZIP" | awk '{print $1}')"
        [ "$expected" = "$actual" ] || die "lo SHA-256 dello zip non corrisponde: file corrotto o manomesso. Installazione annullata."
        say "SHA-256 verificato."
    fi

    say "Estraggo..."
    mkdir "$TMP/x"
    ditto -x -k "$ZIP" "$TMP/x" || die "lo zip è danneggiato."
    local SRC
    SRC="$(find "$TMP/x" -maxdepth 3 -type d -name "$APP_NAME" -print -quit)"
    [ -n "$SRC" ] && [ -f "$SRC/Contents/MacOS/NOX" ] || die "lo zip non contiene $APP_NAME."

    local DEST_DIR="${NOX_INSTALL_DIR:-/Applications}"
    if [ -n "${NOX_INSTALL_DIR:-}" ]; then
        mkdir -p "$DEST_DIR" || die "non riesco a creare $DEST_DIR."
    elif [ ! -w "$DEST_DIR" ]; then
        DEST_DIR="$HOME/Applications"
        mkdir -p "$DEST_DIR"
        warn "/Applications non è scrivibile da questo utente: installo in $DEST_DIR."
    fi
    [ -w "$DEST_DIR" ] || die "non ho i permessi per scrivere in $DEST_DIR."
    local DEST="$DEST_DIR/$APP_NAME"

    # Prepare the new bundle next to the old one, so the swap below is two renames.
    local NEW="$DEST_DIR/.NOX.app.new-$$" OLD="$DEST_DIR/.NOX.app.old-$$"
    rm -rf "$NEW" "$OLD"
    ditto "$SRC" "$NEW" || { rm -rf "$NEW"; die "non riesco a copiare NOX in $DEST_DIR."; }
    chmod +x "$NEW/Contents/MacOS/NOX"
    xattr -dr com.apple.quarantine "$NEW" 2>/dev/null || true
    codesign --force --deep --sign - "$NEW" >/dev/null 2>&1 \
        || { rm -rf "$NEW"; die "firma locale dell'app non riuscita (codesign)."; }

    if [ -n "${NOX_WAIT_PID:-}" ]; then
        say "Attendo la chiusura di NOX (processo $NOX_WAIT_PID)..."
        local waited=0
        while kill -0 "$NOX_WAIT_PID" 2>/dev/null; do
            [ "$waited" -lt 120 ] || { rm -rf "$NEW"; die "NOX non si è chiuso: aggiornamento annullato."; }
            sleep 0.5
            waited=$((waited + 1))
        done
    else
        quit_running_nox
    fi

    if [ -d "$DEST" ]; then
        say "Sostituisco la versione già installata..."
        mv "$DEST" "$OLD" || { rm -rf "$NEW"; die "non riesco a sostituire la versione in $DEST."; }
    fi
    if ! mv "$NEW" "$DEST"; then
        [ -d "$OLD" ] && mv "$OLD" "$DEST"
        rm -rf "$NEW"
        die "non riesco a installare NOX in $DEST."
    fi
    rm -rf "$OLD"
    [ -z "${NOX_ZIP_FILE:-}" ] || rm -f "$NOX_ZIP_FILE"

    local VERSION
    VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$DEST/Contents/Info.plist" 2>/dev/null || echo '?')"
    say "NOX $VERSION installato in $DEST"

    if [ "${NOX_NO_OPEN:-}" != "1" ]; then
        open "$DEST" || warn "apri NOX da $DEST_DIR."
    fi
}

main "$@"
