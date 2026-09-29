"""Build isolated FFmpeg 8.1.2 candidate. Run under Windows Python 3.13.

Locked source archives must be under downloads/. See BUILD.md for acquisition.
No writes to the ClypDat source tree or installed FFmpeg directory.
"""
import hashlib, json, os, pathlib, shutil, subprocess, sys

ROOT=pathlib.Path('D:/ClypDatFfmpeg812')
VS=pathlib.Path('C:/Program Files/Microsoft Visual Studio/18/Community')
CMAKE=VS/'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe'
BASH='C:/Program Files/Git/bin/bash.exe'
PREFIX=ROOT/'deps'
LOGS=ROOT/'logs'
LOGS.mkdir(exist_ok=True)
(ROOT/'build-temp').mkdir(exist_ok=True)

def source(name):
    p=ROOT/'sources'/name
    return p if name=='aom' else next(x for x in p.iterdir() if x.is_dir())

def vs_environment():
    batch=ROOT/'environment.cmd'
    batch.write_text(f'@echo off\ncall "{VS / "Common7/Tools/VsDevCmd.bat"}" -arch=x64 -host_arch=x64 >nul\nif errorlevel 1 exit /b 1\nset\n')
    result=subprocess.run(['cmd.exe','/d','/c',str(batch)],capture_output=True,text=True,check=True)
    env={k.upper():v for k,v in os.environ.items()}
    for line in result.stdout.splitlines():
        if '=' in line:
            k,v=line.split('=',1);env[k.upper()]=v
    env['PATH']=str(ROOT/'tools/usr/bin')+';'+str(ROOT/'tools/nasm-2.16.03')+';'+str(CMAKE.parent)+';'+str(VS/'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja')+';'+env['PATH']+';C:/Program Files/Git/usr/bin'
    env['TEMP']=env['TMP']=str(ROOT/'build-temp')
    env['MSYS2_PATH_TYPE']='inherit'
    env['SOURCE_DATE_EPOCH']='1781650800'
    for name in ('CL','_CL_','LINK','_LINK_','CFLAGS','CXXFLAGS','CPPFLAGS','LDFLAGS','LIBS','CC','CXX','AR','AS','CMAKE_PREFIX_PATH'):
        env.pop(name,None)
    return env

ENV=vs_environment()

def verify_toolchain():
    lock=json.loads((ROOT/'toolchain.lock.json').read_text())
    for key in ('VCToolsVersion','WindowsSDKVersion'):
        if ENV.get(key.upper())!=lock[key]:raise RuntimeError('Toolchain version mismatch: '+key)
    for item in lock['files']:
        name=item['name']
        path=sys.executable if name=='python.exe' else item['path'] if name in ('git.exe','bash.exe','7z.exe') else shutil.which(name,path=ENV['PATH'])
        with open(path,'rb') as f:actual=hashlib.file_digest(f,'sha256').hexdigest()
        if actual!=item['sha256']:raise RuntimeError('Toolchain checksum mismatch: '+name)

def run(name,args,cwd=ROOT,env=ENV):
    print('START',name,flush=True)
    previous=LOGS/(name+'.log')
    if previous.exists():
        number=1
        while (LOGS/f'{name}.previous-{number}.log').exists():number+=1
        shutil.copy2(previous,LOGS/f'{name}.previous-{number}.log')
    (LOGS/(name+'.args.json')).write_text(json.dumps([str(a) for a in args],indent=2))
    with open(LOGS/(name+'.log'),'w',encoding='utf-8') as log:
        r=subprocess.run([str(a) for a in args],cwd=cwd,env=env,stdout=log,stderr=subprocess.STDOUT)
    if r.returncode:
        print((LOGS/(name+'.log')).read_text(errors='replace')[-7000:],flush=True)
        raise RuntimeError(f'{name} failed: {r.returncode}')
    print('PASS',name,flush=True)

def cmake_dep(name,sub='',flags=()):
    if '--from=aom' in sys.argv and name in ('onevpl','zlib'): return
    if '--from=x264' in sys.argv: return
    src=source(name)/sub
    build=ROOT/'build'/('aom-nasm216' if name=='aom' else name)
    cmake=CMAKE
    generator=['-G','Visual Studio 18 2026','-A','x64']
    run(name+'-configure',[cmake,'-S',src,'-B',build,*generator,
        '-DCMAKE_POLICY_VERSION_MINIMUM=3.5',f'-DCMAKE_INSTALL_PREFIX={PREFIX.as_posix()}',
        '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL','-DCMAKE_ASM_NASM_COMPILER=D:/ClypDatFfmpeg812/tools/nasm-2.16.03/nasm.exe',*flags])
    run(name+'-build',[cmake,'--build',build,'--config','Release','--parallel','8'])
    run(name+'-install',[cmake,'--install',build,'--config','Release','--prefix',PREFIX])

def shell(name,script,cwd=ROOT):
    p=ROOT/(name+'.sh');p.write_text('#!/usr/bin/env bash\nset -eu\n'+script,encoding='utf-8',newline='\n')
    run(name,[BASH,p.as_posix()],cwd)

def dav1d():
    env=ENV.copy();env['PYTHONPATH']=str(ROOT/'tools/python-tools')
    meson=[sys.executable,'-m','mesonbuild.mesonmain']
    build=ROOT/'build/dav1d'
    args=[*meson,'setup',str(build),str(source('dav1d')),'--prefix='+PREFIX.as_posix(),'--libdir=lib','--buildtype=release',
          '-Ddefault_library=static','-Db_vscrt=md','-Denable_tools=false','-Denable_tests=false','-Denable_examples=false','-Denable_docs=false']
    if (build/'meson-private/coredata.dat').exists():args.append('--reconfigure')
    run('dav1d-configure',args,env=env)
    run('dav1d-build',[*meson,'compile','-C',build,'-j','8'],env=env)
    run('dav1d-install',[*meson,'install','-C',build],env=env)
    (PREFIX/'lib/pkgconfig/dav1d.pc').write_text(f'prefix={PREFIX.as_posix()}\nName: dav1d\nDescription: pinned dav1d\nVersion: 1.5.4\nLibs: ${{prefix}}/lib/libdav1d.a\nCflags: -I${{prefix}}/include\n')

def x265_multilib(install=True):
    cmake=ROOT/'tools/cmake-3.31.8-windows-x86_64/bin/cmake.exe'
    base=ROOT/'build/x265-multilib';base.mkdir(exist_ok=True)
    common=['-G','Ninja','-DCMAKE_BUILD_TYPE=Release','-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreadedDLL',
            '-DCMAKE_ASM_NASM_COMPILER=D:/ClypDatFfmpeg812/tools/nasm-2.16.03/nasm.exe',
            '-DENABLE_SHARED=OFF','-DENABLE_CLI=OFF','-DENABLE_ASSEMBLY=ON','-DENABLE_NUMA=OFF','-DENABLE_LIBNUMA=OFF',
            '-DCMAKE_INSTALL_PREFIX='+PREFIX.as_posix()]
    libraries={}
    for depth in (12,10,8):
        build=base/str(depth)
        flags=['-DHIGH_BIT_DEPTH=ON','-DEXPORT_C_API=OFF','-DMAIN12='+('ON' if depth==12 else 'OFF')] if depth!=8 else [
            '-DHIGH_BIT_DEPTH=OFF','-DEXPORT_C_API=ON','-DLINKED_10BIT=ON','-DLINKED_12BIT=ON',
            '-DEXTRA_LIB='+libraries[10].as_posix()+';'+libraries[12].as_posix()]
        run(f'x265-{depth}-configure',[cmake,'-S',source('x265')/'source','-B',build,*common,*flags])
        run(f'x265-{depth}-build',[cmake,'--build',build,'--parallel','6'])
        libraries[depth]=build/'x265-static.lib'
    combined=base/'x265-static.lib'
    run('x265-multilib-merge',[shutil.which('lib.exe',path=ENV['PATH']),'/nologo','/OUT:'+str(combined),*[libraries[d] for d in (8,10,12)]])
    if install:
        run('x265-multilib-install',[cmake,'--install',base/'8','--prefix',PREFIX])
        shutil.copy2(combined,PREFIX/'lib/x265-static.lib')

def deps():
    cmake_dep('onevpl',flags=['-DBUILD_SHARED_LIBS=ON','-DBUILD_EXPERIMENTAL=ON','-DBUILD_EXAMPLES=OFF','-DBUILD_TESTS=OFF','-DINSTALL_EXAMPLES=OFF'])
    # zlib's legacy #ifdef treats FFmpeg's HAVE_UNISTD_H=0 as true.
    # MSVC has no unistd.h; fix only the dependency's header template.
    template=source('zlib')/'zconf.h.cmakein'
    original=template.read_text()
    old='#ifdef HAVE_UNISTD_H    /* may be set to #if 1 by ./configure */'
    new='#if defined(HAVE_UNISTD_H) && !defined(_WIN32) /* ClypDat: MSVC has no unistd.h. */'
    if old not in original and new not in original:raise RuntimeError('Unexpected zlib header template')
    template.write_text(original.replace(old,new))
    cmake_dep('zlib',flags=['-DZLIB_BUILD_EXAMPLES=OFF'])
    if '--from=aom' not in sys.argv and '--from=x264' not in sys.argv:x265_multilib()
    cmake_dep('aom',flags=['-DBUILD_SHARED_LIBS=OFF','-DENABLE_TESTS=OFF','-DENABLE_DOCS=OFF','-DENABLE_EXAMPLES=OFF','-DENABLE_TOOLS=OFF'])
    dav1d()
    build=ROOT/'build/x264-md';build.mkdir(parents=True,exist_ok=True)
    shell('x264-build',f'export CC=cl\nexport CFLAGS=-MD\n"{source("x264").as_posix()}/configure" --prefix={PREFIX.as_posix()} --host=x86_64-w64-mingw32 --enable-static --disable-cli --enable-pic\nmake -j8\nmake install\n',build)
    shutil.copytree(source('nv-codec-headers')/'include/ffnvcodec',PREFIX/'include/ffnvcodec',dirs_exist_ok=True)
    amf_header=source('amf')/'amf/public/include/components/DisplayCapture.h'
    header=amf_header.read_text()
    declaration='AMF_RESULT AMF_CDECL_CALL AMFCreateComponentDisplayCapture(amf::AMFContext* pContext, void* reserved, amf::AMFComponent** ppComponent);'
    if '#ifdef __cplusplus\nextern "C"' not in header:
        if declaration not in header:raise RuntimeError('Unexpected AMF display header')
        header=header.replace('extern "C"\n{','#ifdef __cplusplus\nextern "C"\n{',1)
        header=header.replace(declaration+'\n}',declaration+'\n}\n#else\nAMF_RESULT AMF_CDECL_CALL AMFCreateComponentDisplayCapture(AMFContext* pContext, void* reserved, AMFComponent** ppComponent);\n#endif',1)
        amf_header.write_text(header)
    shutil.copytree(source('amf')/'amf/public/include',PREFIX/'include/AMF',dirs_exist_ok=True)
    pc=PREFIX/'lib/pkgconfig';pc.mkdir(parents=True,exist_ok=True)
    # FFmpeg's MSVC linker consumes these import/static libraries directly.
    # Explicit .lib paths prevent pkgconf from guessing MinGW archive names.
    for name,ver,lib in [('vpl','2.16.0','vpl.lib'),('x264','0.165.0','libx264.lib'),('x265','4.1','x265-static.lib'),('aom','3.13.1','aom.lib'),('zlib','1.3.1','zlibstatic.lib')]:
        (pc/(name+'.pc')).write_text(f'prefix={PREFIX.as_posix()}\nName: {name}\nDescription: pinned ClypDat dependency\nVersion: {ver}\nLibs: ${{prefix}}/lib/{lib}\nCflags: -I${{prefix}}/include'+(' -I${prefix}/include/vpl -DONEVPL_EXPERIMENTAL=1' if name=='vpl' else '')+'\n')
    (pc/'ffnvcodec.pc').write_text(f'prefix={PREFIX.as_posix()}\nName: ffnvcodec\nDescription: NV codec headers\nVersion: 13.0.19.0\nCflags: -I${{prefix}}/include\n')

def ffmpeg():
    build=ROOT/'build/ffmpeg';build.mkdir(parents=True,exist_ok=True)
    configure=[str(source('ffmpeg')/'configure').replace('\\','/'),
        '--prefix='+str(ROOT/'candidate').replace('\\','/'),'--toolchain=msvc','--arch=x86_64','--target-os=win64',
        '--enable-shared','--disable-static','--disable-autodetect','--disable-doc','--disable-debug',
        '--enable-gpl','--enable-version3','--enable-libvpl','--enable-d3d11va','--enable-dxva2',
        '--enable-ffnvcodec','--enable-nvenc','--enable-nvdec','--enable-cuvid','--enable-amf',
        '--enable-libx264','--enable-libx265','--enable-libaom','--enable-libdav1d','--enable-zlib','--enable-schannel',
        '--extra-cflags=-MD -ID:/ClypDatFfmpeg812/deps/include -DONEVPL_EXPERIMENTAL=1',
        '--extra-cxxflags=-MD -DONEVPL_EXPERIMENTAL=1',
        '--extra-ldflags=-LIBPATH:D:/ClypDatFfmpeg812/deps/lib','--pkg-config=pkgconf']
    import shlex
    script='export PKG_CONFIG_PATH=/d/ClypDatFfmpeg812/deps/lib/pkgconfig\n'
    if '--resume' not in sys.argv:
        script+=' '.join(shlex.quote(x) for x in configure)+'\n'
    script+='make -r -j8 REVISION=8.1.2\nmake -r REVISION=8.1.2 install\n'
    (ROOT/'configure-command.json').write_text(json.dumps(configure,indent=2)+'\n')
    shell('ffmpeg-build',script,build)
    shutil.copy2(PREFIX/'bin/libvpl.dll',ROOT/'candidate/bin/libvpl.dll')

if __name__=='__main__':
    import msvcrt
    with open(ROOT/'build.lock','a+b') as lock:
        lock.seek(0);lock.write(b'0');lock.flush();lock.seek(0)
        msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
        verify_toolchain()
        for item in json.loads((ROOT/'sources.lock.json').read_text()):
            with open(ROOT/'downloads'/item['file'],'rb') as f: actual=hashlib.file_digest(f,'sha256').hexdigest()
            if actual!=item['sha256']: raise RuntimeError('Source hash mismatch: '+item['name'])
        if 'deps' in sys.argv: deps()
        if 'ffmpeg' in sys.argv: ffmpeg()
