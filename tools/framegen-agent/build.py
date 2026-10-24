"""Build the optional Java-8-compatible agent. Runtime uses Minecraft's LWJGL, not bundled LWJGL."""
import argparse
import hashlib
import io
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent
DEPS = {
    'asm-9.9.jar': ('org/ow2/asm/asm/9.9/asm-9.9.jar', '03d99a74ad1ee5c71334ef67437f4ef4fe3488caa7c96d8645abc73c8e2017d4'),
    'lwjgl-3.3.3.jar': ('org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3.jar', 'dc9c7b2d48e8396d68895f8902ffa01e46253de44dfe927533ff09457ebfeec4'),
    'lwjgl-opengl-3.3.3.jar': ('org/lwjgl/lwjgl-opengl/3.3.3/lwjgl-opengl-3.3.3.jar', '5062da750e5f7ec89e27e8b67b7d40fa8c4b16890159e35f828116ce6c8eca1e'),
    'lwjgl-glfw-3.3.3.jar': ('org/lwjgl/lwjgl-glfw/3.3.3/lwjgl-glfw-3.3.3.jar', 'bedc757fd2b1b255146db26b74b9cbd1fcf5644b89b47b104fa721c741604259'),
}
OLD, NEW = b'org/objectweb/asm', b'pclmoon/framegen/internal/asm'


def relocate(data):
    """Relocate ASM class constants, without touching bytecode offsets/frames or other agents' ASM."""
    if data[:4] != b'\xca\xfe\xba\xbe':
        raise ValueError('not a class file')
    count = struct.unpack_from('>H', data, 8)[0]
    out, pos, index = bytearray(data[:10]), 10, 1
    sizes = {3: 4, 4: 4, 5: 8, 6: 8, 7: 2, 8: 2, 9: 4, 10: 4, 11: 4, 12: 4, 15: 3, 16: 2, 17: 4, 18: 4, 19: 2, 20: 2}
    while index < count:
        tag = data[pos]
        out.append(tag)
        pos += 1
        if tag == 1:
            length = struct.unpack_from('>H', data, pos)[0]
            text = data[pos + 2:pos + 2 + length].replace(OLD, NEW).replace(b'org.objectweb.asm', b'pclmoon.framegen.internal.asm')
            out.extend(struct.pack('>H', len(text)))
            out.extend(text)
            pos += 2 + length
        else:
            length = sizes[tag]
            out.extend(data[pos:pos + length])
            pos += length
            if tag in (5, 6):
                index += 1
        index += 1
    out.extend(data[pos:])
    return bytes(out)


def dependencies(directory):
    directory.mkdir(parents=True, exist_ok=True)
    result = []
    for name, (remote, expected) in DEPS.items():
        target = directory / name
        if not target.exists():
            with urllib.request.urlopen('https://repo.maven.apache.org/maven2/' + remote, timeout=60) as response:
                data = response.read()
            if hashlib.sha256(data).hexdigest() != expected:
                raise ValueError('Dependency checksum mismatch: ' + name)
            pending = target.with_suffix('.pending')
            pending.write_bytes(data)
            pending.replace(target)
        if hashlib.sha256(target.read_bytes()).hexdigest() != expected:
            raise ValueError('Cached dependency checksum mismatch: ' + str(target))
        result.append(target)
    return result


def put(jar, name, data):
    info = zipfile.ZipInfo(name, (1980, 1, 1, 0, 0, 0))
    info.compress_type = zipfile.ZIP_DEFLATED
    jar.writestr(info, data)


def build(output, javac, depdir):
    deps = dependencies(depdir)
    with tempfile.TemporaryDirectory(prefix='framegen-build-', dir=os.environ.get('PI_SCRATCH_DIR')) as temp:
        work = Path(temp)
        classes = work / 'classes'
        classes.mkdir()
        subprocess.run([javac, '-J-Xms16m', '-J-Xmx192m', '--release', '8', '-encoding', 'UTF-8', '-cp', os.pathsep.join(map(str, deps)),
                        '-d', str(classes), *map(str, sorted((ROOT / 'src').rglob('*.java')))], check=True)
        helper = io.BytesIO()
        with zipfile.ZipFile(helper, 'w', zipfile.ZIP_DEFLATED) as jar:
            for source in sorted((classes / 'pclmoon/framegen/bridge').glob('*.class')):
                put(jar, source.relative_to(classes).as_posix(), source.read_bytes())
        output.parent.mkdir(parents=True, exist_ok=True)
        pending = work / 'agent.jar'
        with zipfile.ZipFile(pending, 'w', zipfile.ZIP_DEFLATED) as jar:
            put(jar, 'META-INF/MANIFEST.MF', 'Manifest-Version: 1.0\nPremain-Class: pclmoon.framegen.FrameGenAgent\n\n')
            put(jar, 'bootstrap-helper.jar', helper.getvalue())
            put(jar, 'THIRD-PARTY-NOTICES.md', (ROOT / 'THIRD-PARTY-NOTICES.md').read_bytes())
            for source in sorted(classes.rglob('*.class')):
                if '/bridge/' not in source.as_posix():
                    put(jar, source.relative_to(classes).as_posix(), relocate(source.read_bytes()))
            for source in sorted((ROOT / 'resources').rglob('*')):
                if source.is_file():
                    put(jar, source.relative_to(ROOT / 'resources').as_posix(), source.read_bytes())
            with zipfile.ZipFile(deps[0]) as asm:
                for name in sorted(asm.namelist()):
                    if name.startswith('org/objectweb/asm/') and name.endswith('.class'):
                        put(jar, name.replace(OLD.decode(), NEW.decode()), relocate(asm.read(name)))
        # Do not touch the output timestamp on a no-op build.
        if not output.exists() or output.read_bytes() != pending.read_bytes():
            shutil.copyfile(pending, output)
    print('Built', output, 'SHA256', hashlib.sha256(output.read_bytes()).hexdigest())


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--output', type=Path, default=ROOT / 'build/pcl-moon-framegen-agent.jar')
    parser.add_argument('--javac', default=shutil.which('javac'))
    parser.add_argument('--deps', type=Path, default=Path(os.environ.get('PI_SCRATCH_DIR', ROOT / 'build')) / 'framegen-deps')
    args = parser.parse_args()
    if not args.javac:
        raise SystemExit('JDK 9+ javac required (--release 8); set JAVA_HOME/PATH or --javac')
    build(args.output.resolve(), args.javac, args.deps)


if __name__ == '__main__':
    main()
