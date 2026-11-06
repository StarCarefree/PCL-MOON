"""Offline Windows-x64 startup milestones for self-contained modern Minecraft version JSONs.
Uses the version's JVM/game arguments (including Forge module options), never real login tokens
or quick-play/world flags. Existing libraries only; generated natives/logs go to a new output dir.
A first texture atlas is NOT proof that the main menu is interactive. Compare idle A/B/B/A runs.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import time
import zipfile


def allowed(item, features=None):
    rules = item.get('rules')
    if not rules:
        return True
    result = False
    for rule in rules:
        target = rule.get('os', {})
        if target.get('name', 'windows') != 'windows' or target.get('arch', 'amd64') not in ('amd64', 'x86_64'):
            continue
        if target.get('version') and not re.search(target['version'], '10.0'):
            continue
        if any((features or {}).get(key, False) != value for key, value in rule.get('features', {}).items()):
            continue
        result = rule['action'] == 'allow'
    return result


def expand_arguments(parts, replacements, features=None):
    result = []
    for part in parts:
        if isinstance(part, dict):
            if not allowed(part, features):
                continue
            values = part['value']
            values = values if isinstance(values, list) else [values]
        else:
            values = [part]
        for value in values:
            def replace(match):
                key = match.group(1)
                if key not in replacements:
                    raise ValueError('Unresolved version argument: ' + key)
                return replacements[key]
            result.append(re.sub(r'\$\{([^}]+)\}', replace, value))
    return result


def artifact_path(library):
    path = library.get('downloads', {}).get('artifact', {}).get('path')
    if path:
        return path
    coordinate = library['name'].split('@', 1)
    parts = coordinate[0].split(':')
    if len(parts) not in (3, 4):
        raise ValueError('Unsupported library coordinate')
    group, name, version = parts[:3]
    classifier = '-' + parts[3] if len(parts) == 4 else ''
    extension = coordinate[1] if len(coordinate) == 2 else 'jar'
    return '/'.join([group.replace('.', '/'), name, version, name + '-' + version + classifier + '.' + extension])


def build_command(instance, java, output, jvm_args=(), agent=None, jfr=False, heap='4G', minecraft_root=None):
    root = minecraft_root.resolve() if minecraft_root else instance.parents[1]
    doc = json.loads((instance / (instance.name + '.json')).read_text(encoding='utf-8-sig'))
    if doc.get('inheritsFrom') or 'arguments' not in doc:
        raise ValueError('Resolve inherited/legacy version JSON before benchmarking')
    libraries = []
    native_archives = []
    for lib in doc['libraries']:
        if not allowed(lib):
            continue
        file = root / 'libraries' / artifact_path(lib)
        if not file.is_file():
            raise FileNotFoundError('Required library missing: ' + str(file))
        libraries.append(file)
        native = lib.get('natives', {}).get('windows')
        if native:
            spec = lib['downloads']['classifiers'][native.replace('${arch}', '64')]
            native_archives.append(root / 'libraries' / spec['path'])
    client = instance / (instance.name + '.jar')
    if not client.is_file():
        raise FileNotFoundError('Client JAR missing')
    natives = output / 'natives'
    natives.mkdir()
    # Modern libraries carry their native classifiers as classpath artifacts. Legacy classifiers
    # are handled too; only DLL basenames are extracted, never arbitrary ZIP member paths.
    for file in dict.fromkeys([*libraries, *native_archives]):
        with zipfile.ZipFile(file) as archive:
            for entry in archive.infolist():
                if entry.filename.lower().endswith('.dll') and not entry.is_dir():
                    if entry.file_size > 128 * 1024 * 1024:
                        raise ValueError('Oversized native library')
                    destination = natives / PurePosixPath(entry.filename.replace('\\', '/')).name
                    destination.write_bytes(archive.read(entry))
    replacements = {
        'natives_directory': str(natives), 'launcher_name': 'PCL-MOON-benchmark', 'launcher_version': '1',
        'classpath': ';'.join(map(str, [*libraries, client])), 'classpath_separator': ';',
        'library_directory': (root / 'libraries').as_posix(), 'version_name': doc['id'],
        'auth_player_name': 'StartupBenchmark', 'game_directory': str(instance),
        'assets_root': str(root / 'assets'), 'assets_index_name': doc['assetIndex']['id'],
        'auth_uuid': '00000000000030008000000000000001', 'auth_access_token': '0',
        'clientid': '0', 'auth_xuid': '0', 'user_type': 'legacy', 'version_type': 'PCL-MOON benchmark',
        'resolution_width': '854', 'resolution_height': '480', 'user_properties': '{}',
    }
    jvm = expand_arguments(doc['arguments']['jvm'], replacements)
    game = expand_arguments(doc['arguments']['game'], replacements, {'has_custom_resolution': True})
    if any(flag.startswith('--quickPlay') or flag in ('--server', '--world') for flag in game):
        raise ValueError('Automatic world/server entry is not allowed in this benchmark')
    command = [str(java), '-Xms512M', '-Xmx' + heap, '-XX:+UseG1GC', '-Dfile.encoding=UTF-8',
               '-Djava.io.tmpdir=' + str(output), *jvm_args]
    if agent:
        command += ['-javaagent:' + str(agent.resolve())]
    if jfr:
        command += ['-XX:StartFlightRecording=filename="' + str(output / 'startup.jfr') + '",settings=profile,dumponexit=true']
    command += [*jvm, doc['mainClass'], *game]
    mods = sorted((instance / 'mods').glob('*.jar'))
    fingerprint = hashlib.sha256()
    for mod in mods:
        fingerprint.update(mod.name.encode('utf-8'))
        fingerprint.update(hashlib.sha256(mod.read_bytes()).digest())
    return command, {'main_class': doc['mainClass'], 'java_required': doc.get('javaVersion', {}).get('majorVersion'),
                     'top_level_mod_count': len(mods), 'mod_set_sha256': fingerprint.hexdigest()}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--instance', type=Path, required=True)
    parser.add_argument('--java', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--minecraft-root', type=Path, help='Read shared assets/libraries from this root for an isolated instance copy')
    parser.add_argument('--agent', type=Path)
    parser.add_argument('--jfr', action='store_true')
    parser.add_argument('--heap', default='4G')
    parser.add_argument('--timeout', type=int, default=90)
    parser.add_argument('--jvm-arg', action='append', default=[], help='Use --jvm-arg=-Dname=value')
    parser.add_argument('--settle-seconds', type=int, default=8)
    a = parser.parse_args()
    if os.name != 'nt':
        parser.error('Windows x64 only')
    if a.timeout < 10 or not re.fullmatch(r'[1-9][0-9]*[mMgG]', a.heap):
        parser.error('Timeout must be >=10 seconds and heap must be a positive M/G size')
    instance, java, output = a.instance.resolve(), a.java.resolve(), a.output.resolve()
    output.mkdir(parents=True, exist_ok=False)
    command, metadata = build_command(instance, java, output, a.jvm_arg, a.agent, a.jfr, a.heap, a.minecraft_root)
    java_version = subprocess.run([str(java), '-version'], capture_output=True, check=True)
    metadata['java_version'] = (java_version.stderr + java_version.stdout).decode('utf-8', errors='replace').splitlines()[0]
    marks = {}
    patterns = {'loader': r'Loading Minecraft|ModLauncher .* starting:|ModLauncher running:',
                'mods_resolved': r'Loading \d+ mods:|Found \d+ dependencies adding them to mods collection',
                'render': r'Backend library:|LWJGL version ', 'reload': r'Reloading ResourceManager',
                'sound': r'Sound engine started', 'first_atlas': r'Created:.*(?:atlas|textures)'}
    start = time.perf_counter()
    logfile = output / 'console.log'
    result = None
    with logfile.open('w', encoding='utf-8') as console, logfile.open('r', encoding='utf-8', errors='replace') as reader:
        process = subprocess.Popen(command, cwd=instance, stdout=console, stderr=subprocess.STDOUT,
                                   env={k: v for k, v in os.environ.items() if not k.startswith('PCL_MOON_')})
        try:
            pending = ''
            while time.perf_counter() - start < a.timeout:
                elapsed = time.perf_counter() - start
                # 只扫描新增完整行，保留跨轮写入的半行，避免重复读取增长中的日志。
                chunk = pending + reader.read()
                end = chunk.rfind('\n') + 1
                text, pending = chunk[:end], chunk[end:]
                for name, pattern in patterns.items():
                    if name not in marks and re.search(pattern, text):
                        marks[name] = round(elapsed, 3)
                        print(name, marks[name], flush=True)
                if 'first_atlas' in marks and elapsed - marks['first_atlas'] > max(0, a.settle_seconds):
                    break
                if process.poll() is not None:
                    break
                time.sleep(.05)
            if a.jfr and process.poll() is None:
                subprocess.run([str(java.with_name('jcmd.exe')), str(process.pid), 'JFR.dump',
                                'filename=' + str(output / 'startup.jfr')], check=True, capture_output=True, timeout=20)
            text = logfile.read_text(encoding='utf-8', errors='replace')
            count = re.search(r'Loading (\d+) mods:', text)
            errors = re.findall(r'\[[^\r\n\]]+/(?:ERROR|FATAL)\][^\r\n]*', text)
            result = {**metadata, 'milestones_seconds': marks, 'exit_code_before_stop': process.poll(),
                      'duration_seconds': round(time.perf_counter() - start, 3),
                      'mod_count': int(count.group(1)) if count else None, 'error_count': len(errors),
                      'loader_to_render_seconds': round(marks['render'] - marks['loader'], 3)
                          if 'render' in marks and 'loader' in marks else None,
                      'note': 'first_atlas is NOT an interactive main menu; filesystem cache is not flushed'}
            (output / 'result.json').write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding='utf-8')
            print(json.dumps(result, ensure_ascii=False), flush=True)
        finally:
            if process.poll() is None:
                process.terminate()
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
            for helper in output.glob('pcl-moon-startup-helper-*.jar'):
                helper.unlink()
    if 'first_atlas' not in marks:
        raise SystemExit('Resource milestone not reached; inspect the local console.log')
    if result['exit_code_before_stop'] is not None:
        raise SystemExit('Game process exited before the benchmark stopped it; inspect console.log')


if __name__ == '__main__':
    main()
