"""
Generate audio embeddings for a directory of mp3 tracks using the MAEST model.

Usage:
    python embed.py [audio_dir] [output_file]

    audio_dir    Directory containing .mp3 files (default: current directory)
    output_file  Path to the output JSON file (default: embeddings.json)
"""

import os
import json
import sys
import random

import numpy as np
import torch
from pydub import AudioSegment
from maest_infer import get_maest

MAEST_ARCH = "discogs-maest-30s-pw-129e-519l"
MAX_TRACKS = 1_000_000  # effectively "no limit" unless overridden


def parse_args() -> tuple[str, str]:
    """Read audio directory and output file path from CLI args, with defaults."""
    audio_dir = sys.argv[1] if len(sys.argv) > 1 else "."
    output_file = sys.argv[2] if len(sys.argv) > 2 else "embeddings.json"
    return audio_dir, output_file


def load_mp3(path: str, duration_sec: int = 40) -> torch.Tensor:
    """Load an mp3 file as a mono 16kHz float tensor normalized to [-1, 1]."""
    audio = AudioSegment.from_mp3(path)
    audio = audio.set_frame_rate(16000).set_channels(1)
    # audio = audio[:1000 * 20]

    samples = np.array(audio.get_array_of_samples()).astype(np.float32)
    samples /= np.iinfo(audio.array_type).max
    return torch.tensor(samples)


def load_model():
    """Load and prepare the MAEST model for inference."""
    print("Loading MAEST model...")
    model = get_maest(arch=MAEST_ARCH)
    model.eval()
    return model


def list_mp3_files(audio_dir: str) -> list[str]:
    """Return all .mp3 filenames in audio_dir."""
    return [f for f in os.listdir(audio_dir) if f.endswith(".mp3")]


def select_files(all_files: list[str], limit: int) -> list[str]:
    """Randomly sample up to `limit` files from the full list."""
    return random.sample(all_files, min(limit, len(all_files)))


def extract_embedding(model, audio: torch.Tensor) -> list[float]:
    """Run the model on an audio tensor and return a flat embedding vector."""
    with torch.no_grad():
        _, embeddings = model(audio)
        emb = embeddings.squeeze()

        if emb.dim() == 1:
            return emb.tolist()
        return emb.mean(dim=0).tolist()


def save_results(results: list[dict], output_file: str) -> None:
    """Atomically write results to output_file via a temp file + rename."""
    tmp_file = output_file + ".tmp"
    with open(tmp_file, "w", encoding="utf-8") as f:
        json.dump(results, f)
    os.replace(tmp_file, output_file)


def process_files(model, audio_dir: str, files: list[str], output_file: str) -> list[dict]:
    """Embed each file in turn, saving progress after every track."""
    results: list[dict] = []
    total = len(files)

    for idx, filename in enumerate(files, 1):
        path = os.path.join(audio_dir, filename)
        print(f"[{idx}/{total}] {filename}")

        try:
            audio = load_mp3(path)
            vec = extract_embedding(model, audio)

            results.append({
                "file": filename,
                "embedding": vec,
            })

            save_results(results, output_file)

        except Exception as ex:
            print(f"  Error: {ex}")

    return results


def main() -> None:
    audio_dir, output_file = parse_args()

    model = load_model()

    all_files = list_mp3_files(audio_dir)
    files = select_files(all_files, MAX_TRACKS)
    print(f"Total tracks: {len(all_files)}, processing: {len(files)}")

    results = process_files(model, audio_dir, files, output_file)

    print(f"\nDone. Saved to {output_file}")
    print(f"Embedding dimension: {len(results[0]['embedding']) if results else 'N/A'}")


if __name__ == "__main__":
    main()
