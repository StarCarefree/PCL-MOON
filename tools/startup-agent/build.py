"""Build Java-17-compatible Agent with JDK 17+; no network dependencies. Intermediates stay in scratch/temp."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, default=Path(__file__).resolve().parents[2] / 'Plain Craft Launcher 2/Resources/pcl-moon-startup-agent.jar')
parser.add_argument('--javac', default=shutil.which('javac'))
args = parser.parse_args()
if not args.javac:
    raise SystemExit('JDK 17+ javac is required')
sources = Path(__file__).parent / 'src/pclmoon/startup'
native = Path(__file__).parent / 'native'
notices = Path(__file__).parent / 'THIRD-PARTY-NOTICES.md'
def bootstrap(file):
    return file.name.startswith(('NegativeCache', 'ForgeDirectRead', 'Mmcss$')) or file.name == 'Mmcss.class'
for arch in ('windows-x64', 'windows-arm64'):
    if not (native / arch / 'mmcss.dll').is_file():
        raise SystemExit('Run tools/startup-agent/native/build.py before packaging: missing ' + arch)
with tempfile.TemporaryDirectory(prefix='pcl-moon-startup-build-', dir=os.environ.get('PI_SCRATCH_DIR')) as temp:
    work = Path(temp)
    classes = work / 'classes'
    classes.mkdir()
    subprocess.run([args.javac, '-encoding', 'UTF-8', '-source', '17', '-target', '17',
        '--add-exports', 'java.base/jdk.internal.org.objectweb.asm=ALL-UNNAMED',
        '--add-exports', 'java.base/jdk.internal.org.objectweb.asm.tree=ALL-UNNAMED', '-d', str(classes),
        *map(str, sorted(sources.glob('*.java')))], check=True)
    helper = work / 'bootstrap-helper.jar'
    with zipfile.ZipFile(helper, 'w', zipfile.ZIP_DEFLATED) as jar:
        for file in sorted((classes / 'pclmoon/startup').glob('*.class')):
            if bootstrap(file):
                jar.write(file, file.relative_to(classes).as_posix())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(args.output, 'w', zipfile.ZIP_DEFLATED) as jar:
        jar.writestr('META-INF/MANIFEST.MF', 'Manifest-Version: 1.0\nPremain-Class: pclmoon.startup.StartupAgent\nCan-Retransform-Classes: true\n\n')
        jar.write(helper, 'bootstrap-helper.jar')
        jar.write(notices, 'META-INF/MinecraftMMCSS-LICENSE.txt')
        for arch in ('windows-x64', 'windows-arm64'):
            jar.write(native / arch / 'mmcss.dll', 'native/' + arch + '/mmcss.dll')
        for file in sorted(classes.rglob('*.class')):
            if not bootstrap(file):
                jar.write(file, file.relative_to(classes).as_posix())
print('Built', args.output)
