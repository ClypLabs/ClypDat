"""Assemble notices, runtime dependency closure and checksums. Never install."""
import hashlib, json, pathlib, re, shutil, subprocess, zipfile

R=pathlib.Path(__file__).resolve().parent
P=R/'candidate';BIN=P/'bin'
VS=pathlib.Path('C:/Program Files/Microsoft Visual Studio/18/Community')
DUMP=VS/'VC/Tools/MSVC/14.51.36231/bin/Hostx64/x64/dumpbin.exe'
REDIST=VS/'VC/Redist/MSVC/14.51.36231/x64/Microsoft.VC145.CRT'

def sha(path):
    with open(path,'rb') as f:return hashlib.file_digest(f,'sha256').hexdigest()

def source(name):
    p=R/'sources'/name
    return p if name=='aom' else next(x for x in p.iterdir() if x.is_dir())

def runtime():
    # FFmpeg's MSVC install puts import libraries beside DLLs. Keep runtime
    # and SDK files separate in the distributable, without changing binaries.
    sdk=P/'lib';sdk.mkdir(exist_ok=True)
    for path in BIN.glob('*.lib'):
        target=sdk/path.name
        shutil.copy2(path,target)
        assert sha(path)==sha(target)
        path.unlink()
    # This SDK contains shared import libraries, not static FFmpeg archives.
    for path in (sdk/'pkgconfig').glob('*.pc'):
        lines=[]
        for line in path.read_text().splitlines():
            key=line.split('=',1)[0]
            replacements={'prefix':'${pcfiledir}/../..','exec_prefix':'${prefix}',
                          'libdir':'${prefix}/lib','includedir':'${prefix}/include'}
            if key in replacements:line=key+'='+replacements[key]
            if line.startswith(('Libs.private:','Requires.private:')):continue
            lines.append(line)
        path.write_text('\n'.join(lines)+'\n')
    available={p.name.lower():p for p in REDIST.glob('*.dll')}
    while True:
        copied=False
        for p in list(BIN.iterdir()):
            if p.suffix.lower() not in ('.dll','.exe'):continue
            result=subprocess.check_output([str(DUMP),'/nologo','/dependents',str(p)],text=True)
            for name in re.findall(r'^\s+(\S+\.dll)\s*$',result,re.M|re.I):
                if name.lower() in available and not (BIN/name).exists():
                    shutil.copy2(available[name.lower()],BIN/name);copied=True
        if not copied:break
    data=[]
    for p in BIN.glob('*.dll'):
        if p.name.lower() in available:
            original=available[p.name.lower()]
            assert sha(p)==sha(original)
            data.append(dict(file=p.name,source=str(original),sha256=sha(p)))
    (P/'microsoft-runtime.json').write_text(json.dumps(data,indent=2)+'\n')

def licenses():
    dst=P/'licenses';dst.mkdir(exist_ok=True)
    shutil.copy2(R/'downloads/LLVM-LICENSE.TXT',dst/'LLVM-LICENSE.TXT')
    for name in ('ffmpeg','onevpl','x264','x265','aom','dav1d','zlib','ogg','opus','vorbis','amf','nv-codec-headers'):
        base=source(name)
        (dst/name).mkdir(exist_ok=True)
        for p in base.rglob('*'):
            if not p.is_file() or '.git' in p.parts:continue
            if name=='amf' and p.relative_to(base).parts[0]=='Thirdparty':continue
            if p.name.lower().startswith(('license','copying','patents','copyright','third_party_notices')):
                target=dst/name/p.relative_to(base);target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(p,target)
    shutil.copy2(source('zlib')/'zlib.h',dst/'zlib/zlib-header-license.txt')
    nv=source('nv-codec-headers')/'include/ffnvcodec/nvEncodeAPI.h'
    (dst/'nv-codec-headers').mkdir(exist_ok=True)
    (dst/'nv-codec-headers/NVIDIA-header-license.txt').write_text(nv.read_text().split('*/',1)[0]+'*/\n')
    (dst/'IJG-ACKNOWLEDGEMENT.txt').write_text('This software is based in part on the work of the Independent JPEG Group.\nFFmpeg jfdctfst.c, jfdctint_template.c and jrevdct.c are included unmodified.\nTheir original notices are retained in the corresponding source archive.\n')
    for name in ('jfdctfst.c','jfdctint_template.c','jrevdct.c'):
        shutil.copy2(source('ffmpeg')/'libavcodec'/name,dst/name)
    (dst/'MICROSOFT-RUNTIME.txt').write_text('Microsoft Visual C++ release runtime, x64, from VC/Redist/MSVC/14.51.36231.\nCopyright Microsoft Corporation. Distributed unmodified under Microsoft Visual Studio redistributable terms.\nhttps://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution\nhttps://visualstudio.microsoft.com/license-terms/\nThese files are not relicensed under the GPL. See microsoft-runtime.json for exact provenance and hashes.\n')

def write_manifest():
    files=[p for p in P.rglob('*') if p.is_file() and p.name not in ('SHA256SUMS','manifest.json')]
    data=[dict(path=p.relative_to(P).as_posix(),sha256=sha(p),bytes=p.stat().st_size) for p in sorted(files)]
    (P/'manifest.json').write_text(json.dumps(data,indent=2)+'\n')
    (P/'SHA256SUMS').write_text(''.join(f"{x['sha256']}  {x['path']}\n" for x in data))

def archive():
    name='clypdat-ffmpeg-8.1.2-win64-shared-r3'
    for suffix,files in [('',[(p,name+'/'+p.relative_to(P).as_posix()) for p in P.rglob('*') if p.is_file()]),
                         ('-sources',[(R/'downloads'/x['file'],'downloads/'+x['file']) for x in json.loads((R/'sources.lock.json').read_text()) if x['name'] not in ('make','pkgconf','cmake-compat','nasm-compat','nasm','meson','amf','llvm-aom')])]:
        if suffix:
            # AMF's complete upstream archive contains unrelated FFmpeg 7 DLLs.
            # Ship only the exact headers used here, plus the root AMD notice.
            amf=source('amf')
            files += [(p,'amf-headers/'+p.relative_to(amf).as_posix()) for p in (amf/'amf/public/include').rglob('*') if p.is_file()]
            files += [(amf/'LICENSE.txt','amf-headers/LICENSE.txt')]
            files += [(R/x,x) for x in ('sources.lock.json','toolchain.lock.json','build-candidate.py','bootstrap.py','package.py','BUILD.md','LICENSE-REVIEW.md')]
            files += [(p,'patches/'+p.name) for p in (R/'patches').glob('*')]
        path=R/(name+suffix+'.zip')
        with zipfile.ZipFile(path,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=6) as z:
            for p,arcname in sorted(files,key=lambda x:x[1]):
                info=zipfile.ZipInfo(arcname,(2026,9,29,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED;info.external_attr=0o644<<16
                z.writestr(info,p.read_bytes())
        (R/(path.name+'.sha256')).write_text(sha(path)+'  '+path.name+'\n')
        print(path.name,sha(path),flush=True)

if __name__=='__main__':
    runtime();licenses()
    for name in ('sources.lock.json','toolchain.lock.json','configure-command.json','BUILD.md','LICENSE-REVIEW.md','build-candidate.py','bootstrap.py','package.py'):
        shutil.copy2(R/name,P/name)
    shutil.copytree(R/'patches',P/'patches',dirs_exist_ok=True)
    write_manifest();archive()
