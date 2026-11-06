"""Rebuild the tiny Windows JNI bridges with Zig 0.14.1+ (no Java/Maven dependencies).
Zig is a build tool only; neither Zig nor a compiler is needed at runtime.
"""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

parser = argparse.ArgumentParser()
parser.add_argument('--zig', default=shutil.which('zig'))
parser.add_argument('--javac', default=shutil.which('javac'))
a = parser.parse_args()
if not a.zig or not a.javac:
    parser.error('Zig and a Windows JDK javac are required (or pass --zig / --javac)')
root = Path(__file__).resolve().parent
include = Path(a.javac).resolve().parents[1] / 'include'
with tempfile.TemporaryDirectory(prefix='pcl-mmcss-native-', dir=os.environ.get('PI_SCRATCH_DIR')) as temp:
    work = Path(temp)
    cache = Path(os.environ.get('PI_SCRATCH_DIR') or tempfile.gettempdir()) / 'pcl-mmcss-zig-cache'
    env = dict(os.environ, ZIG_LOCAL_CACHE_DIR=str(cache / 'local'), ZIG_GLOBAL_CACHE_DIR=str(cache / 'global'))
    for arch, target in [('x64', 'x86_64-windows-gnu'), ('arm64', 'aarch64-windows-gnu')]:
        output = root / ('windows-' + arch) / 'mmcss.dll'
        output.parent.mkdir(parents=True, exist_ok=True)
        pending = work / ('mmcss-' + arch + '.dll')
        subprocess.run([a.zig, 'cc', '-target', target, '-shared', '-O2', '-s', '-Wall', '-Wextra', '-Werror',
                        '-Wno-cast-function-type-mismatch',
                        '-I' + str(include), '-I' + str(include / 'win32'), str(root / 'mmcss.c'),
                        '-o', str(pending)], check=True, env=env, cwd=work)
        shutil.copyfile(pending, output)
        print('Built', output, flush=True)
