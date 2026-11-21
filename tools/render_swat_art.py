"""Export GameAssets infantry for sandbox3d and render eight directions for the sprite pipeline."""
import hashlib
import json
import math
from pathlib import Path
import sys

import bpy
from bpy_extras.object_utils import world_to_camera_view
from mathutils import Matrix, Vector

args = sys.argv[sys.argv.index("--") + 1:]
source, output = map(Path, args[:2])
sniper = "--sniper" in args[2:]
rocketeer = "--rocketeer" in args[2:]
grenade_only = "--grenade-only" in args[2:]
assert sum((sniper, rocketeer, grenade_only)) <= 1
model_name = "rocketeer" if rocketeer else "sniper" if sniper else "swat-armored"
output.mkdir(parents=True, exist_ok=True)
bpy.ops.wm.open_mainfile(filepath=str(source), use_scripts=False)
scene = bpy.context.scene
rig = bpy.data.objects["CharacterArmature"]
meshes = [obj for obj in scene.objects if obj.type == "MESH" and obj.parent == rig]
clips = {
    "stand": ("MixIdle", True), "travel": ("MixRunLowReady", True), "shoot": ("MixFire", False),
    "walk": ("MixWalk", True), "aim": ("MixAim", True), "run-raised": ("MixRunRaised", True),
    "jog-unarmed": ("UALJog", True), "run-unarmed": ("StockRun", True),
}
if rocketeer:
    clips = {"stand": ("RpgIdle", True), "travel": ("RpgRun", True),
             "prepare": ("RpgFire", False), "shoot": ("RpgFire", False)}
assert set(bpy.data.actions.keys()) == {action for action, _ in clips.values()}


def set_action(name):
    action = bpy.data.actions[name]
    rig.animation_data.action = action
    rig.animation_data.action_slot = action.slots[0]
    start, end = map(round, action.frame_range)
    scene.frame_set(start)
    return start, end


if not grenade_only:
    # Evaluate the source modifiers in the neutral rest pose, retaining skin weights.
    set_action(clips["stand"][0])
    rig.data.pose_position = "REST"
    bpy.ops.object.select_all(action="DESELECT")
    for obj in meshes:
        bpy.context.view_layer.objects.active = obj
        obj.select_set(True)
        for modifier in list(obj.modifiers):
            if modifier.type in {"MIRROR", "NODES", "WEIGHTED_NORMAL"}:
                assert bpy.ops.object.modifier_apply(modifier=modifier.name) == {"FINISHED"}
        obj.select_set(False)
    rig.data.pose_position = "POSE"
    scene.frame_set(0)
    rig.select_set(True)
    for obj in meshes:
        obj.select_set(True)
    bpy.context.view_layer.objects.active = rig
    assert bpy.ops.export_scene.gltf(
        filepath=str(output / (model_name + ".glb")),
        export_format="GLB", use_selection=True,
        export_animations=True, export_animation_mode="ACTIONS", export_frame_range=False,
        export_force_sampling=True, export_yup=True, export_morph=False,
    ) == {"FINISHED"}


# Fixed camera looking from the same quadrant as the isometric sandbox.
cell = 128
span = 2.85
scale = .72
pitch = math.atan(1 / math.sqrt(2))
camera = scene.camera
focus = Vector((0, 0, .84))
camera.location = focus + Vector((1, -1, 1)) * 6
camera.rotation_euler = (focus - camera.location).to_track_quat("-Z", "Y").to_euler()
camera.data.type = "ORTHO"
camera.data.ortho_scale = span
scene.render.engine = "BLENDER_WORKBENCH"
scene.display.render_aa = "FXAA"
scene.display.shading.light = "STUDIO"
scene.display.shading.studio_light = "paint.sl"
scene.display.shading.color_type = "MATERIAL"
scene.display.shading.show_shadows = False
scene.display.shading.show_cavity = False
scene.display.shading.show_object_outline = False
scene.view_settings.view_transform = "Standard"
scene.view_settings.exposure = .65
scene.render.image_settings.color_mode = "RGBA"
scene.render.film_transparent = True
scene.render.resolution_x = scene.render.resolution_y = cell
scene.render.resolution_percentage = 100
bpy.data.materials["TEAM_PRIMARY"].diffuse_color = (.58, .012, .58, 1.0)
bpy.context.view_layer.update()
anchor = world_to_camera_view(scene, camera, Vector())

# Measure the muzzle at the first firing frame, facing world north.
fire_action = clips["shoot"][0]
set_action(fire_action)
release = bpy.data.actions[fire_action].pose_markers["Launch"].frame if rocketeer else 0
scene.frame_set(release)
weapon = rig.pose.bones["RifleProp"]
muzzle_tip = (.649, 0, 0) if rocketeer else (.782, 0, .038) if sniper else (.4375, 0, .035)
muzzle = weapon.matrix @ weapon.bone.matrix_local.inverted() @ Vector(muzzle_tip)
muzzle_local = [-muzzle.x * scale, muzzle.z * scale, muzzle.y * scale]
info = dict(
    source_blend_sha256=hashlib.sha256(source.read_bytes()).hexdigest(), fps=scene.render.fps,
    facings=["N", "NE", "E", "SE", "S", "SW", "W", "NW"], cell=cell,
    camera_elevation_degrees=math.degrees(pitch), sprite_rotation=-45,
    size=[span*scale, span*scale/math.sin(pitch)], pivot=[anchor.x, 1-anchor.y],
    muzzle_local=muzzle_local, clips={},
)
if rocketeer:
    info["release_source_frame"] = release

for name, (action, loop) in (() if grenade_only else clips.items()):
    start, end = set_action(action)
    if rocketeer and name == "shoot":
        start = release
    if rocketeer and name == "prepare":
        end = release - 1
    frames = list(range(start, end if loop else end + 1, 2))
    if not loop and frames[-1] != end:
        frames.append(end)
    hold_until = end if loop else end + 1
    durations = [after - before for before, after in zip(frames, frames[1:] + [hold_until])]
    info["clips"][name] = dict(action=action, frames=frames, durations=durations, loop=loop)
    folder = output / "frames" / name
    folder.mkdir(parents=True, exist_ok=True)
    for direction in range(8):
        rig.rotation_euler.z = math.pi - direction * math.tau / 8
        for index, frame in enumerate(frames):
            scene.frame_set(frame)
            scene.render.filepath = str(folder / f"{direction}-{index:02}.png")
            bpy.ops.render.render(write_still=True)
    print("Rendered", name, len(frames), flush=True)

def render_grenade(info, comparison):
    with bpy.data.libraries.load(str(comparison), link=False) as (available, selected):
        assert "MixThrow" in available.actions
        selected.actions = ["MixThrow"]
    set_action("MixThrow")
    start, release, end = 36, 57, 78
    assert bpy.data.actions["MixThrow"].frame_range[1] >= end
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=2, radius=.048)
    grenade = bpy.context.object
    grenade.name = "Held grenade"
    grenade.parent = rig
    material = bpy.data.materials.new("Grenade olive")
    material.diffuse_color = (.13, .25, .055, 1)
    grenade.data.materials.append(material)
    prop = rig.pose.bones["RifleProp"]
    prop_rest = prop.bone.matrix_local.copy()

    def palm(side):
        wrist = rig.pose.bones[f"Wrist.{side}"].head
        fingers = [rig.pose.bones[f"{digit}1.{side}"].head for digit in ("Index", "Middle", "Ring")]
        return wrist.lerp(sum(fingers, Vector()) / 3, .69)

    scene.frame_set(release)
    tip = palm("R")
    info["grenade"] = dict(release_source_frame=release,
        release_local=[-tip.x * scale, tip.z * scale, tip.y * scale],
        comparison_blend_sha256=hashlib.sha256(comparison.read_bytes()).hexdigest())
    for name, frames, hold_until in [
        ("throw-windup", [*range(start, release - 1, 2), release - 1], release),
        ("throw-release", list(range(release, end + 1, 2)), end + 1),
    ]:
        durations = [after - before for before, after in zip(frames, frames[1:] + [hold_until])]
        info["clips"][name] = dict(action="MixThrow", frames=frames, durations=durations, loop=False)
        folder = output / "frames" / name
        folder.mkdir(parents=True, exist_ok=True)
        for direction in range(8):
            rig.rotation_euler.z = math.pi - direction * math.tau / 8
            for index, frame in enumerate(frames):
                scene.frame_set(frame)
                grenade.hide_render = frame >= release
                grenade.location = palm("R")
                x = Vector((.10, -.72, -.69)).normalized()
                y = Vector((0, 0, 1)).cross(x).normalized()
                z = x.cross(y)
                prop.matrix = Matrix.Translation(palm("L") - .23*x - .032*z) @ Matrix((x,y,z)).transposed().to_4x4() @ prop_rest
                prop.scale = (1, 1, 1)
                bpy.context.view_layer.update()
                scene.render.filepath = str(folder / f"{direction}-{index:02}.png")
                bpy.ops.render.render(write_still=True)
        print("Rendered", name, len(frames), flush=True)


if grenade_only:
    info = json.loads((output / "manifest.json").read_text())
    assert info["source_blend_sha256"] == hashlib.sha256(source.read_bytes()).hexdigest()
if not (sniper or rocketeer):
    render_grenade(info, Path(args[2]))
(output / "manifest.json").write_text(json.dumps(info, indent=2) + "\n")
