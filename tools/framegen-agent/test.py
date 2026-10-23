"""Agent integration tests; --gpu additionally runs the real hidden-window OpenGL image test."""
import argparse
import io
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
import zipfile
import build as agent_build

ROOT = Path(__file__).resolve().parent
NATIVES = {
    'lwjgl-3.3.3-natives-windows.jar': ('org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar', '5ee63a19187efe5bb8747fd24f7b13257db348df5ad641113a3695e43f213959'),
    'lwjgl-opengl-3.3.3-natives-windows.jar': ('org/lwjgl/lwjgl-opengl/3.3.3/lwjgl-opengl-3.3.3-natives-windows.jar', 'f45e5f60596b03b2e3d8b98a123c855ebd0b5154c8155dc2a56b890ccc907477'),
    'lwjgl-glfw-3.3.3-natives-windows.jar': ('org/lwjgl/lwjgl-glfw/3.3.3/lwjgl-glfw-3.3.3-natives-windows.jar', '98155b451dfc6f585089dec77a12f9c05789c4dceb8da472f9d308160101a70d'),
}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--java', default=shutil.which('java'))
    parser.add_argument('--javac', default=shutil.which('javac'))
    parser.add_argument('--jar', type=Path, default=ROOT / 'build/pcl-moon-framegen-agent.jar')
    parser.add_argument('--deps', type=Path, default=Path(os.environ.get('PI_SCRATCH_DIR', ROOT / 'build')) / 'framegen-deps')
    parser.add_argument('--skip-build', action='store_true')
    parser.add_argument('--gpu', action='store_true')
    args = parser.parse_args()
    if not args.java or not args.javac:
        parser.error('java and javac are required')
    jar = args.jar.resolve()
    if not args.skip_build:
        agent_build.build(jar, args.javac, args.deps)
    deps = agent_build.dependencies(args.deps)
    with tempfile.TemporaryDirectory(prefix='framegen-test-', dir=os.environ.get('PI_SCRATCH_DIR')) as temp:
        work = Path(temp)
        fixture, renderer, helper = work / 'fixture', work / 'renderer', work / 'helper.jar'
        fixture.mkdir()
        renderer.mkdir()
        with zipfile.ZipFile(jar) as archive:
            assert not any(n.startswith(('org/lwjgl/', 'org/objectweb/asm/')) for n in archive.namelist()), 'Unrelocated/bundled runtime dependency'
            helper.write_bytes(archive.read('bootstrap-helper.jar'))
            for item in (archive, zipfile.ZipFile(io.BytesIO(helper.read_bytes()))):
                for name in item.namelist():
                    if name.endswith('.class') and '/internal/' not in name:
                        assert struct.unpack_from('>H', item.read(name), 6)[0] == 52, name + ': not Java 8 bytecode'
        subprocess.run([args.javac, '-J-Xms16m', '-J-Xmx192m', '--release', '8', '-encoding', 'UTF-8', '-cp', str(helper), '-d', str(fixture),
                        *map(str, sorted((ROOT / 'test/fixture').rglob('*.java')))], check=True)
        subprocess.run([args.javac, '-J-Xms16m', '-J-Xmx192m', '--release', '8', '-encoding', 'UTF-8', '-cp', str(fixture), '-d', str(renderer),
                        *map(str, sorted((ROOT / 'test/fake-renderer').rglob('*.java')))], check=True)
        fake_jar = work / 'renderer.jar'
        with zipfile.ZipFile(fake_jar, 'w') as archive:
            for source in renderer.rglob('*.class'):
                agent_build.put(archive, source.relative_to(renderer).as_posix(), source.read_bytes())
        modes = ('disabled', 'diagnostics-only', 'no-context', 'unfocused', 'missing-renderer', 'generate', 'render-failure', 'native-failure', 'budget', 'long-gap', 'stall-diagnostics')
        for mode in modes:
            flags = ['-Xms16m', '-Xmx128m', '-XX:ReservedCodeCacheSize=64m', '-Dpclmoon.framegen.outputFps=30']
            if mode == 'disabled': flags += ['-Dpclmoon.framegen.disabled=true']
            if mode in ('diagnostics-only', 'stall-diagnostics'): flags += ['-Dpclmoon.framegen.enabled=false', '-Dpclmoon.framegen.diagnostics=true']
            target = work / 'missing.jar' if mode == 'missing-renderer' else fake_jar
            result = subprocess.run([args.java, *flags, '-javaagent:' + str(jar), '-cp', os.pathsep.join(map(str, (fixture, helper))),
                            'pclmoon.framegen.test.AgentHarness', mode, str(target)], check=True, timeout=20, cwd=work,
                            capture_output=mode == 'stall-diagnostics')
            if mode == 'stall-diagnostics':
                assert b'[PCL FrameTime] presentation gap=' in result.stderr, 'watchdog did not report the controlled render-thread stall'
                print(result.stdout.decode('utf-8').strip())
        print('PASS: 11 agent integration scenarios + deterministic pacing/stall boundary assertions')
        if args.gpu:
            if os.name != 'nt': parser.error('--gpu native fixture currently targets Windows x64')
            original = agent_build.DEPS
            try:
                agent_build.DEPS = NATIVES
                natives = agent_build.dependencies(args.deps)
            finally:
                agent_build.DEPS = original
            gpu = work / 'gpu'
            gpu.mkdir()
            cp = os.pathsep.join(map(str, [jar, *deps[1:], *natives]))
            subprocess.run([args.javac, '-J-Xms16m', '-J-Xmx192m', '--release', '8', '-encoding', 'UTF-8', '-cp', cp, '-d', str(gpu),
                            *map(str, sorted((ROOT / 'test/pclmoon/framegen/render').rglob('*.java')))], check=True)
            for minor in (2, 3):
                subprocess.run([args.java, '-Xms16m', '-Xmx128m', '-XX:ReservedCodeCacheSize=64m',
                                '-Dpcl.framegen.test.glMinor=' + str(minor), '-cp', str(gpu) + os.pathsep + cp,
                                'pclmoon.framegen.render.GlInterpolatorSmoke'], check=True, timeout=60, cwd=work)


if __name__ == '__main__':
    main()
