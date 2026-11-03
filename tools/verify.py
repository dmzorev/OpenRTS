"""End-to-end content parity and static GPU-instancing smoke checks (no simulation benchmark)."""
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


def report(output):
    lines = [line.removeprefix("OPENRTS_CONTENT:") for line in output.splitlines() if line.startswith("OPENRTS_CONTENT:")]
    if len(lines) != 1:
        raise RuntimeError("Expected exactly one content report:\n" + output)
    return json.loads(lines[0])


def main():
    godot = sys.argv[1]
    provenance = json.loads((ROOT / "docs/vendor/declarative-content.json").read_text())
    for source in provenance["files"]:
        actual = hashlib.sha256((ROOT / source["path"]).read_bytes()).hexdigest()
        if actual != source["sha256"]:
            raise RuntimeError("Vendored source differs from recorded import: " + source["path"])
    print("PASS DeclarativeContent import matches recorded SHA-256", flush=True)

    headless = ["dotnet", "run", "--project", "tools/OpenRTS.Headless", "--no-build", "--no-launch-profile", "--"]
    expected = report(run(headless).stdout)
    if expected["DefinitionCount"] != 2:
        raise RuntimeError("Expected two concrete unit definitions")
    for count in (1, 10000, 10001):
        result = run([godot, "--headless", "--path", str(ROOT / "sandbox"), "--", "--verify", f"--count={count}"])
        if "ERROR:" in result.stderr or "SCRIPT ERROR:" in result.stderr:
            raise RuntimeError(result.stderr)
        if report(result.stdout) != expected or f"OPENRTS_PREVIEW:{count}" not in result.stdout:
            raise RuntimeError("Godot/headless content mismatch or preview failed:\n" + result.stdout)
        print(f"PASS Godot/headless content parity; {count} static MultiMesh instances", flush=True)

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
