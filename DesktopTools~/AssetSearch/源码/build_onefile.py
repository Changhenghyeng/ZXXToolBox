"""Build a self-contained EXE, fixing runtime DLLs before sealing the archive."""
from pathlib import Path
import os
import PyInstaller.__main__
import PySide6

source = Path(__file__).resolve().parent
workspace = source.parent
output = Path(os.environ.get('ASSET_SEARCH_DIST', str(workspace/'outputs/成品工具/单文件版')))
build = Path(os.environ.get('ASSET_SEARCH_BUILD_WORK', str(workspace/'work/build-onefile')))
licenses = source.parent/'发布资料/第三方许可'
build.mkdir(parents=True, exist_ok=True)

# Analysis can collect an incompatible ICU from PATH. Fix its TOC before EXE
# embeds the files, because a onefile bundle cannot be repaired afterwards.
spec = f'''# -*- mode: python ; coding: utf-8 -*-
from pathlib import Path
from PyInstaller.utils.hooks import collect_data_files
source = Path({str(source)!r})
qt = Path({str(Path(PySide6.__file__).parent)!r})
licenses = Path({str(licenses)!r})
datas = collect_data_files('psd_tools')
datas += [(str(f), str(Path('第三方许可')/f.relative_to(licenses).parent))
          for f in licenses.rglob('*') if f.is_file()]
a = Analysis([str(source/'app.py')], pathex=[str(source)], binaries=[],
    datas=datas, hiddenimports=[], hookspath=[], hooksconfig={{}}, runtime_hooks=[],
    excludes=['PySide6.QtQml','PySide6.QtQuick','PySide6.QtOpenGL',
              'PySide6.QtNetwork','scipy','matplotlib'], noarchive=False, optimize=0)
a.binaries = [entry for entry in a.binaries
              if Path(entry[0]).name.lower() not in ('icuuc.dll','icudt78.dll')]
runtime_names = ['vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll',
    'msvcp140_1.dll','msvcp140_2.dll','msvcp140_codecvt_ids.dll','concrt140.dll']
for name in runtime_names:
    correct = qt/name
    if correct.exists():
        a.binaries = [(dest, str(correct), kind) if Path(dest).name.lower()==name
                      else (dest, path, kind) for dest,path,kind in a.binaries]
        a.binaries = [entry for entry in a.binaries if entry[0].lower()!=name]
        a.binaries.append((name,str(correct),'BINARY'))
pyz = PYZ(a.pure)
exe = EXE(pyz, a.scripts, a.binaries, a.datas, [], name='素材搜图',
    debug=False, bootloader_ignore_signals=False, strip=False, upx=False,
    console=False, disable_windowed_traceback=False, argv_emulation=False,
    target_arch=None, codesign_identity=None, entitlements_file=None)
'''
spec_path = build/'素材搜图_单文件.spec'
spec_path.write_text(spec, encoding='utf-8')
PyInstaller.__main__.run(['--noconfirm','--distpath',str(output),
    '--workpath',str(build),str(spec_path)])
print(output/'素材搜图.exe')
