"""Build the portable Windows app and avoid an incompatible ICU DLL from PATH."""
from pathlib import Path
import os
import shutil
import PyInstaller.__main__
import PySide6

source=Path(__file__).resolve().parent
output=Path(os.environ.get('ASSET_SEARCH_DIST',str(source.parent/'程序')))
build=Path(os.environ.get('ASSET_SEARCH_BUILD_WORK',str(source/'build')))
PyInstaller.__main__.run([
    '--noconfirm','--onedir','--windowed','--name','素材搜图',
    '--distpath',str(output),'--workpath',str(build),'--specpath',str(build),
    '--exclude-module','PySide6.QtQml','--exclude-module','PySide6.QtQuick',
    '--exclude-module','PySide6.QtOpenGL','--exclude-module','PySide6.QtNetwork',
    '--exclude-module','scipy','--exclude-module','matplotlib',
    '--collect-data','psd_tools',str(source/'app.py')])
internal=(output/'素材搜图'/'_internal').resolve()
for name in ['icuuc.dll','icudt78.dll']:
    target=internal/name
    assert target.resolve().is_relative_to(internal)
    if target.exists():
        target.unlink()
# Use the VC runtime supplied with Qt instead of an older runtime chosen from PATH.
qt=Path(PySide6.__file__).parent
for name in ['vcruntime140.dll','vcruntime140_1.dll','msvcp140.dll','msvcp140_1.dll',
             'msvcp140_2.dll','msvcp140_codecvt_ids.dll','concrt140.dll']:
    if (qt/name).exists():
        shutil.copyfile(qt/name,internal/name)
print(output/'素材搜图'/'素材搜图.exe')
