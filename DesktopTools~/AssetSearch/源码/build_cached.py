"""Build a single shareable EXE with a persistent, versioned runtime cache."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import sys
import zipfile

source = Path(__file__).resolve().parent
workspace = source.parent
build = workspace/'work/build-cached-launcher-v013'
payload = workspace/'work/cached-payload-v013/素材搜图'
output = workspace/'outputs/成品工具/单文件加速版'
build.mkdir(parents=True, exist_ok=True)
output.mkdir(parents=True, exist_ok=True)

if '--skip-payload-build' not in sys.argv:
    env = dict(os.environ, ASSET_SEARCH_DIST=str(payload.parent),
               ASSET_SEARCH_BUILD_WORK=str(workspace/'work/build-cached-v013'))
    subprocess.run([sys.executable,str(source/'build_portable.py')],env=env,check=True)
assert (payload/'素材搜图.exe').is_file()
assert not (payload/'素材搜图数据').exists()
license_target = payload/'_internal/第三方许可'
if not license_target.exists():
    shutil.copytree(source.parent/'发布资料/第三方许可',license_target)
archive = build/'AssetsPayload.zip'
files = []
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as bundle:
    for file in sorted(payload.rglob('*')):
        if file.is_file():
            relative = file.relative_to(payload).as_posix()
            assert file.name not in ('settings.json','index.sqlite','数据目录.json')
            bundle.write(file,relative)
            files.append({'path':relative,'size':file.stat().st_size})
with open(archive,'rb') as stream:
    payload_hash = hashlib.file_digest(stream,'sha256').hexdigest()
manifest = build/'AssetsManifest.json'
manifest.write_text(json.dumps({'payload_hash':payload_hash,'files':files},
    ensure_ascii=False,separators=(',',':')),encoding='utf-8')
framework = Path(os.environ['SYSTEMROOT'])/'Microsoft.NET/Framework64/v4.0.30319'
exe = output/'素材搜图.exe'
subprocess.run([str(framework/'csc.exe'),'/nologo','/target:winexe','/platform:x64',
    '/optimize+','/reference:'+str(framework/'System.IO.Compression.dll'),
    '/reference:'+str(framework/'System.Web.Extensions.dll'),
    '/reference:'+str(framework/'System.Windows.Forms.dll'),
    '/reference:'+str(framework/'System.Drawing.dll'),
    '/resource:'+str(archive)+',AssetsPayload.zip',
    '/resource:'+str(manifest)+',AssetsManifest.json',
    '/out:'+str(exe),str(source/'CachedLauncher.cs')],check=True)
print(json.dumps({'exe':str(exe),'bytes':exe.stat().st_size,
    'payload_hash':payload_hash,'files':len(files)},ensure_ascii=False))
