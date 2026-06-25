# run.ps1 - install dependencies, generate embeddings, and run driftlist (Windows)
#
# Usage:
#   .\run.ps1 [-MusicDir "C:\path\to\music"] [-ForceEmbed]
#
# If -MusicDir is not given, .\music next to this script is used.
# -ForceEmbed forces embeddings.json to be regenerated even if it exists.

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

function Test-Command($name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

# ---------- .NET SDK ----------
if (Test-Command "dotnet") {
    $dotnetVer = (dotnet --version) 2>$null
    Log "Found dotnet: $dotnetVer"
} else {
    Warn ".NET SDK not found."
    if (Test-Command "winget") {
        Log "Installing .NET 10 SDK via winget..."
        winget install --id Microsoft.DotNet.SDK.10 -e --accept-package-agreements --accept-source-agreements
    } else {
        Err "winget not found. Install .NET 10 SDK manually: https://dotnet.microsoft.com/download"
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
    Warn "Python 3.10+ not found."
    if (Test-Command "winget") {
        Log "Installing Python via winget..."
        winget install --id Python.Python.3.12 -e --accept-package-agreements --accept-source-agreements
        $PythonBin = "python"
    } else {
        Err "winget not found. Install Python 3.10+ manually: https://www.python.org/downloads/"
        exit 1
    }
}

Log "Using interpreter: $PythonBin"

# ---------- ffmpeg (required by pydub to decode mp3) ----------
if (-not (Test-Command "ffmpeg")) {
    Warn "ffmpeg not found (required by pydub to decode mp3 files)."
    if (Test-Command "winget") {
        Log "Installing ffmpeg via winget..."
        winget install --id Gyan.FFmpeg -e --accept-package-agreements --accept-source-agreements
        Warn "You may need to restart your terminal for PATH changes to take effect."
    } else {
        Err "winget not found. Install ffmpeg manually: https://ffmpeg.org/download.html"
        exit 1
    }
} else {
    Log "ffmpeg found."
}

# ---------- csproj: on Windows, VideoLAN.LibVLC.Windows is already unconditional ----------
Log "csproj uses VideoLAN.LibVLC.Windows - no changes needed on Windows."

# ---------- Python venv and dependencies ----------
if (-not (Test-Path $VenvDir)) {
    Log "Creating Python virtual environment in .venv..."
    & $PythonBin -m venv $VenvDir
}

$VenvPython = Join-Path $VenvDir "Scripts\python.exe"

Log "Installing Python dependencies (torch, pydub, numpy, maest-infer)..."
& $VenvPython -m pip install --upgrade pip --quiet
& $VenvPython -m pip install torch numpy pydub maest-infer --quiet

# ---------- Generate embeddings ----------
if (-not (Test-Path $MusicDir)) {
    Err "Music folder not found: $MusicDir"
    Err "Pass the path explicitly: .\run.ps1 -MusicDir C:\path\to\music"
    exit 1
}

if ((Test-Path $EmbeddingsFile) -and (-not $ForceEmbed)) {
    Log "Found existing $EmbeddingsFile - skipping embedding generation."
    Log "(use -ForceEmbed to regenerate)"
} else {
    Log "Generating embeddings from $MusicDir ..."
    & $VenvPython $EmbedPy $MusicDir $EmbeddingsFile
}

# ---------- Build and run the .NET binary ----------
Log "Building driftlist (dotnet build)..."
dotnet build $Csproj -c Release

Log "Running driftlist..."
dotnet run --project $Csproj -c Release -- $EmbeddingsFile $MusicDir
