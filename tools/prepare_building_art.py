"""Reproduce technical normalization from recorded originals. Requires Pillow."""
import hashlib
import io
import json
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent


def main():
    data = json.loads((ROOT / 'docs/assets/buildings-art.json').read_text())
    for item in data['assets']:
        source = ROOT / item['source']
        if hashlib.sha256(source.read_bytes()).hexdigest() != item['source_sha256']:
            raise ValueError('Source hash differs: ' + str(source))
        image = Image.open(source).convert('RGBA')
        if item['name'].endswith('-strip'):
            side = image.width // 3
            if image.width != side * 3 or image.height != side:
                raise ValueError('Expected three equal square animation cells')
            canvas = Image.new('RGBA', (1536, 512))
            for i in range(3):
                frame = image.crop((i * side, 0, (i + 1) * side, side))
                if item['name'] == 'door-strip':
                    frame = frame.crop((4, 4, side - 4, side - 4)).resize((480, 480), Image.Resampling.LANCZOS)
                    canvas.alpha_composite(frame, (i * 512 + 16, 16))
                else:
                    frame = frame.resize((512, 512), Image.Resampling.LANCZOS)
                    canvas.alpha_composite(frame, (i * 512, 0))
        else:
            bounds = image.getchannel('A').point(lambda a: 255 if a > 128 else 0).getbbox()
            if bounds is None:
                raise ValueError('Empty sprite: ' + str(source))
            image = image.crop(bounds)
            factor = min(1, 928 / max(image.size))
            image = image.resize((round(image.width * factor), round(image.height * factor)), Image.Resampling.LANCZOS)
            canvas = Image.new('RGBA', (1024, 1024))
            canvas.alpha_composite(image, ((1024 - image.width) // 2, (1024 - image.height) // 2))
        target = ROOT / item['asset']
        # Check in memory before touching an approved output.
        output = io.BytesIO()
        canvas.save(output, format='PNG')
        if hashlib.sha256(output.getvalue()).hexdigest() != item['sha256']:
            raise ValueError('Normalized output differs: ' + str(target))
        target.write_bytes(output.getvalue())
        print('PASS ' + item['asset'])


if __name__ == '__main__':
    main()
