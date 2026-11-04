#!/usr/bin/env python3
"""Offline normalization of the approved walking sheets; requires Pillow."""
from pathlib import Path
from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
SOURCES = ROOT / "docs/assets/sources"


def prepare(name):
    sheet = Image.open(SOURCES / ("rocketeer-walk-exposure-final.png" if name == "rocketeer" else f"{name}-walk-generated.png")).convert("RGBA")
    assert sheet.size == (2172, 724) and sheet.getpixel((0, 0))[3] == 0
    contents = []
    for n in range(4):
        cell = sheet.crop((n * 543, 0, (n + 1) * 543, 724))
        bounds = cell.getchannel("A").point(lambda a: 255 if a > 8 else 0).getbbox()
        assert bounds is not None
        contents.append(cell.crop(bounds))
    scale = 480 / max(max(frame.size) for frame in contents)
    frames = []
    for content in contents:
        resized = content.resize(tuple(round(v * scale) for v in content.size), Image.Resampling.LANCZOS)
        temporary = Image.new("RGBA", (480, 480))
        temporary.alpha_composite(resized, ((480 - resized.width) // 2, 480 - resized.height))
        # A top-down gait pivots around the torso, not the lowest moving boot.
        helmet = [(x, y) for y in range(100, 310) for x in range(195, 275)
                  if (lambda c: c[0] > 100 and c[2] > 90 and min(c[0], c[2]) > c[1] * 1.8 and c[3] > 128)(temporary.getpixel((x, y)))]
        assert len(helmet) > 300
        anchor = tuple(round(sum(p[a] for p in helmet) / len(helmet)) for a in (0, 1))
        offset = (256 - anchor[0], 232 - anchor[1])
        bounds = temporary.getchannel("A").point(lambda a: 255 if a > 20 else 0).getbbox()
        assert bounds[0] + offset[0] >= 0 and bounds[1] + offset[1] >= 0
        assert bounds[2] + offset[0] <= 512 and bounds[3] + offset[1] <= 512
        frame = Image.new("RGBA", (512, 512))
        frame.alpha_composite(temporary, offset)
        frames.append(frame)
    atlas = Image.new("RGBA", (2048, 512))
    for n, frame in enumerate(frames):
        atlas.alpha_composite(frame, (n * 512, 0))
    atlas.save(ROOT / f"sandbox/assets/units/{name}-walk.png")
    return frames


if __name__ == "__main__":
    animations = [prepare(name) for name in ("rifleman", "rocketeer")]
    previews = []
    for n in range(4):
        preview = Image.new("RGB", (512, 256), (39, 49, 44))
        for actor, animation in enumerate(animations):
            frame = animation[n].resize((256, 256), Image.Resampling.LANCZOS)
            preview.paste(frame, (actor * 256, 0), frame)
        previews.append(preview)
    previews[0].save(ROOT / "docs/assets/infantry-walk-preview.gif", save_all=True,
                     append_images=previews[1:], duration=125, loop=0)
