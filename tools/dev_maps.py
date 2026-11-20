"""Map authoring and pack checks shared by the Bash and PowerShell entry points."""

import argparse
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import tempfile


ROOT = Path(__file__).resolve().parent.parent
SANDBOX = ROOT / 'sandbox'
PROJECT = 'sandbox/OpenRTS.Sandbox.csproj'
AUTHORING_PLUGIN = '"res://addons/openrts_editor/plugin.cfg"'
ROCKY_PASS = 'res://examples/maps/RockyPass.tscn'
AUTHORING_MAP = 'res://examples/maps/AuthoringMap.tscn'


def build(target, root=ROOT):
    source = os.environ.get('OPENRTS_NUGET_SOURCE')
    options = ['--disable-build-servers', '-m:1']
    subprocess.run(['dotnet', 'restore', target, *options, *(['--source', source] if source else [])],
                   cwd=root, check=True)
    subprocess.run(['dotnet', 'build', target, '--configuration', 'Debug', '--no-restore', *options,
                    '-p:UseSharedCompilation=false'], cwd=root, check=True)


def import_assets(godot, project):
    subprocess.run([godot, '--headless', '--editor', '--path', project, '--import'], cwd=project.parent, check=True)


def authoring_enabled(project):
    return any(line.startswith('enabled=') and AUTHORING_PLUGIN in line
               for line in (project / 'project.godot').read_text(encoding='utf-8').splitlines())


def build_map(godot, project, scene, *args):
    result = subprocess.run([godot, '--headless', '--editor', '--path', project, '--max-fps', '60',
                             '--quit-after', '7200', scene, '--', f'--build-map={scene}', *args],
                            cwd=project.parent, stdout=subprocess.PIPE, encoding='utf-8', errors='replace')
    print(result.stdout, end='', file=sys.stderr if result.returncode else sys.stdout, flush=True)
    return result.returncode == 0 and any(line.startswith('OPENRTS_MAP_BUILD:') for line in result.stdout.splitlines())


def require_map_build(godot, project, scene, *args):
    if not build_map(godot, project, scene, *args):
        raise RuntimeError('Map authoring did not report a published build.')


def export_pack(godot, project, output):
    subprocess.run([godot, '--headless', '--path', project, '--', '--check-scene-maps'],
                   cwd=project.parent, check=True)
    result = subprocess.run([godot, '--headless', '--editor', '--path', project, '--export-pack',
                             'Scene Map Pack', output], cwd=project.parent, stdout=subprocess.PIPE,
                            stderr=subprocess.STDOUT, encoding='utf-8', errors='replace')
    if (result.returncode or re.search(r'^ERROR:', result.stdout, re.MULTILINE)
            or not output.is_file() or not output.stat().st_size):
        print(result.stdout, end='', file=sys.stderr)
        raise RuntimeError('Scene Map Pack export failed.')


def check_map_run(godot, cwd, marker, *args):
    result = subprocess.run([godot, '--headless', '--max-fps', '60', '--quit-after', '1800', *args],
                            cwd=cwd, stdout=subprocess.PIPE, encoding='utf-8', errors='replace')
    report = '\n'.join(line for line in result.stdout.splitlines() if line.startswith(marker))
    if result.returncode or not report:
        print(result.stdout, end='', file=sys.stderr)
        raise RuntimeError(f'Map verification did not report {marker}')
    print(report, flush=True)
    return report


def set_native_pack_preset(project):
    host = platform.system()
    target, rid_os = {'Darwin': ('macOS', 'osx'), 'Linux': ('Linux', 'linux'),
                      'Windows': ('Windows Desktop', 'win')}[host]
    machine = platform.machine().lower()
    architecture, rid_arch = {'arm64': ('arm64', 'arm64'), 'aarch64': ('arm64', 'arm64'),
                              'amd64': ('x86_64', 'x64'), 'x86_64': ('x86_64', 'x64')}[machine]
    preset = project / 'export_presets.cfg'
    text = preset.read_text(encoding='utf-8')
    text = text.replace('platform="macOS"', f'platform="{target}"')
    preset.write_text(text.replace('binary_format/architecture="arm64"',
                                   f'binary_format/architecture="{architecture}"'), encoding='utf-8')
    return f'{rid_os}-{rid_arch}'


def verify_map_export(godot):
    with tempfile.TemporaryDirectory(prefix='openrts-map-export-') as temp:
        staging = Path(temp)
        source = staging / 'source'
        source.mkdir()
        for name in ('global.json', 'Directory.Build.props', 'NuGet.Config'):
            shutil.copy2(ROOT / name, source)
        for name in ('src', 'libs', 'sandbox', 'docs/vendor'):
            shutil.copytree(ROOT / name, source / name, ignore=shutil.ignore_patterns('.godot', 'bin', 'obj', 'artifacts'))

        project = source / 'sandbox'
        generated = project / 'Controls/Shared'
        if generated.exists():
            shutil.rmtree(generated)
        build(PROJECT, source)
        import_assets(godot, project)
        require_map_build(godot, project, ROCKY_PASS, '--format=compiled', '--verify')
        (project / 'artifacts/maps/rocky-pass.ortmap.json').replace(project / 'content/scene-maps/rocky-pass.ortmap.json')
        settings = project / 'project.godot'
        settings.write_text(settings.read_text(encoding='utf-8').replace(AUTHORING_PLUGIN, ''), encoding='utf-8')
        (project / '.godot').rename(staging / 'authoring-cache')
        (project / '.godot/mono').mkdir(parents=True)
        (staging / 'authoring-cache/mono/temp').rename(project / '.godot/mono/temp')
        import_assets(godot, project)

        menu_marker = 'OPENRTS_SCENE_MAP_MENU:PASS'
        simulation_marker = 'OPENRTS_SIMULATION:'
        check_map_run(godot, source, menu_marker, '--path', project, '--', '--verify-scene-map-menu')
        compiled_args = ('--', '--map=res://content/scene-maps/rocky-pass.ortmap.json',
                         '--mod=res://rocky-pass-game.yaml', '--verify-simulation', '--ticks=8')
        loose_report = check_map_run(godot, source, simulation_marker, '--path', project, *compiled_args)

        dependency = project / 'examples/maps/SceneDecoration.gd'
        dependency.rename(staging / dependency.name)
        result = subprocess.run([godot, '--headless', '--path', project, '--', '--check-scene-maps'],
                                cwd=source, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                encoding='utf-8', errors='replace')
        missing = 'missing dependency res://examples/maps/SceneDecoration.gd'
        if not result.returncode or missing not in result.stdout:
            print(result.stdout, end='', file=sys.stderr)
            raise RuntimeError('Scene map validation did not reject the missing dependency.')
        print(next(line for line in result.stdout.splitlines() if missing in line), flush=True)
        (staging / dependency.name).rename(dependency)

        run = staging / 'run'
        run.mkdir()
        rid = set_native_pack_preset(project)
        export_pack(godot, project, run / 'maps.pck')
        # The editor's standalone runner looks for Debug even when loading a pack.
        shutil.copytree(project / '.godot/mono/temp/bin/ExportRelease' / rid, run / '.godot/mono/temp/bin/Debug')
        pack = ('--main-pack', 'maps.pck', '--')
        check_map_run(godot, run, 'OPENRTS_SCENEMAP_CHECK:', *pack, '--check-scene-maps')
        check_map_run(godot, run, menu_marker, *pack, '--verify-scene-map-menu')
        check_map_run(godot, run, 'OPENRTS_MAP_PROPERTIES:PASS', *pack, '--verify-initial-properties')
        pack_report = check_map_run(godot, run, simulation_marker, '--main-pack', 'maps.pck', *compiled_args)
        if pack_report != loose_report:
            raise RuntimeError(f'Compiled map differs after export.\nLoose {loose_report}\nPacked {pack_report}')
    print('OPENRTS_MAP_EXPORT:PASS addon disabled, 3D bundles and 2D compiled map')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('godot', type=Path)
    parser.add_argument('action', choices=('map-build', 'scene-map', 'scene-map-pack', 'verify-map-export', 'verify-authoring'))
    parser.add_argument('options', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    godot, action, options = args.godot, args.action, args.options
    if action == 'verify-map-export':
        verify_map_export(godot)
    elif action == 'scene-map-pack':
        output = Path(options[0]) if len(options) == 1 else Path()
        if not output.is_absolute() or output.suffix != '.pck':
            parser.error('Usage: scene-map-pack <absolute output.pck path>')
        build('OpenRTS.sln')
        import_assets(godot, SANDBOX)
        with tempfile.TemporaryDirectory(prefix='.scene-map-pack.', dir=output.parent) as temp:
            staged = Path(temp) / 'maps.pck'
            export_pack(godot, SANDBOX, staged)
            staged.replace(output)
        print(output)
    else:
        if action in ('map-build', 'scene-map'):
            if not options or not re.fullmatch(r'res://.*\.tscn', options[0]):
                parser.error(f'Usage: {action} res://path/to/map.tscn')
            if action == 'scene-map':
                if len(options) != 1:
                    parser.error('Usage: scene-map res://path/to/map.tscn')
                options.append('--format=bundle')
        if not authoring_enabled(SANDBOX):
            raise RuntimeError('Enable OpenRTS Map Authoring in Sandbox Project Settings > Plugins.')
        build(PROJECT)
        import_assets(godot, SANDBOX)
        if action != 'verify-authoring':
            require_map_build(godot, SANDBOX, *options)
            return
        subprocess.run([godot, '--headless', '--path', SANDBOX, 'res://Verification/MapAuthoringChecks.tscn'],
                       cwd=ROOT, check=True)
        subprocess.run([godot, '--headless', '--editor', '--path', SANDBOX, AUTHORING_MAP, '--', '--verify-map-editor'],
                       cwd=ROOT, check=True)
        require_map_build(godot, SANDBOX, ROCKY_PASS, '--format=compiled', '--verify')
        require_map_build(godot, SANDBOX, AUTHORING_MAP, '--format=bundle', '--verify', '--start=west',
                          '--start-set=content/start-sets/infantry.yaml')
        if build_map(godot, SANDBOX, ROCKY_PASS, '--format=bundle'):
            raise RuntimeError('2D authoring incorrectly published a scene-map bundle.')


if __name__ == '__main__':
    try:
        main()
    except subprocess.CalledProcessError as error:
        print(error, file=sys.stderr)
        sys.exit(error.returncode)
    except (OSError, RuntimeError) as error:
        print(error, file=sys.stderr)
        sys.exit(1)
