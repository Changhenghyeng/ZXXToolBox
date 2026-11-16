"""Optional isolated executable smoke test; never runs during normal use."""
import json
import time
from pathlib import Path
from PIL import Image,ImageDraw
from psd_tools import PSDImage
from PySide6.QtCore import QTimer,Qt
from PySide6.QtGui import QFontDatabase,QFont
from PySide6.QtWidgets import QApplication


def attach(application,window,data):
    data=Path(data)
    target=data/'self-test-assets'
    target.mkdir(exist_ok=True)
    query=Image.new('RGBA',(128,128),(0,0,0,0))
    draw=ImageDraw.Draw(query)
    draw.ellipse((20,20,105,105),fill='#138be8',outline='white',width=4)
    draw.polygon([(64,28),(35,90),(94,90)],fill='#ffd700')
    path=target/'query.png'
    query.save(path)
    PSDImage.frompil(Image.new('RGB',(96,96),'#dd5566')).save(target/'merged.psd')
    window.roots.clear()
    window.roots.addItem(str(target))
    for name,check in window.formats.items():
        check.setChecked(name in ('PNG','PSD / PSB'))
    window.min_size.setValue(0)
    QFontDatabase.addApplicationFont('C:/Windows/Fonts/msyh.ttc')
    application.setFont(QFont('Microsoft YaHei UI',9))
    state={'stage':0,'started':time.time()}
    timer=QTimer(window)
    def finish(ok,detail):
        timer.stop()
        (data/'selftest-result.json').write_text(json.dumps({'ok':ok,'detail':detail},ensure_ascii=False,indent=2),encoding='utf-8')
        window.close()
        application.exit(0 if ok else 1)
    def tick():
        try:
            if time.time()-state['started']>60:
                finish(False,'timeout: '+window.status.text())
                return
            if state['stage']==0:
                window.start_job('index')
                state['stage']=1
            elif state['stage']==1 and window.process is None:
                import sqlite3
                db=sqlite3.connect(str(data/'index.sqlite'))
                assert db.execute('SELECT COUNT(*) FROM images').fetchone()[0]==2,window.status.text()
                db.close()
                from app import pil_pixmap
                QApplication.clipboard().setImage(pil_pixmap(query).toImage())
                window.paste()
                assert window.query.size==query.size
                window.start_job('search')
                state['stage']=2
            elif state['stage']==2 and window.process is None:
                assert window.gallery.count()==2,window.status.text()
                window.gallery.setCurrentRow(0)
                assert Path(window.selected_path())==path
                window.copy_path()
                assert QApplication.clipboard().text()==str(path)
                window.grab().save(str(data/'packaged-ui.png'))
                window.start_relocation(str(data.parent/'relocated-v012'))
                state['stage']=3
            elif state['stage']==3 and window.process is None:
                import sqlite3,sys
                actual=sys.modules[window.__class__.__module__]
                destination=data.parent/'relocated-v012'
                assert actual.DATA.resolve()==destination.resolve(),window.status.text()
                for folder in [data,destination]:
                    with sqlite3.connect(str(folder/'index.sqlite')) as db:
                        assert db.execute('SELECT COUNT(*) FROM images').fetchone()[0]==2
                    assert (folder/'settings.json').exists()
                assert actual.LOCATION_FILE.exists()
                window.start_job('search')
                state['stage']=4
            elif state['stage']==4 and window.process is None:
                assert window.gallery.count()==2,window.status.text()
                finish(True,'EXE startup, indexing PNG+PSD, clipboard search, copy path, data migration preserving source and search after migration passed')
        except Exception:
            import traceback
            finish(False,traceback.format_exc())
    timer.timeout.connect(tick)
    timer.start(150)
