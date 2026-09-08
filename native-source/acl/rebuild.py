"""Rebuild using the separately obtained, pinned LLVM-MinGW compiler.

python rebuild.py --compiler /path/to/clang++.exe --output /path/to/Sora.Acl.dll
No toolchain download or game resources are involved.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

def digest(path): return hashlib.sha256(path.read_bytes()).hexdigest()

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--compiler',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    package=Path(__file__).resolve().parent
    build=json.loads((package/'BUILD.json').read_text())
    compiler=args.compiler.resolve()
    for name,expected in build['compilerBinaries'].items():
        if digest(compiler.parent/name)!=expected: raise ValueError('Compiler binary hash differs: '+name)
    expected={x['path']:x['sha256'] for x in json.loads((package/'source-hashes.json').read_text())['files']}
    expected.update({x['path']:x['after'] for x in json.loads((package/'source-modifications.json').read_text())})
    actual={p.relative_to(package/'source').as_posix():digest(p) for p in (package/'source').rglob('*') if p.is_file()}
    if actual!=expected: raise ValueError('Packaged source hash inventory differs')
    with tempfile.TemporaryDirectory(prefix='sora-acl-rebuild-') as temp:
        root=Path(temp)
        shutil.copytree(package/'source',root/'source')
        subprocess.run([str(compiler),*build['compilerArgs']],cwd=root/'source',check=True,timeout=180)
        output=root/'Sora.Acl.dll'
        if digest(output)!=build['sha256']: raise ValueError('Rebuilt native binary is not byte-identical')
        args.output.parent.mkdir(parents=True,exist_ok=True)
        shutil.copy2(output,args.output)
        print(json.dumps({'sha256':digest(args.output),'byteIdentical':True,'sourceFiles':len(actual)}))

if __name__=='__main__':
    try: main()
    except Exception as error:
        raise SystemExit(str(error))
