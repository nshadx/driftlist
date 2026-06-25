# run.ps1 — установка зависимостей, генерация эмбеддингов и запуск driftlist (Windows)
#
# Использование:
#   .\run.ps1 [-MusicDir "C:\путь\к\музыке"] [-ForceEmbed]
#
# Если -MusicDir не указан — используется .\music рядом со скриптом.
# -ForceEmbed заставляет пересчитать embeddings.json, даже если он уже есть.

param(
    [string]$MusicDir = "",
    [switch]$ForceEmbed
)

$ErrorActionPreference = "Stop"

function Log($msg)  { Write-Host "[run] $msg" -ForegroundColor Cyan }
function Warn($msg) { Write-Host "[warn] $msg" -ForegroundColor Yellow }
function Err($msg)  { Write-Host "[error] $msg" -ForegroundColor Red }

$ScriptDir       = Split-Path -Parent $MyInvocation.MyCommand.Path
$DotnetProjDir   = Join-Path $ScriptDir "driftlist"
$Csproj          = Join-Path $DotnetProjDir "driftlist.csproj"
$EmbedPy         = Join-Path $ScriptDir "embed.py"
$VenvDir         = Join-Path $ScriptDir ".venv"
$EmbeddingsFile  = Join-Path $ScriptDir "embeddings.json"
$DefaultMusicDir = Join-Path $ScriptDir "music"

if ([string]::IsNullOrWhiteSpace($MusicDir)) {
    $MusicDir = $DefaultMusicDir
}

# ---------- Проверка прав на установку через winget ----------
function Test-Command($name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

# ---------- .NET SDK ----------
if (Test-Command "dotnet") {
    $dotnetVer = (dotnet --version) 2>$null
    Log "Найден dotnet: $dotnetVer"
} else {
    Warn ".NET SDK не найден."
    if (Test-Command "winget") {
        Log "Устанавливаю .NET 10 SDK через winget..."
        winget install --id Microsoft.DotNet.SDK.10 -e --accept-package-agreements --accept-source-agreements
    } else {
        Err "winget не найден. Установите .NET 10 SDK вручную: https://dotnet.microsoft.com/download"
        exit 1
    }
}

# ---------- Python 3.10+ ----------
$PythonBin = $null
foreach ($candidate in @("python", "py")) {
    if (Test-Command $candidate) {
        try {
            $verOutput = & $candidate -c "import sys; print('%d.%d' % sys.version_info[:2])" 2>$null
            if ($verOutput) {
                $parts = $verOutput.Split(".")
                $major = [int]$parts[0]
                $minor = [int]$parts[1]
                if ($major -eq 3 -and $minor -ge 10) {
                    $PythonBin = $candidate
                    break
                }
            }
        } catch {}
    }
}

if (-not $PythonBin) {
    Warn "Python 3.10+ не найден."
    if (Test-Command "winget") {
        Log "Устанавливаю Python через winget..."
        winget install --id Python.Python.3.12 -e --accept-package-agreements --accept-source-agreements
        $PythonBin = "python"
    } else {
        Err "winget не найден. Установите Python 3.10+ вручную: https://www.python.org/downloads/"
        exit 1
    }
}

Log "Использую интерпретатор: $PythonBin"

# ---------- ffmpeg (нужен pydub для чтения mp3) ----------
if (-not (Test-Command "ffmpeg")) {
    Warn "ffmpeg не найден (нужен pydub для декодирования mp3)."
    if (Test-Command "winget") {
        Log "Устанавливаю ffmpeg через winget..."
        winget install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements
        Warn "Возможно потребуется перезапустить терминал, чтобы PATH обновился."
    } else {
        Err "winget не найден. Установите ffmpeg вручную: https://ffmpeg.org/download.html"
        exit 1
    }
} else {
    Log "ffmpeg найден."
}

# ---------- csproj: на Windows пакет VideoLAN.LibVLC.Windows уже стоит безусловно ----------
# Проверим, не пропатчен ли он уже на условный — если да, оставляем (Condition для Windows
# тоже будет true), если нет — ничего делать не нужно, всё работает как есть.
Log "csproj использует VideoLAN.LibVLC.Windows — на Windows дополнительных правок не требуется."

# ---------- Python venv и зависимости ----------
if (-not (Test-Path $VenvDir)) {
    Log "Создаю виртуальное окружение Python в .venv..."
    & $PythonBin -m venv $VenvDir
}

$VenvPython = Join-Path $VenvDir "Scripts\python.exe"
$VenvPip    = Join-Path $VenvDir "Scripts\pip.exe"

Log "Устанавливаю Python-зависимости (torch, pydub, numpy, maest-infer)..."
& $VenvPip install --upgrade pip --quiet
& $VenvPip install torch numpy pydub maest-infer --quiet

# ---------- Генерация эмбеддингов ----------
if (-not (Test-Path $MusicDir)) {
    Err "Папка с музыкой не найдена: $MusicDir"
    Err "Передайте путь явно: .\run.ps1 -MusicDir 'C:\путь\к\музыке'"
    exit 1
}

if ((Test-Path $EmbeddingsFile) -and (-not $ForceEmbed)) {
    Log "Найден существующий $EmbeddingsFile — пропускаю генерацию эмбеддингов."
    Log "(чтобы пересчитать — запустите с флагом -ForceEmbed)"
} else {
    Log "Генерирую эмбеддинги из $MusicDir ..."
    & $VenvPython $EmbedPy $MusicDir $EmbeddingsFile
}

# ---------- Сборка и запуск .NET бинаря ----------
Log "Собираю driftlist (dotnet build)..."
dotnet build $Csproj -c Release

Log "Запускаю driftlist..."
dotnet run --project $Csproj -c Release -- $EmbeddingsFile $MusicDir
