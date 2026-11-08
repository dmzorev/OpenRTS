#!/usr/bin/env python3
"""Normalize generated alpha sprites and record their provenance. Does not synthesize artwork."""
import hashlib
import json
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
manifest = ROOT / 'docs/assets/aviation-art.json'
data = json.loads(manifest.read_text())
for entry in data['assets']:
    source = ROOT / entry['source']
    digest = hashlib.sha256(source.read_bytes()).hexdigest()
    if entry.get('source_sha256', digest) != digest:
        raise ValueError('Changed art source: ' + str(source))
    picture = Image.open(source).convert('RGBA')
    bounds = picture.getchannel('A').point(lambda a: 255 if a > 128 else 0).getbbox()
    if bounds is None:
        raise ValueError('Empty sprite: ' + str(source))
    picture = picture.crop(bounds)
    scale = 928 / max(picture.size)
    picture = picture.resize((round(picture.width * scale), round(picture.height * scale)), Image.Resampling.LANCZOS)
    canvas = Image.new('RGBA', (1024, 1024))
    canvas.alpha_composite(picture, ((1024 - picture.width) // 2, (1024 - picture.height) // 2))
    destination = ROOT / entry['asset']
    destination.parent.mkdir(parents=True, exist_ok=True)
    canvas.save(destination)
    entry.update(source_sha256=digest, sha256=hashlib.sha256(destination.read_bytes()).hexdigest(),
                 normalization='alpha>128 bounds, aspect fit928 centered1024, preserve alpha')
    resource = ROOT / 'sandbox/resources' / destination.parent.name / (entry['name'] + '.tres')
    resource.parent.mkdir(parents=True, exist_ok=True)
    animations = ',\n'.join('{"frames": [{"duration": 1.0, "texture": ExtResource("1")}], "loop": true, "name": &"' + name + '", "speed": 1.0}' for name in ['stand', 'travel', 'shoot'])
    resource.write_text('[gd_resource type="SpriteFrames" load_steps=2 format=3]\n\n[ext_resource type="Texture2D" path="res://' + str(destination.relative_to(ROOT / 'sandbox')) + '" id="1"]\n\n[resource]\nanimations = [' + animations + ']\n')
manifest.write_text(json.dumps(data, indent=2, ensure_ascii=False) + '\n')
print('Prepared', len(data['assets']), 'aviation sprites and SpriteFrames')
