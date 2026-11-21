"""Pack rendered infantry frames into directional Godot SpriteFrames for the isometric sandbox."""
import hashlib
import json
from pathlib import Path
from PIL import Image
import sys

rendered = Path(sys.argv[1])
sniper = "--sniper" in sys.argv[2:]
rocketeer = "--rocketeer" in sys.argv[2:]
assert not (sniper and rocketeer)
prefix = "rocketeer" if rocketeer else "sniper" if sniper else "swat"
repo = Path(__file__).resolve().parents[1]
asset_dir = repo / "sandbox/assets/units/isometric"
resource = repo / "sandbox/resources/units/isometric" / (prefix + ".tres" if sniper or rocketeer else "swat-rifleman.tres")
manifest = json.loads((rendered / "manifest.json").read_text())
cell = manifest["cell"]
gutter = 2
stride = cell + 2 * gutter
sprites = []
external = []
subresources = []
sequences = []

for clip, definition in manifest["clips"].items():
    count = len(definition["frames"])
    facing_count = len(manifest["facings"])
    atlas = Image.new("RGBA", (count * stride, facing_count * stride))
    assert atlas.width <= 8192 and atlas.height <= 8192
    atlas_name = prefix + "-" + clip + ".png"
    external.append(f'[ext_resource type="Texture2D" path="res://assets/units/isometric/{atlas_name}" id="{clip}"]')
    for direction in range(facing_count):
        refs = []
        for index, duration in enumerate(definition["durations"]):
            source = rendered / "frames" / clip / f"{direction}-{index:02}.png"
            with Image.open(source) as frame:
                rgba = frame.convert("RGBA")
            assert rgba.size == (cell, cell), source
            bounds = rgba.getchannel("A").point(lambda a: 255 if a > 20 else 0).getbbox()
            assert bounds and min(bounds[0], bounds[1], cell - bounds[2], cell - bounds[3]) > 0, source
            if not clip.endswith("-unarmed") or index == 0:
                team = sum(a > 120 and min(r, b) > 1.3 * g for r, g, b, a in rgba.get_flattened_data())
                assert team >= 8, (source, team)
            x, y = index * stride + gutter, direction * stride + gutter
            atlas.paste(rgba, (x, y))
            sub = f"{clip}_{direction}_{index}".replace("-", "_")
            subresources.append(
                f'[sub_resource type="AtlasTexture" id="{sub}"]\natlas = ExtResource("{clip}")\n'
                f'region = Rect2({x}, {y}, {cell}, {cell})\nfilter_clip = true'
            )
            refs.append(f'{{"duration": {float(duration):.1f}, "texture": SubResource("{sub}")}}')
        repeat = "true" if definition["loop"] else "false"
        rate = manifest["fps"] * (0.95 if clip == "travel" else 1)
        sequences.append(
            '{"frames": [' + ",\n".join(refs) + f'], "loop": {repeat}, '
            f'"name": &"{clip}-{direction}", "speed": {rate:.2f}' + "}"
        )
    atlas_path = asset_dir / atlas_name
    atlas.save(atlas_path, optimize=True)
    sprites.append(atlas_path)

# At import, the sampler reads the full atlas with the UV rect of only the inner cell.
header = f'[gd_resource type="SpriteFrames" load_steps={1 + len(external) + len(subresources)} format=3]'
resource.write_text("\n\n".join([header, "\n".join(external), *subresources,
    "[resource]\nanimations = [\n" + ",\n".join(sequences) + "\n]"]) + "\n")

glb = repo / "sandbox3d/assets/quaternius" / (prefix + ".glb" if sniper or rocketeer else "swat-armored.glb")
glb.write_bytes((rendered / glb.name).read_bytes())
manifest["source"] = ("GameAssets/characters/rocketeer-animated-v001/rocketeer-animated.blend" if rocketeer
                      else "GameAssets/characters/sniper-armored-v001/sniper-armored.blend" if sniper
                      else "GameAssets/characters/quaternius-infantry-v001/swat-armored.blend")
manifest["asset_sources"] = {
    "model": "https://quaternius.com/packs/ultimatemodularcharacters.html",
    "universal_animation_library": "https://quaternius.com/packs/universalanimationlibrary.html",
    "mixamo": "https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html",
}
manifest["outputs_sha256"] = {
    path.relative_to(repo).as_posix(): hashlib.sha256(path.read_bytes()).hexdigest()
    for path in [glb, resource, *sprites]
}
(repo / "docs/assets" / (prefix + "-infantry.json")).write_text(
    json.dumps(manifest, indent=2) + "\n")
print(f"Packed {len(sequences)} directional sequences into {len(sprites)} atlases.")
