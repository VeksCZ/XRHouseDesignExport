#!/usr/bin/env python3
"""Offline semantic check: compiles Assets/Scripts against generated stand-ins and prints only the errors that are new
compared with a known-good revision (default HEAD). See FullCompile.csproj."""
import os, re, subprocess, sys, tempfile, shutil, collections
HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..', '..'))
base = 'HEAD'
if '--base' in sys.argv: base = sys.argv[sys.argv.index('--base') + 1]

def signatures(scripts_dir):
    env = dict(os.environ, SCRIPTS_DIR=scripts_dir, FRESH='1')
    for f in ('types.json', 'Auto.cs'):
        p = os.path.join(HERE, f)
        if os.path.exists(p): os.remove(p)
    open(os.path.join(HERE, 'Auto.cs'), 'w').write('// generated\n')
    subprocess.run(['dotnet', 'restore', 'FullCompile.csproj', '-v', 'q'], cwd=HERE, capture_output=True)
    subprocess.run([sys.executable, 'gen.py'], cwd=HERE, env=env, capture_output=True)
    out = open(os.path.join(HERE, 'last.txt')).read()
    sig = collections.Counter()
    for l in set(out.split('\n')):
        m = re.search(r'([^/\\]+\.cs)\((\d+),(\d+)\): error (CS\d+): (.*?)(?: \[/|$)', l)
        if m: sig[(m.group(1), m.group(4), m.group(5).strip())] += 1
    return sig, out

tmp = tempfile.mkdtemp()
try:
    subprocess.run('git archive %s Assets/Scripts | tar -x -C %s' % (base, tmp), shell=True, cwd=REPO, check=True)
    old, _ = signatures(os.path.join(tmp, 'Assets', 'Scripts'))
    new, out = signatures(os.path.join(REPO, 'Assets', 'Scripts'))
finally:
    shutil.rmtree(tmp, ignore_errors=True)
fresh = [(k, n - old.get(k, 0)) for k, n in new.items() if n > old.get(k, 0)]
print('errors: base %d signature(s), working tree %d; new: %d' % (len(old), len(new), len(fresh)))
for (f, code, msg), n in sorted(fresh):
    line = next((l for l in out.split('\n') if f in l and code in l and msg[:40] in l), '')
    m = re.search(r'%s\((\d+),(\d+)\)' % re.escape(f), line)
    print('NEW %s%s %s: %s' % (f, '(' + m.group(1) + ')' if m else '', code, msg[:200]))
sys.exit(1 if fresh else 0)
