#!/usr/bin/env bash
#
# run.sh - install dependencies, generate embeddings, and run driftlist
#
# Usage:
#   ./run.sh [path_to_mp3_folder] [--force-embed]
#
# If no path is given, ./music next to this script is used.
# --force-embed forces embeddings.json to be regenerated even if it exists.

set -euo pipefail

# ---------- Paths ----------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET_PROJ_DIR="$SCRIPT_DIR/driftlist"
CSPROJ="$DOTNET_PROJ_DIR/driftlist.csproj"
EMBED_PY="$SCRIPT_DIR/embed.py"
VENV_DIR="$SCRIPT_DIR/.venv"
EMBEDDINGS_FILE="$SCRIPT_DIR/embeddings.json"
DEFAULT_MUSIC_DIR="$SCRIPT_DIR/music"

# ---------- Argument parsing ----------
MUSIC_DIR=""
FORCE_EMBED=0

for arg in "$@"; do
    case "$arg" in
        --force-embed)
            FORCE_EMBED=1
            ;;
        *)
            MUSIC_DIR="$arg"
            ;;
    esac
done

if [[ -z "$MUSIC_DIR" ]]; then
    MUSIC_DIR="$DEFAULT_MUSIC_DIR"
fi

# ---------- Helpers ----------
log()  { printf '\033[1;36m[run]\033[0m %s\n' "$1"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$1"; }
err()  { printf '\033[1;31m[error]\033[0m %s\n' "$1" >&2; }

OS="$(uname -s)"
case "$OS" in
    Linux*)  PLATFORM="linux" ;;
    Darwin*) PLATFORM="macos" ;;
    *)       err "Unsupported platform: $OS. This script supports Linux and macOS (use run.ps1 for Windows)."; exit 1 ;;
esac

log "Platform: $PLATFORM"

# ---------- .NET SDK ----------
check_dotnet() {
    if command -v dotnet >/dev/null 2>&1; then
        log "Found dotnet: $(dotnet --version 2>/dev/null || echo 'version unknown')"
        return 0
    fi
    return 1
}

if ! check_dotnet; then
    warn ".NET SDK not found."
    if [[ "$PLATFORM" == "macos" ]]; then
        if command -v brew >/dev/null 2>&1; then
            log "Installing .NET SDK via Homebrew..."
            brew install --cask dotnet-sdk
        else
            err "Homebrew not found. Install .NET 10 SDK manually: https://dotnet.microsoft.com/download"
            exit 1
        fi
    else
        if command -v apt-get >/dev/null 2>&1; then
            log "Installing .NET SDK via apt..."
            sudo apt-get update
            sudo apt-get install -y dotnet-sdk-10.0 || {
                warn "Package dotnet-sdk-10.0 not found in default repositories."
                warn "Install .NET 10 SDK manually: https://dotnet.microsoft.com/download"
                exit 1
            }
        else
            err "Could not detect a package manager. Install .NET 10 SDK manually: https://dotnet.microsoft.com/download"
            exit 1
        fi
    fi
fi

# ---------- Python 3.10+ ----------
PYTHON_BIN=""
for candidate in python3.12 python3.11 python3.10 python3; do
    if command -v "$candidate" >/dev/null 2>&1; then
        ver="$("$candidate" -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
        major="${ver%%.*}"
        minor="${ver##*.}"
        if [[ "$major" -eq 3 && "$minor" -ge 10 ]]; then
            PYTHON_BIN="$candidate"
            break
        fi
    fi
done

if [[ -z "$PYTHON_BIN" ]]; then
    warn "Python 3.10+ not found."
    if [[ "$PLATFORM" == "macos" ]]; then
        if command -v brew >/dev/null 2>&1; then
            log "Installing Python via Homebrew..."
            brew install python@3.12
            PYTHON_BIN="python3.12"
        else
            err "Homebrew not found. Install Python 3.10+ manually."
            exit 1
        fi
    else
        if command -v apt-get >/dev/null 2>&1; then
            log "Installing Python via apt..."
            sudo apt-get update
            sudo apt-get install -y python3 python3-venv python3-pip
            PYTHON_BIN="python3"
        else
            err "Could not detect a package manager. Install Python 3.10+ manually."
            exit 1
        fi
    fi
fi

log "Using interpreter: $PYTHON_BIN ($($PYTHON_BIN --version))"

# ---------- ffmpeg (required by pydub to decode mp3) ----------
if ! command -v ffmpeg >/dev/null 2>&1; then
    warn "ffmpeg not found (required by pydub to decode mp3 files)."
    if [[ "$PLATFORM" == "macos" ]]; then
        if command -v brew >/dev/null 2>&1; then
            log "Installing ffmpeg via Homebrew..."
            brew install ffmpeg
        else
            err "Homebrew not found. Install ffmpeg manually."
            exit 1
        fi
    else
        if command -v apt-get >/dev/null 2>&1; then
            log "Installing ffmpeg via apt..."
            sudo apt-get update
            sudo apt-get install -y ffmpeg
        else
            err "Could not detect a package manager. Install ffmpeg manually."
            exit 1
        fi
    fi
else
    log "ffmpeg found."
fi

# ---------- libVLC (required by LibVLCSharp for playback) ----------
check_libvlc() {
    if [[ "$PLATFORM" == "macos" ]]; then
        [[ -d "/Applications/VLC.app" ]] && return 0
        brew list --cask vlc >/dev/null 2>&1 && return 0
        return 1
    else
        ldconfig -p 2>/dev/null | grep -q libvlc.so && return 0
        return 1
    fi
}

if ! check_libvlc; then
    warn "System libVLC not found (required by LibVLCSharp for playback)."
    if [[ "$PLATFORM" == "macos" ]]; then
        if command -v brew >/dev/null 2>&1; then
            log "Installing VLC via Homebrew..."
            brew install --cask vlc
        else
            err "Homebrew not found. Install VLC manually: https://www.videolan.org/vlc/"
            exit 1
        fi
    else
        if command -v apt-get >/dev/null 2>&1; then
            log "Installing libvlc via apt..."
            sudo apt-get update
            sudo apt-get install -y libvlc-dev vlc
        else
            err "Could not detect a package manager. Install VLC/libvlc manually."
            exit 1
        fi
    fi
else
    log "libVLC found."
fi

# ---------- Patch csproj: add native LibVLC packages for Linux/macOS ----------
patch_csproj() {
    if grep -q "VideoLAN.LibVLC.Mac" "$CSPROJ" 2>/dev/null && grep -q "VideoLAN.LibVLC.Linux" "$CSPROJ" 2>/dev/null; then
        log "csproj already has conditional macOS/Linux packages, skipping patch."
        return
    fi

    log "Adding conditional PackageReference entries to csproj for macOS/Linux..."

    # Replace the unconditional VideoLAN.LibVLC.Windows reference with a conditional one,
    # and add conditional packages for Mac and Linux.
    python3 - "$CSPROJ" <<'PYEOF'
import re
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8") as f:
    content = f.read()

old_line_pattern = re.compile(
    r'\s*<PackageReference Include="VideoLAN\.LibVLC\.Windows" Version="[^"]+"\s*/>'
)

new_block = (
    '\n      <PackageReference Include="VideoLAN.LibVLC.Windows" Version="3.0.23.1" '
    'Condition="$([MSBuild]::IsOSPlatform(\'Windows\'))" />\n'
    '      <PackageReference Include="VideoLAN.LibVLC.Mac" Version="3.1.3.1" '
    'Condition="$([MSBuild]::IsOSPlatform(\'OSX\'))" />\n'
    '      <PackageReference Include="VideoLAN.LibVLC.Linux" Version="3.0.0" '
    'Condition="$([MSBuild]::IsOSPlatform(\'Linux\'))" />'
)

if old_line_pattern.search(content):
    content = old_line_pattern.sub(new_block, content, count=1)
else:
    # No unconditional line found (maybe already patched differently) - insert before </ItemGroup>
    content = content.replace("</ItemGroup>", new_block + "\n    </ItemGroup>", 1)

with open(path, "w", encoding="utf-8") as f:
    f.write(content)

print("csproj updated.")
PYEOF
}

patch_csproj

# ---------- Python venv and dependencies ----------
if [[ ! -d "$VENV_DIR" ]]; then
    log "Creating Python virtual environment in .venv..."
    "$PYTHON_BIN" -m venv "$VENV_DIR"
fi

# shellcheck disable=SC1091
source "$VENV_DIR/bin/activate"

log "Installing Python dependencies (torch, pydub, numpy, maest-infer)..."
python -m pip install --upgrade pip --quiet
python -m pip install torch numpy pydub maest-infer --quiet

# ---------- Generate embeddings ----------
if [[ ! -d "$MUSIC_DIR" ]]; then
    err "Music folder not found: $MUSIC_DIR"
    err "Pass the path explicitly: ./run.sh /path/to/music"
    deactivate
    exit 1
fi

if [[ -f "$EMBEDDINGS_FILE" && "$FORCE_EMBED" -eq 0 ]]; then
    log "Found existing $EMBEDDINGS_FILE - skipping embedding generation."
    log "(use --force-embed to regenerate)"
else
    log "Generating embeddings from $MUSIC_DIR ..."
    python "$EMBED_PY" "$MUSIC_DIR" "$EMBEDDINGS_FILE"
fi

deactivate

# ---------- Build and run the .NET binary ----------
log "Building driftlist (dotnet build)..."
dotnet build "$CSPROJ" -c Release

log "Running driftlist..."
dotnet run --project "$CSPROJ" -c Release -- "$EMBEDDINGS_FILE" "$MUSIC_DIR"
