#!/usr/bin/env bash
#
# run.sh — установка зависимостей, генерация эмбеддингов и запуск driftlist
#
# Использование:
#   ./run.sh [путь_к_папке_с_mp3] [--force-embed]
#
# Если путь не указан — используется ./music рядом со скриптом.
# --force-embed заставляет пересчитать embeddings.json, даже если он уже есть.

set -euo pipefail

# ---------- Пути ----------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET_PROJ_DIR="$SCRIPT_DIR/driftlist"
CSPROJ="$DOTNET_PROJ_DIR/driftlist.csproj"
EMBED_PY="$SCRIPT_DIR/embed.py"
VENV_DIR="$SCRIPT_DIR/.venv"
EMBEDDINGS_FILE="$SCRIPT_DIR/embeddings.json"
DEFAULT_MUSIC_DIR="$SCRIPT_DIR/music"

# ---------- Разбор аргументов ----------
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

# ---------- Вспомогательные функции ----------
log()  { printf '\033[1;36m[run]\033[0m %s\n' "$1"; }
warn() { printf '\033[1;33m[warn]\033[0m %s\n' "$1"; }
err()  { printf '\033[1;31m[error]\033[0m %s\n' "$1" >&2; }

OS="$(uname -s)"
case "$OS" in
    Linux*)  PLATFORM="linux" ;;
    Darwin*) PLATFORM="macos" ;;
    *)       err "Неизвестная платформа: $OS. Скрипт поддерживает Linux и macOS (для Windows используйте run.ps1)."; exit 1 ;;
esac

log "Платформа: $PLATFORM"

# ---------- Проверка наличия .NET SDK ----------
check_dotnet() {
    if command -v dotnet >/dev/null 2>&1; then
        log "Найден dotnet: $(dotnet --version 2>/dev/null || echo 'версия не определена')"
        return 0
    fi
    return 1
}

if ! check_dotnet; then
    warn ".NET SDK не найден."
    if [[ "$PLATFORM" == "macos" ]]; then
        if command -v brew >/dev/null 2>&1; then
            log "Устанавливаю .NET SDK через Homebrew..."
            brew install --cask dotnet-sdk
        else
            err "Homebrew не найден. Установите .NET 10 SDK вручную: https://dotnet.microsoft.com/download"
            exit 1
        fi
    else
        if command -v apt-get >/dev/null 2>&1; then
            log "Устанавливаю .NET SDK через apt..."
            sudo apt-get update
            sudo apt-get install -y dotnet-sdk-10.0 || {
                warn "Пакет dotnet-sdk-10.0 не найден в стандартных репозиториях."
                warn "Установите .NET 10 SDK вручную: https://dotnet.microsoft.com/download"
                exit 1
            }
        else
            err "Не удалось определить пакетный менеджер. Установите .NET 10 SDK вручную: https://dotnet.microsoft.com/download"
            exit 1
        fi
    fi
fi

# ---------- Проверка наличия Python 3.10+ ----------
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
    warn "Python 3.10+ не найден."
    if [[ "$PLATFORM" == "macos" ]]; then
        if command -v brew >/dev/null 2>&1; then
            log "Устанавливаю Python через Homebrew..."
            brew install python@3.12
            PYTHON_BIN="python3.12"
        else
            err "Homebrew не найден. Установите Python 3.10+ вручную."
            exit 1
        fi
    else
        if command -v apt-get >/dev/null 2>&1; then
            log "Устанавливаю Python через apt..."
            sudo apt-get update
            sudo apt-get install -y python3 python3-venv python3-pip
            PYTHON_BIN="python3"
        else
            err "Не удалось определить пакетный менеджер. Установите Python 3.10+ вручную."
            exit 1
        fi
    fi
fi

log "Использую интерпретатор: $PYTHON_BIN ($($PYTHON_BIN --version))"

# ---------- Системный ffmpeg (нужен pydub для чтения mp3) ----------
if ! command -v ffmpeg >/dev/null 2>&1; then
    warn "ffmpeg не найден (нужен pydub для декодирования mp3)."
    if [[ "$PLATFORM" == "macos" ]]; then
        if command -v brew >/dev/null 2>&1; then
            log "Устанавливаю ffmpeg через Homebrew..."
            brew install ffmpeg
        else
            err "Homebrew не найден. Установите ffmpeg вручную."
            exit 1
        fi
    else
        if command -v apt-get >/dev/null 2>&1; then
            log "Устанавливаю ffmpeg через apt..."
            sudo apt-get update
            sudo apt-get install -y ffmpeg
        else
            err "Не удалось определить пакетный менеджер. Установите ffmpeg вручную."
            exit 1
        fi
    fi
else
    log "ffmpeg найден."
fi

# ---------- Системный libVLC (нужен для воспроизведения треков биноварём) ----------
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
    warn "Системная библиотека libVLC не найдена (нужна LibVLCSharp для воспроизведения)."
    if [[ "$PLATFORM" == "macos" ]]; then
        if command -v brew >/dev/null 2>&1; then
            log "Устанавливаю VLC через Homebrew..."
            brew install --cask vlc
        else
            err "Homebrew не найден. Установите VLC вручную: https://www.videolan.org/vlc/"
            exit 1
        fi
    else
        if command -v apt-get >/dev/null 2>&1; then
            log "Устанавливаю libvlc через apt..."
            sudo apt-get update
            sudo apt-get install -y libvlc-dev vlc
        else
            err "Не удалось определить пакетный менеджер. Установите VLC/libvlc вручную."
            exit 1
        fi
    fi
else
    log "libVLC найден."
fi

# ---------- Патчим csproj: добавляем нативные пакеты LibVLC под Linux/macOS ----------
patch_csproj() {
    if grep -q "VideoLAN.LibVLC.Mac" "$CSPROJ" 2>/dev/null && grep -q "VideoLAN.LibVLC.Linux" "$CSPROJ" 2>/dev/null; then
        log "csproj уже содержит условные пакеты под macOS/Linux, пропускаю патч."
        return
    fi

    log "Добавляю в csproj условные PackageReference для macOS/Linux..."

    # Заменяем безусловный пакет VideoLAN.LibVLC.Windows на версию с Condition,
    # и добавляем условные пакеты для Mac и Linux.
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
    # Безусловной строки нет (возможно, уже пропатчено иначе) — вставляем перед </ItemGroup>
    content = content.replace("</ItemGroup>", new_block + "\n    </ItemGroup>", 1)

with open(path, "w", encoding="utf-8") as f:
    f.write(content)

print("csproj обновлён.")
PYEOF
}

patch_csproj

# ---------- Python venv и зависимости ----------
if [[ ! -d "$VENV_DIR" ]]; then
    log "Создаю виртуальное окружение Python в .venv..."
    "$PYTHON_BIN" -m venv "$VENV_DIR"
fi

# shellcheck disable=SC1091
source "$VENV_DIR/bin/activate"

log "Устанавливаю Python-зависимости (torch, pydub, numpy, maest-infer)..."
pip install --upgrade pip --quiet
pip install torch numpy pydub maest-infer --quiet

# ---------- Генерация эмбеддингов ----------
if [[ ! -d "$MUSIC_DIR" ]]; then
    err "Папка с музыкой не найдена: $MUSIC_DIR"
    err "Передайте путь явно: ./run.sh /путь/к/музыке"
    deactivate
    exit 1
fi

if [[ -f "$EMBEDDINGS_FILE" && "$FORCE_EMBED" -eq 0 ]]; then
    log "Найден существующий $EMBEDDINGS_FILE — пропускаю генерацию эмбеддингов."
    log "(чтобы пересчитать — запустите с флагом --force-embed)"
else
    log "Генерирую эмбеддинги из $MUSIC_DIR ..."
    python "$EMBED_PY" "$MUSIC_DIR" "$EMBEDDINGS_FILE"
fi

deactivate

# ---------- Сборка и запуск .NET бинаря ----------
log "Собираю driftlist (dotnet build)..."
dotnet build "$CSPROJ" -c Release

log "Запускаю driftlist..."
dotnet run --project "$CSPROJ" -c Release -- "$EMBEDDINGS_FILE" "$MUSIC_DIR"
