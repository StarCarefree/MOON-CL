"""Compile and run startup Agent regression fixtures; all generated data stays in scratch/temp."""
import argparse
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--java', default=shutil.which('java'))
parser.add_argument('--agent', type=Path, default=Path(__file__).resolve().parents[2] / 'Plain Craft Launcher 2/Resources/pcl-moon-startup-agent.jar')
a = parser.parse_args()
if not a.java:
    parser.error('JDK 17 or 21 is required')
java = Path(a.java)
javac = java.with_name('javac.exe' if os.name == 'nt' else 'javac')
sources = Path(__file__).parent / 'test'
with tempfile.TemporaryDirectory(prefix='pcl-moon-loader-tests-', dir=os.environ.get('PI_SCRATCH_DIR')) as temp:
    work = Path(temp)
    classes = work / 'classes'
    classes.mkdir()
    subprocess.run([str(javac), '-encoding', 'UTF-8', '-d', str(classes), *map(str, sorted(sources.rglob('*.java')))], check=True)
    fixture = work / 'boundary-agent.jar'
    with zipfile.ZipFile(fixture, 'w', zipfile.ZIP_DEFLATED) as jar:
        jar.writestr('META-INF/MANIFEST.MF', 'Manifest-Version: 1.0\nPremain-Class: AgentBoundaryTest\nCan-Retransform-Classes: true\n\n')
        for file in classes.glob('AgentBoundaryTest*.class'):
            jar.write(file, file.name)
    def run(test, mode, flags=(), pre_agent=False):
        directory = work / (test + '-' + mode)
        directory.mkdir()
        command = [str(java)]
        if pre_agent:
            command += ['-javaagent:' + str(fixture) + '=' + str(directory)]
        command += ['-javaagent:' + str(a.agent.resolve()), '-Djava.io.tmpdir=' + str(directory), *flags,
                    '-cp', str(classes), test, str(directory)]
        print('TEST', test, mode, flush=True)
        subprocess.run(command, check=True, timeout=35)
    for test in ('LookupTest', 'ClassPathCacheTest'):
        run(test, 'enabled', ['-Dpclmoon.startup.metrics=true'])
        run(test, 'classpath-off', ['-Dpclmoon.startup.classpathCache.disabled=true'])
        run(test, 'all-off', ['-Dpclmoon.startup.disabled=true'])
    run('AgentBoundaryTest', 'agent-order', ['-Dpclmoon.startup.metrics=true'], pre_agent=True)
    run('TimeoutTest', 'timer', ['-Dpclmoon.startup.durationMs=1000'])
    run('ForgeReadCacheTest', 'raw-read', ['-Dpclmoon.startup.metrics=true', '-Xverify:all'])
    run('ForgeFastReadTest', 'direct-read', ['-Dpclmoon.startup.metrics=true', '-Xverify:all'])
print('PASS: all 10 startup-agent regression modes')
