import os
import json
import sys
import random
import numpy as np
import torch
from pydub import AudioSegment
from maest_infer import get_maest

AUDIO_DIR = sys.argv[1] if len(sys.argv) > 1 else "."
OUTPUT_FILE = sys.argv[2] if len(sys.argv) > 2 else "embeddings.json"
LIMIT = 1_000_000

def load_mp3(path, duration_sec=40):
    audio = AudioSegment.from_mp3(path)
    audio = audio.set_frame_rate(16000).set_channels(1)
    #audio = audio[:1000 * 20]
    samples = np.array(audio.get_array_of_samples()).astype(np.float32)
    samples /= np.iinfo(audio.array_type).max
    return torch.tensor(samples)

print("Загружаю модель MAEST...")
model = get_maest(arch="discogs-maest-30s-pw-129e-519l")
model.eval()

results = []

all_files = [f for f in os.listdir(AUDIO_DIR) if f.endswith(".mp3")]
files = random.sample(all_files, min(LIMIT, len(all_files)))
total = len(files)
print(f"Треков всего: {len(all_files)}, обрабатываю: {total}")

for idx, filename in enumerate(files, 1):
    path = os.path.join(AUDIO_DIR, filename)
    print(f"[{idx}/{total}] {filename}")

    try:
        audio = load_mp3(path)

        with torch.no_grad():
            _, embeddings = model(audio)
            emb = embeddings.squeeze()
            if emb.dim() == 1:
                vec = emb.tolist()
            else:
                vec = emb.mean(dim=0).tolist()

        results.append({
            "file": filename,
            "embedding": vec
        })
        
        with open(OUTPUT_FILE + ".tmp", "w", encoding="utf-8") as f:
            json.dump(results, f)
        os.replace(OUTPUT_FILE + ".tmp", OUTPUT_FILE)

    except Exception as ex:
        print(f"  Ошибка: {ex}")

print(f"\nГотово. Сохранено в {OUTPUT_FILE}")
print(f"Размерность эмбеддинга: {len(results[0]['embedding']) if results else 'N/A'}")