"""Technical sprite normalization. Requires Pillow; preserves alpha and aspect ratio."""
import hashlib
import json
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
MANIFEST = ROOT / "docs/assets/weapon-stage-art.json"

def main():
    data = json.loads(MANIFEST.read_text())
    for item in data["assets"]:
        source = ROOT / item["source"]
        if hashlib.sha256(source.read_bytes()).hexdigest() != item["source_sha256"]:
            raise ValueError(f"Source hash differs: {source}")
        image = Image.open(source).convert("RGBA")
        bounds = image.getchannel("A").point(lambda a: 255 if a > 128 else 0).getbbox()
        if bounds is None:
            raise ValueError(f"Empty sprite: {source}")
        image = image.crop(bounds)
        image.thumbnail((1160, 1160), Image.Resampling.LANCZOS)
        canvas = Image.new("RGBA", (1254, 1254))
        canvas.alpha_composite(image, ((1254 - image.width) // 2, (1254 - image.height) // 2))
        target = ROOT / item["asset"]
        canvas.save(target)
        actual = hashlib.sha256(target.read_bytes()).hexdigest()
        if actual != item["sha256"]:
            raise ValueError(f"Normalized output differs: {target}")
        print(f"PASS {item['asset']}")

if __name__ == "__main__":
    main()
