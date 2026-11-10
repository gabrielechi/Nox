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
# block the ad hoc signed app. The script downloads NOX-macOS-arm64.zip and its .sha256 from
# BASE_URL, checks the hash, copies NOX.app into /Applications (or ~/Applications when
# /Applications is not writable) and opens it. Settings in ~/Library/Application Support/NOX
# are never touched.
#
# Options (environment variables):
#   NOX_BASE_URL         overrides BASE_URL below (e.g. for a test server)
#   NOX_SKIP_CHECKSUM=1  installs even without a .sha256 file (not recommended)
#   NOX_NO_OPEN=1        does not open NOX at the end
#   NOX_INSTALL_DIR      installs into this folder instead of /Applications
#
# Everything lives inside main(), called on the last line: if the download of this script is
# cut short, bash runs nothing.

main() {
    set -euo pipefail

    # --- Set this to the public folder that holds NOX-macOS-arm64.zip and its .sha256 ---
    # GitHub release example: https://github.com/<utente>/<repo>/releases/latest/download
    local BASE_URL="${NOX_BASE_URL:-https://github.com/gabrielechi/Nox/releases/latest/download}"

    local ASSET="NOX-macOS-arm64.zip"
    local APP_NAME="NOX.app"

    say()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
    warn() { printf '\033[1;33mAttenzione:\033[0m %s\n' "$*" >&2; }
    die()  { printf '\033[1;31mErrore:\033[0m %s\n' "$*" >&2; exit 1; }

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

    case "$BASE_URL" in
        __NOX_BASE_URL__) die "indirizzo di download non configurato: imposta BASE_URL nello script o NOX_BASE_URL." ;;
        https://*) ;;
        *) die "l'indirizzo di download deve essere HTTPS: $BASE_URL" ;;
    esac
    BASE_URL="${BASE_URL%/}"

    local TMP
    TMP="$(mktemp -d -t nox-install)"
    # shellcheck disable=SC2064
    trap "rm -rf '$TMP'" EXIT

    say "Scarico NOX..."
    curl -fL --proto '=https' --tlsv1.2 --progress-bar -o "$TMP/$ASSET" "$BASE_URL/$ASSET" \
        || die "download non riuscito da $BASE_URL/$ASSET"

    if curl -fsSL --proto '=https' --tlsv1.2 -o "$TMP/$ASSET.sha256" "$BASE_URL/$ASSET.sha256"; then
        local expected actual
        expected="$(awk '{print tolower($1); exit}' "$TMP/$ASSET.sha256")"
        actual="$(shasum -a 256 "$TMP/$ASSET" | awk '{print $1}')"
        [ "${#expected}" = 64 ] || die "il file $ASSET.sha256 non contiene un hash SHA-256 valido."
        [ "$expected" = "$actual" ] || die "lo SHA-256 dello zip non corrisponde: file corrotto o manomesso. Installazione annullata."
        say "SHA-256 verificato."
    elif [ "${NOX_SKIP_CHECKSUM:-}" = "1" ]; then
        warn "nessun file $ASSET.sha256: installo senza verificare l'integrità (NOX_SKIP_CHECKSUM=1)."
    else
        die "manca $BASE_URL/$ASSET.sha256: non posso verificare il download."
    fi

    say "Estraggo..."
    mkdir "$TMP/x"
    ditto -x -k "$TMP/$ASSET" "$TMP/x" || die "lo zip è danneggiato."
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
    local DEST="$DEST_DIR/$APP_NAME"

    quit_running_nox
    if [ -d "$DEST" ]; then
        say "Sostituisco la versione già installata..."
        rm -rf "$DEST" || die "non riesco a rimuovere la versione precedente in $DEST."
    fi
    ditto "$SRC" "$DEST" || die "non riesco a copiare NOX in $DEST_DIR."

    chmod +x "$DEST/Contents/MacOS/NOX"
    xattr -dr com.apple.quarantine "$DEST" 2>/dev/null || true
    codesign --force --deep --sign - "$DEST" >/dev/null 2>&1 \
        || die "firma locale dell'app non riuscita (codesign)."

    local VERSION
    VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$DEST/Contents/Info.plist" 2>/dev/null || echo '?')"
    say "NOX $VERSION installato in $DEST"

    if [ "${NOX_NO_OPEN:-}" != "1" ]; then
        open "$DEST" || warn "apri NOX da $DEST_DIR."
    fi
}

main "$@"
