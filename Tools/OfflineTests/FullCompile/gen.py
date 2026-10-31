import re, subprocess, sys, json, os, glob
SRC=os.environ.get('SCRIPTS_DIR') or os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)),'..','..','..','Assets','Scripts'))
STRUCTS=set('''Vector2Int Vector3Int Vector4 Color32 RectInt Bounds Matrix4x4 RaycastHit RaycastResult Keyframe Plane PointerEventData Vector3Data
GraphicsFormat AsyncGPUReadbackRequest Hash128 Resolution NativeArray JobHandle InputAction Pose'''.split())
STRUCTS-= {'Vector3Data','PointerEventData','InputAction'}   # classes
ENUMS=set()
def fixed_in_seed():
    s=open('Seed.cs').read()
    return set(re.findall(r'(?:class|struct|enum|delegate \w+|interface)\s+(\w+)',s))
seed=fixed_in_seed()
# generic arity from sources
arity={}
for f in glob.glob(SRC+'/**/*.cs',recursive=True):
    t=open(f,encoding='utf-8').read()
    for m in re.finditer(r'\b([A-Z]\w*)<([^<>;(){}]*(?:<[^<>]*>[^<>;(){}]*)*)>',t):
        n=m.group(1); inner=m.group(2)
        depth=0;c=1
        for ch in inner:
            if ch=='<':depth+=1
            elif ch=='>':depth-=1
            elif ch==',' and depth==0:c+=1
        if re.fullmatch(r'[\w\s,.<>\[\]?()]+',inner): arity[n]=max(arity.get(n,0),c)
types={}   # (ns,name)->arity
nss=set()
for f in glob.glob(SRC+'/**/*.cs',recursive=True):
    for m in re.finditer(r'^\s*using\s+([A-Za-z_][\w.]*)\s*;',open(f,encoding='utf-8').read(),re.M):
        n=m.group(1)
        if not n.startswith('System'): nss.add(n)
        elif n.startswith('System.') and n not in ('System.Collections','System.Collections.Generic','System.Linq','System.IO','System.Text','System.Threading','System.Threading.Tasks','System.Diagnostics','System.Globalization','System.Runtime.InteropServices','System.Reflection','System.Runtime.CompilerServices','System.Security.Cryptography','System.Net','System.Net.Sockets','System.IO.Compression','System.Text.RegularExpressions','System.Collections.Concurrent','System.Xml','System.ComponentModel'): nss.add(n)
if os.path.exists('types.json') and not os.environ.get('FRESH'):
    d=json.load(open('types.json')); types={tuple(k.split('|',1)):v for k,v in d.items()}
def emit():
    out=['using System;','// generated']
    byns={}
    for (ns,name),a in types.items(): byns.setdefault(ns,[]).append((name,a))
    for n in sorted(nss):
        if n.startswith('UnityEngine.Events') : continue
        byns.setdefault(n,[])
    for ns,lst in sorted(byns.items()):
        if ns: out.append('namespace %s {'%ns)
        for name,a in sorted(lst):
            if name in seed: continue
            tp=('<'+','.join('T%d'%i for i in range(a))+'>') if a else ''
            if re.fullmatch(r'I[A-Z]\w*',name): out.append('public interface %s%s {}'%(name,tp))
            elif name.endswith('Attribute'): out.append('public class %s : System.Attribute { public %s(params object[] a) {} }'%(name,name))
            elif name in STRUCTS: out.append('public struct %s%s {}'%(name,tp))
            else: out.append('public class %s%s {}'%(name,tp))
        if ns: out.append('}')
    open('Auto.cs','w').write('\n'.join(out)+'\n')
def build():
    r=subprocess.run(['dotnet','build','FullCompile.csproj','-c','Release','--no-restore','-nologo','-v','q','-p:ScriptsDir='+SRC],capture_output=True,text=True)
    return r.stdout+r.stderr
for it in range(40):
    emit()
    o=build()
    new=0
    for m in re.finditer(r"error CS0246: The type or namespace name '([^']+)' could not be found",o):
        raw=m.group(1); name=re.sub(r'<.*>','',raw)
        a=raw.count(',')+1 if '<' in raw else arity.get(name,0)
        if name.endswith('Attribute'): pass
        key=('',name)
        if key not in types and name not in seed: types[key]=a; new+=1
    for m in re.finditer(r"error CS0234: The type or namespace name '([^']+)' does not exist in the namespace '([^']+)'",o):
        name=re.sub(r'<.*>','',m.group(1)); ns=m.group(2)
        key=(ns,name)
        if key not in types: types[key]=arity.get(name,0); new+=1
    json.dump({'|'.join(k):v for k,v in types.items()},open('types.json','w'))
    errs=[l for l in o.split('\n') if ' error ' in l]
    print('iter',it,'new',new,'errors',len(set(errs)))
    if new==0: break
open('last.txt','w').write(o)
