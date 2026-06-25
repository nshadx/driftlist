# driftlist

An autoplaylist engine that builds endless, mood-aware listening sessions from your local mp3 library.

Instead of shuffling randomly or following fixed playlists, driftlist picks each next track based on how
closely it matches the "mood" of what you've been listening to recently. The mood drifts over time as you
listen, so a session can wander from one sound to another while still feeling coherent — like a long DJ set
that reacts to its own momentum.

## How it works

1. **Embeddings** (`embed.py`) — every track in your library is run through the [MAEST](https://github.com/palonso/MAEST)
   audio model, which produces a vector embedding capturing its musical characteristics. These are saved to
   `embeddings.json`.
2. **Playback** (`driftlist/`, a C# console app) — on startup you pick a track to begin with. Its embedding
   becomes the initial "mood vector." After each track:
   - The mood vector is updated using an exponential moving average (EMA) toward the embedding of the track
     that just played, so recent listening has more influence than older listening.
   - If the next track is too dissimilar from the current mood (cosine similarity below a threshold), the
     mood resets to that track instead of blending — this handles abrupt genre switches.
   - The next track is sampled (not just picked greedily) using softmax-with-temperature over cosine
     similarity to the mood vector, filtered with top-p, with already-played tracks excluded.

This means every session is different, but each one stays thematically consistent while still being free to
drift.

## Project structure

```
driftlist/
├── run.sh              # setup + run script for Linux/macOS
├── run.ps1             # setup + run script for Windows
├── embed.py            # generates embeddings.json from your mp3 library
├── embeddings.json      # generated on first run (gitignored)
├── .venv/               # Python virtual environment (gitignored)
├── music/               # default location for your mp3 files (gitignored)
└── driftlist/
    ├── driftlist.csproj
    └── Program.cs       # the playback engine
```

## Requirements

The run scripts install most of this for you automatically where possible, but here's what's needed:

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Python 3.10+
- [ffmpeg](https://ffmpeg.org/) (used by `pydub` to decode mp3 files)
- [VLC](https://www.videolan.org/vlc/) / libVLC (used by `LibVLCSharp` for audio playback)

## Usage

Put your mp3 files in a folder (by default, `./music` next to the scripts), then run:

**Linux / macOS**
```bash
./run.sh                       # uses ./music
./run.sh /path/to/your/music   # or point at a specific folder
./run.sh --force-embed         # regenerate embeddings.json even if it already exists
```

**Windows (PowerShell)**
```powershell
.\run.ps1
.\run.ps1 -MusicDir "C:\path\to\your\music"
.\run.ps1 -ForceEmbed
```

On first run, the script will:
1. Check for and install .NET SDK, Python, ffmpeg, and VLC if missing.
2. Patch `driftlist.csproj` to add the right native LibVLC package for your OS (the project ships with
   the Windows package by default).
3. Create a Python virtual environment and install `torch`, `numpy`, `pydub`, and `maest-infer`.
4. Generate `embeddings.json` from your music folder (skipped on later runs unless `embeddings.json` is
   missing or `--force-embed` / `-ForceEmbed` is passed).
5. Build and launch the driftlist player.

Embedding generation can take a while depending on your library size and hardware — progress is printed
per track, and results are saved incrementally so an interrupted run doesn't lose previous progress.

## Controls

Once a session starts, the player runs in your terminal:

| Key     | Action                                   |
|---------|-------------------------------------------|
| `Enter` | Play the next track, chosen by the mood-drift algorithm |
| `M`     | Manually pick a specific track to play next |
| `S`     | Stop the session and return to track selection |
| `X`     | Exit the player |

## Notes

- Embeddings are tied to file names, so renaming mp3 files after generating `embeddings.json` will break
  the match between entries and files — regenerate with `--force-embed` / `-ForceEmbed` if your library
  changes.
- The Python dependencies (`torch` in particular) are installed CPU-only by default. If you want GPU
  acceleration, install a CUDA-enabled build of `torch` into `.venv` manually before running embeddings.
- `maest-infer` downloads pretrained model weights automatically on first use and caches them in
  `~/.cache/torch/hub/checkpoints/`.
