"""Compare startup-agent resource lookups in independent Java 21 processes."""
import argparse
import hashlib
import json
import math
import os
from pathlib import Path
import re
import shutil
import statistics
import subprocess
import time
import traceback
import zipfile


MODES = ('none', 'baseline', 'candidate')
SCENARIOS = ('positive', 'misses', 'mixed')
QUERIES = 4000


def sha256(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def save_result(output, result):
    (output / 'results.json').write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def run_process(command, directory, environment):
    directory.mkdir(parents=True, exist_ok=True)
    temporary = directory / 'tmp'
    temporary.mkdir(exist_ok=True)
    env = dict(environment, TEMP=str(temporary), TMP=str(temporary), TMPDIR=str(temporary))
    (directory / 'command.json').write_text(
        json.dumps(command, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    with (directory / 'stdout.log').open('wb') as stdout, (directory / 'stderr.log').open('wb') as stderr:
        start = time.perf_counter_ns()
        subprocess.run(command, cwd=directory, env=env, stdout=stdout, stderr=stderr,
                       check=True, timeout=180)
        elapsed = (time.perf_counter_ns() - start) / 1_000_000
    return elapsed


def benchmark(args, output, result):
    java_name = shutil.which(args.java)
    java = Path(java_name or args.java).resolve(strict=True)
    javac = java.with_name('javac.exe' if os.name == 'nt' else 'javac')
    if not javac.is_file():
        raise FileNotFoundError(f'javac not found beside java: {javac}')
    environment = os.environ.copy()
    # 避免外部 JVM 参数注入其他 agent 或改变本次对照条件。
    removed = [name for name in ('JAVA_TOOL_OPTIONS', 'JDK_JAVA_OPTIONS', '_JAVA_OPTIONS', 'CLASSPATH')
               if environment.pop(name, None) is not None]
    result['removed_environment_variables'] = removed
    version_dir = output / 'jdk'
    run_process([str(java), '-XX:-UsePerfData', '-Djava.io.tmpdir=' + str(version_dir / 'tmp'),
                 '-XshowSettings:properties', '-version'], version_dir, environment)
    version = (version_dir / 'stderr.log').read_text(encoding='utf-8', errors='replace')
    result['jdk'] = {'java': str(java), 'version_output': version}
    if not re.search(r'^\s*java\.specification\.version\s*=\s*21\s*$', version, re.MULTILINE):
        raise RuntimeError('Java 21 is required; see jdk/stderr.log')

    agents = {}
    result['agents'] = {}
    for mode, source in (('baseline', args.baseline_agent), ('candidate', args.candidate_agent)):
        source = source.resolve(strict=True)
        directory = output / 'agents' / mode
        directory.mkdir(parents=True)
        target = directory / 'agent.jar'
        shutil.copyfile(source, target)
        agents[mode] = target
        result['agents'][mode] = {'source': str(source), 'copy': str(target), 'sha256': sha256(target)}

    fixtures = output / 'fixtures'
    fixtures.mkdir()
    result['fixtures'] = []
    # 固定条目名、顺序、时间和内容，所有进程共享同一组输入 JAR。
    for jar_index in range(32):
        path = fixtures / f'fixture-{jar_index:02d}.jar'
        with zipfile.ZipFile(path, 'w', compression=zipfile.ZIP_STORED) as jar:
            for resource_index in range(1000):
                entry = zipfile.ZipInfo(f'fixture/j{jar_index}/C{resource_index}.class',
                                        date_time=(2020, 1, 1, 0, 0, 0))
                jar.writestr(entry, b'lookup-only fixture\n')
        result['fixtures'].append({'file': path.name, 'sha256': sha256(path)})

    source = Path(__file__).resolve().parent / 'benchmark' / 'LookupBenchmark.java'
    classes = output / 'classes'
    classes.mkdir()
    compile_dir = output / 'compile'
    run_process([str(javac), '-J-XX:-UsePerfData', '-J-Djava.io.tmpdir=' + str(compile_dir / 'tmp'),
                 '-encoding', 'UTF-8', '--release', '17', '-d', str(classes), str(source)],
                compile_dir, environment)
    result['fixture_source_sha256'] = sha256(source)
    save_result(output, result)

    for round_index in range(args.rounds):
        # 每个场景内轮换先后顺序；每次查询场景均由全新 JVM 执行。
        order = MODES[round_index % 3:] + MODES[:round_index % 3]
        for scenario in SCENARIOS:
            for mode in order:
                directory = output / 'runs' / f'{round_index + 1:02d}-{scenario}-{mode}'
                command = [str(java), '-XX:-UsePerfData',
                           '-Djava.io.tmpdir=' + str(directory / 'tmp'),
                           '-Dpclmoon.startup.metrics=false', '-Dpclmoon.startup.durationMs=120000']
                if mode != 'none':
                    command.append('-javaagent:' + str(agents[mode]))
                command += ['-cp', str(classes), 'LookupBenchmark', str(fixtures), scenario]
                elapsed = run_process(command, directory, environment)
                stdout = (directory / 'stdout.log').read_text(encoding='utf-8')
                lines = [line[len('BENCHMARK '):] for line in stdout.splitlines()
                         if line.startswith('BENCHMARK ')]
                if len(lines) != 1:
                    raise RuntimeError(f'Expected one measurement in {directory / "stdout.log"}')
                measurement = json.loads(lines[0])
                expected_hits = {'positive': QUERIES, 'misses': 0, 'mixed': QUERIES // 2}[scenario]
                if (measurement['scenario'] != scenario or measurement['queries'] != QUERIES
                        or measurement['hits'] != expected_hits
                        or measurement['misses'] != QUERIES - expected_hits):
                    raise RuntimeError(f'Incorrect lookup counts: {directory}: {measurement}')
                first, total = measurement['first_query_ms'], measurement['query_loop_ms']
                if not (math.isfinite(first) and math.isfinite(total) and 0 <= first <= total):
                    raise RuntimeError(f'Invalid timings: {directory}: {measurement}')
                measurement.update(round=round_index + 1, mode=mode, spawn_to_exit_ms=elapsed,
                                   logs=str(directory.relative_to(output)))
                result['measurements'].append(measurement)
                save_result(output, result)
                print(f'{round_index + 1}/{args.rounds} {scenario} {mode}: '
                      f'first={first:.3f} ms loop={total:.3f} ms process={elapsed:.3f} ms', flush=True)

    result['medians'] = {}
    for scenario in SCENARIOS:
        result['medians'][scenario] = {}
        for mode in MODES:
            samples = [item for item in result['measurements']
                       if item['scenario'] == scenario and item['mode'] == mode]
            result['medians'][scenario][mode] = {
                key: statistics.median(item[key] for item in samples)
                for key in ('first_query_ms', 'query_loop_ms', 'spawn_to_exit_ms')}
    result['status'] = 'complete'
    save_result(output, result)


def main():
    parser = argparse.ArgumentParser(description='Java 21 startup-agent lookup comparison.')
    parser.add_argument('--java', required=True, help='Java 21 executable')
    parser.add_argument('--baseline-agent', type=Path, required=True, help='Baseline agent JAR')
    parser.add_argument('--candidate-agent', type=Path, required=True, help='Candidate agent JAR')
    parser.add_argument('--output', type=Path, required=True, help='New directory; prefer artifacts/test/')
    parser.add_argument('--rounds', type=int, default=6, help='Rounds per scenario (default: 6)')
    args = parser.parse_args()
    if args.rounds < 1:
        parser.error('--rounds must be positive')
    output = args.output.resolve()
    # 不复用旧目录，避免覆盖既有数据或混入旧的 helper 和日志。
    output.mkdir(parents=True, exist_ok=False)
    result = {
        'status': 'running', 'rounds': args.rounds, 'queries_per_process': QUERIES,
        'metrics_enabled': False, 'measurements': [],
        'note': 'Synthetic URLClassLoader resource lookup benchmark; not a Minecraft end-to-end measurement. '
                'No class loading, no warmup, no OS filesystem cache flush. '
                'Query loop includes the first lookup; process timing includes JVM and agent startup and exit. '
                'Each agent copy reuses its own extracted helper after its first process.',
    }
    try:
        benchmark(args, output, result)
    except BaseException:
        # 原始子进程输出与完整异常均保留；失败以非零退出码传播。
        failure = traceback.format_exc()
        (output / 'error.log').write_text(failure, encoding='utf-8')
        result['status'] = 'failed'
        result['error'] = failure
        save_result(output, result)
        raise
    print(f'Results: {output / "results.json"}')


if __name__ == '__main__':
    main()
