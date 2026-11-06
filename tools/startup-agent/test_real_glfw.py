"""Windows-x64 smoke using existing LWJGL 3.3.3 JARs; no game launch or download."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

p = argparse.ArgumentParser()
p.add_argument('--java', default=shutil.which('java'))
p.add_argument('--libraries', type=Path, required=True)
p.add_argument('--agent', type=Path, default=Path(__file__).resolve().parents[2] / 'Plain Craft Launcher 2/Resources/pcl-moon-startup-agent.jar')
a = p.parse_args()
if os.name != 'nt' or not a.java:
    p.error('Windows x64 and JDK 17/21 are required')
java = Path(a.java)
javac = java.with_name('javac.exe')
jars = [a.libraries / 'org/lwjgl' / name / '3.3.3' / (name + '-3.3.3' + suffix + '.jar')
        for name in ('lwjgl', 'lwjgl-glfw') for suffix in ('', '-natives-windows')]
for jar in jars:
    if not jar.is_file(): p.error('Missing local LWJGL dependency: ' + str(jar))
with tempfile.TemporaryDirectory(prefix='pcl-mmcss-real-glfw-', dir=os.environ.get('PI_SCRATCH_DIR')) as temp:
    work = Path(temp)
    agent = work / a.agent.name
    shutil.copyfile(a.agent, agent)
    subprocess.run([str(javac), '-encoding', 'UTF-8', '-d', str(work),
                    str(Path(__file__).parent / 'test/MmcssGlfwSmoke.java')], check=True)
    subprocess.run([str(java), '-Xverify:all', '-javaagent:' + str(agent), '-Djava.io.tmpdir=' + str(work),
                    '-cp', os.pathsep.join(map(str, [work, *jars])), 'MmcssGlfwSmoke'], check=True, timeout=45)
