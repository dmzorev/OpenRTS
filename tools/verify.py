"""Content parity, simulation replay and static/animated rendering smoke checks."""
import hashlib
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parent.parent


def run(command, expected=0):
    result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True, timeout=60)
    if result.returncode != expected:
        raise RuntimeError(f"Command returned {result.returncode}, expected {expected}: {command}\n{result.stdout}\n{result.stderr}")
    return result


def report(output, prefix="OPENRTS_CONTENT:"):
    lines = [line.removeprefix(prefix) for line in output.splitlines() if line.startswith(prefix)]
    if len(lines) != 1:
        raise RuntimeError("Expected exactly one content report:\n" + output)
    return json.loads(lines[0])


def main():
    godot = sys.argv[1]
    provenance = json.loads((ROOT / "docs/vendor/declarative-content.json").read_text())
    for source in provenance["files"]:
        actual = hashlib.sha256((ROOT / source["path"]).read_bytes()).hexdigest()
        if "local_sha256" in source and not source.get("local_change"):
            raise RuntimeError("Local import change lacks a reason: " + source["path"])
        if actual != source.get("local_sha256", source["sha256"]):
            raise RuntimeError("Vendored source differs from recorded import: " + source["path"])
    print("PASS DeclarativeContent import and documented local changes match SHA-256", flush=True)

    art = json.loads((ROOT / "docs/assets/weapon-stage-art.json").read_text())
    for item in art["assets"]:
        for path_key, hash_key in (("source", "source_sha256"), ("asset", "sha256")):
            actual = hashlib.sha256((ROOT / item[path_key]).read_bytes()).hexdigest()
            if actual != item[hash_key]:
                raise RuntimeError("Weapon-stage art provenance differs: " + item[path_key])
    print("PASS weapon-stage sprite sources and normalized assets match SHA-256", flush=True)

    art = json.loads((ROOT / "docs/assets/buildings-art.json").read_text())
    for item in art["assets"]:
        for path_key, hash_key in (("source", "source_sha256"), ("asset", "sha256"), ("original_source", "original_source_sha256")):
            if path_key in item and hashlib.sha256((ROOT / item[path_key]).read_bytes()).hexdigest() != item[hash_key]:
                raise RuntimeError("Building art provenance differs: " + item[path_key])
    print("PASS building sources, open-door edits and normalized sprites match SHA-256", flush=True)

    headless = ["dotnet", "run", "--project", "tools/OpenRTS.Headless", "--no-build", "--no-launch-profile", "--"]
    expected = report(run(headless).stdout)
    if expected["DefinitionCount"] != 10:
        raise RuntimeError("Expected ten concrete unit definitions")
    for count in (1, 10000, 10001):
        result = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify", f"--count={count}"])
        if "ERROR:" in result.stderr or "SCRIPT ERROR:" in result.stderr:
            raise RuntimeError(result.stderr)
        if report(result.stdout) != expected or f"OPENRTS_PREVIEW:{count}" not in result.stdout:
            raise RuntimeError("Godot/headless content mismatch or preview failed:\n" + result.stdout)
        print(f"PASS Godot/headless content parity; {count} static MultiMesh instances", flush=True)

    for count in (1, 10000, 10001):
        arguments = [f"--count={count}", "--ticks=64"]
        direct = report(run(headless + ["--simulate"] + arguments).stdout, "OPENRTS_SIMULATION:")
        for fps in (15, 120):
            result = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify-simulation", f"--fps={fps}"] + arguments)
            if "ERROR:" in result.stderr or report(result.stdout, "OPENRTS_SIMULATION:") != direct:
                raise RuntimeError("Simulation parity failed:\n" + result.stdout + result.stderr)
        print(f"PASS {count} moving entities: direct/worker/Godot parity at 15 and 120 FPS", flush=True)

    paced = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify-simulation", "--realtime", "--count=10000", "--ticks=8", "--fps=15"])
    direct = report(run(headless + ["--simulate", "--count=10000", "--ticks=8"]).stdout, "OPENRTS_SIMULATION:")
    if "ERROR:" in paced.stderr or report(paced.stdout, "OPENRTS_SIMULATION:") != direct:
        raise RuntimeError("Real-time session parity failed:\n" + paced.stdout + paced.stderr)
    print("PASS real-time worker pacing at 25 Hz with a 15 FPS presentation", flush=True)

    for count in (1, 10000):
        # Any-angle motion reaches the wall earlier; 256 ticks exercise routes and local contacts.
        # Complete passage is checked separately by the 1000-member Core regression.
        arguments = ["--navigation", f"--count={count}", f"--ticks={256 if count == 10000 else 128}"]
        expected_navigation = report(run(headless + arguments + ["--workers=1"]).stdout, "OPENRTS_SIMULATION:")
        reverse = report(run(headless + arguments + ["--workers=4", "--reverse-jobs"]).stdout, "OPENRTS_SIMULATION:")
        if reverse != expected_navigation:
            raise RuntimeError("Reversed parallel navigation changed headless state")
        for fps, workers in ((15, 1), (120, 4)):
            result = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify-simulation",
                f"--fps={fps}", f"--workers={workers}"] + arguments)
            if "ERROR:" in result.stderr or report(result.stdout, "OPENRTS_SIMULATION:") != expected_navigation:
                raise RuntimeError("Navigation host parity failed:\n" + result.stdout + result.stderr)
        print(f"PASS {count} navigation entities: sequential/parallel/reverse/Godot parity", flush=True)

    controls = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify-controls", "--navigation", "--count=100"])
    if "ERROR:" in controls.stderr or "OPENRTS_CONTROLS:PASS" not in controls.stdout:
        raise RuntimeError("Sandbox controls failed:\n" + controls.stdout + controls.stderr)
    print("PASS Godot wheel focus, zoom, pan and playback input handlers", flush=True)

    presentation = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify-presentation"])
    showcase = report(run(headless + ["--showcase", "--ticks=12"]).stdout, "OPENRTS_SIMULATION:")
    if "ERROR:" in presentation.stderr or "OPENRTS_PRESENTATION:PASS" not in presentation.stdout or "OPENRTS_COMBAT_PRESENTATION:PASS" not in presentation.stdout or report(presentation.stdout, "OPENRTS_SIMULATION:") != showcase:
        raise RuntimeError("Presentation verification failed:\n" + presentation.stdout + presentation.stderr)
    controls = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--showcase", "--verify-controls"])
    if "ERROR:" in controls.stderr or "OPENRTS_CONTROLS:PASS" not in controls.stdout:
        raise RuntimeError("Showcase controls failed:\n" + controls.stdout + controls.stderr)
    print("PASS SpriteFrames, layered presentation, combat effects, repair cursor, alpha, states and showcase input", flush=True)
    rts = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify-rts-controls"])
    if "ERROR:" in rts.stderr or "OPENRTS_RTS_CONTROLS:PASS" not in rts.stdout:
        raise AssertionError("RTS selection/action/cursor verification failed: " + rts.stdout + rts.stderr)
    print("PASS RTS selection, queue, target modes, cursor feedback and UI/programmatic actions", flush=True)

    buildings = report(run(headless + ["--buildings", "--ticks=24", "--workers=1"]).stdout, "OPENRTS_SIMULATION:")
    parallel = report(run(headless + ["--buildings", "--ticks=24", "--workers=4"]).stdout, "OPENRTS_SIMULATION:")
    native = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify-buildings", "--workers=4"])
    if buildings != parallel or "ERROR:" in native.stderr or "OPENRTS_BUILDINGS:PASS" not in native.stdout or report(native.stdout, "OPENRTS_SIMULATION:") != buildings:
        raise RuntimeError("Building host/worker/presentation parity failed:\n" + native.stdout + native.stderr)
    print("PASS buildings, footprint masks, sizes, lifecycle states, doors, radar and 10000-building batches; host/worker parity", flush=True)

    invalid = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify", "--count=0"], expected=1)
    if "лимит диагностического стенда" not in invalid.stderr:
        raise RuntimeError("Missing invalid-count diagnostic")
    with tempfile.TemporaryDirectory(prefix="openrts-verify-") as directory:
        path = Path(directory) / "invalid.yaml"
        path.write_text("unit: {Health: {HP: 0}}\n")
        invalid = run(headless + [str(path)], expected=1)
        if "HP" not in invalid.stderr:
            raise RuntimeError("Missing invalid-content diagnostic")
    print("PASS invalid content and preview arguments fail explicitly", flush=True)


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, OSError, subprocess.TimeoutExpired) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
