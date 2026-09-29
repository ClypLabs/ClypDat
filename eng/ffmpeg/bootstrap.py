"""Acquire locked sources/tools into a new isolated Windows build directory.

Usage: python bootstrap.py D:/path/without-production-files
Requires Git for Windows, Python 3.13, 7-Zip and the documented MSVC toolchain.
Existing directories are refused. The fixed build junction must not exist.
"""
import concurrent.futures, hashlib, io, json, os, pathlib, shutil, subprocess, sys, tarfile, urllib.request, zipfile

HERE=pathlib.Path(__file__).resolve().parent
GIT='C:/Program Files/Git/cmd/git.exe'

def sha(path):
    with open(path,'rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()

def acquire(root,item):
    dest=root/'downloads'/item['file']
    if item.get('git'):
        src=root/'sources/aom'
        subprocess.run([GIT,'clone','--depth','1','--branch','v3.13.1',item['git'],str(src)],check=True)
        actual=subprocess.check_output([GIT,'-C',str(src),'rev-parse','HEAD'],text=True).strip()
        if actual!=item['revision']:raise RuntimeError('AOM tag no longer matches locked commit')
        subprocess.run([GIT,'-C',str(src),'archive','--format=tar.gz','--output='+str(dest),actual],check=True)
    else:
        with urllib.request.urlopen(item['url'],timeout=180) as response,open(dest,'wb') as output:shutil.copyfileobj(response,output)
    if sha(dest)!=item['sha256']:raise RuntimeError('Source checksum mismatch: '+item['name'])
    print('VERIFIED',item['name'],flush=True)
    if item.get('git'):return
    if item['name'] in ('make','pkgconf'):
        data=subprocess.check_output(['C:/Program Files/7-Zip/7z.exe','x','-so',str(dest)])
        with tarfile.open(fileobj=io.BytesIO(data)) as archive:archive.extractall(root/'tools',filter='data')
    elif item['name'] in ('cmake-compat','nasm-compat'):
        with zipfile.ZipFile(dest) as archive:archive.extractall(root/'tools')
    elif item['name']=='meson':
        with zipfile.ZipFile(dest) as archive:archive.extractall(root/'tools/python-tools')
    else:
        target=root/'sources'/item['name'];target.mkdir(parents=True)
        if dest.suffix=='.zip':
            with zipfile.ZipFile(dest) as archive:archive.extractall(target)
        else:
            with tarfile.open(dest) as archive:archive.extractall(target,filter='data')

def main():
    root=pathlib.Path(sys.argv[1]).resolve()
    junction=pathlib.Path('D:/ClypDatFfmpeg812')
    if root.exists() or junction.exists():raise RuntimeError('Fresh staging and unoccupied D:/ClypDatFfmpeg812 required')
    root.mkdir(parents=True)
    for directory in ('downloads','sources','tools','build-temp'):(root/directory).mkdir()
    for file in ('sources.lock.json','toolchain.lock.json','build-candidate.py','bootstrap.py','BUILD.md','LICENSE-REVIEW.md','package.py'):
        shutil.copy2(HERE/file,root/file)
    shutil.copytree(HERE/'patches',root/'patches')
    items=json.loads((root/'sources.lock.json').read_text())
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:list(pool.map(lambda item:acquire(root,item),items))
    env=os.environ.copy();env['CLYPDAT_BUILD_TARGET']=str(root)
    subprocess.run(['powershell.exe','-NoProfile','-Command',
        "New-Item -ItemType Junction -Path 'D:/ClypDatFfmpeg812' -Target $env:CLYPDAT_BUILD_TARGET | Out-Null"],env=env,check=True)
    # Retained until explicitly removed after inspection; do not delete evidence.
    subprocess.run([sys.executable,str(root/'build-candidate.py'),'deps','ffmpeg'],check=True,cwd=root)
    subprocess.run([sys.executable,str(root/'package.py')],check=True,cwd=root)
    print('Candidate built; not installed into ClypDat:',root/'candidate')

if __name__=='__main__':main()
