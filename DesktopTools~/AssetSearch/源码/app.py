import os
import sys
import io
import json
import time
import sqlite3
import traceback
import multiprocessing as mp
import struct
import threading
import shutil
import tempfile
from concurrent.futures import ThreadPoolExecutor, wait, FIRST_COMPLETED
from pathlib import Path

import numpy as np
import cv2
from PIL import Image, ImageOps
from PySide6.QtCore import Qt, QTimer, QSize, QRect, QPoint
from PySide6.QtGui import QPixmap, QPainter, QColor, QPen, QImage
from PySide6.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout,
    QHBoxLayout, QLabel, QPushButton, QListWidget, QListWidgetItem, QCheckBox,
    QFileDialog, QLineEdit, QSpinBox, QSplitter, QProgressBar, QMessageBox,
    QAbstractItemView, QDialog, QDialogButtonBox, QRubberBand)

VERSION = '0.1.3'
GROUPS = {'PNG': ['.png'], 'JPG': ['.jpg', '.jpeg'], 'WebP': ['.webp'],
          'BMP': ['.bmp'], 'TGA': ['.tga'], 'TIFF': ['.tif', '.tiff'],
          'GIF': ['.gif'], 'PSD / PSB': ['.psd', '.psb']}
# A cached launcher runs the bundled EXE from a versioned runtime directory.
# User data still belongs to the outer, shareable EXE's directory.
BASE = Path(os.environ['ASSET_SEARCH_BASE_DIR']) if os.environ.get('ASSET_SEARCH_BASE_DIR') else (Path(sys.executable).parent if getattr(sys, 'frozen', False) else Path(__file__).resolve().parent.parent)
LOCATION_FILE = Path(os.environ.get('ASSET_SEARCH_LOCATION_FILE',str(BASE/'数据目录.json')))


def read_data_location():
    if os.environ.get('ASSET_SEARCH_DATA'):
        return Path(os.environ['ASSET_SEARCH_DATA'])
    if LOCATION_FILE.exists():
        chosen=Path(json.loads(LOCATION_FILE.read_text(encoding='utf-8'))['directory'])
        return chosen if chosen.is_absolute() else BASE/chosen
    return BASE/'素材搜图数据'


# Startup resolves the persistent location after QApplication exists, so failures can be shown.
DATA = Path(os.environ.get('ASSET_SEARCH_DATA',str(BASE/'素材搜图数据')))


def write_data_location(directory):
    LOCATION_FILE.parent.mkdir(parents=True,exist_ok=True)
    fd,name=tempfile.mkstemp(prefix='.data-location-',suffix='.tmp',dir=LOCATION_FILE.parent)
    try:
        with os.fdopen(fd,'w',encoding='utf-8') as file:
            chosen=Path(directory).resolve()
            stored=chosen.relative_to(BASE.resolve()) if chosen.is_relative_to(BASE.resolve()) else chosen
            json.dump({'directory':str(stored)},file,ensure_ascii=False,indent=2)
        os.replace(name,LOCATION_FILE)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def migrate_data(source,destination,queue):
    source,destination=Path(source).resolve(),Path(destination).resolve()
    if source==destination:
        raise ValueError('已在使用此目录')
    destination.mkdir(parents=True,exist_ok=True)
    managed=['index.sqlite','settings.json','最近扫描日志.txt','错误日志.txt']
    if any((destination/name).exists() for name in managed+['index.sqlite-wal','index.sqlite-shm']):
        raise ValueError('目标目录已有索引或配置，请选择空文件夹，避免覆盖已有数据。')
    with tempfile.TemporaryDirectory(prefix='.素材搜图迁移-',dir=destination) as staging:
        staging=Path(staging)
        old=sqlite3.connect((source/'index.sqlite').as_uri()+'?mode=ro',uri=True)
        new=sqlite3.connect(str(staging/'index.sqlite'))
        last=[0.0]
        def progress(status,remaining,total):
            if time.time()-last[0]>.2:
                queue.put(('progress','正在迁移索引和缩略图…',total-remaining,total))
                last[0]=time.time()
        try:
            old.backup(new,pages=256,progress=progress)
            assert new.execute('PRAGMA quick_check').fetchone()[0]=='ok'
            new.commit()
        finally:
            old.close()
            new.close()
        for name in managed[1:]:
            if (source/name).exists():
                shutil.copy2(source/name,staging/name)
        # All copying and validation finish before activating the new location.
        copied=[]
        try:
            for file in staging.iterdir():
                target=destination/file.name
                # Exclusive creation prevents overwriting a concurrently created file.
                with open(file,'rb') as src,open(target,'xb') as dst:
                    copied.append(target)
                    shutil.copyfileobj(src,dst,1024*1024)
            write_data_location(destination)
        except Exception:
            for file in copied:
                file.unlink(missing_ok=True)
            raise
    queue.put(('relocated',str(destination)))


def database(data):
    Path(data).mkdir(parents=True, exist_ok=True)
    db = sqlite3.connect(str(Path(data) / 'index.sqlite'), timeout=20)
    db.execute('PRAGMA journal_mode=WAL')
    db.execute('CREATE TABLE IF NOT EXISTS images (path TEXT PRIMARY KEY, mtime INTEGER, size INTEGER, width INTEGER, height INTEGER, vec BLOB, thumb BLOB, used REAL)')
    return db


def under(path, roots, recursive=True):
    p = os.path.normcase(os.path.abspath(path))
    for root in roots:
        r = os.path.normcase(os.path.abspath(root)).rstrip('\\/')
        if (p.startswith(r + os.sep) if recursive else os.path.dirname(p) == r):
            return True
    return False


PSD_LOCK = threading.Semaphore(1)


def read_psd_preview(path):
    """Read the merged image directly, seeking past layer pixel data."""
    from psd_tools import PSDImage
    from psd_tools.psd import PSD
    from psd_tools.psd.header import FileHeader
    from psd_tools.psd.color_mode_data import ColorModeData
    from psd_tools.psd.image_resources import ImageResources
    from psd_tools.psd.image_data import ImageData
    from psd_tools.psd.layer_and_mask import LayerAndMaskInformation, LayerInfo
    from psd_tools.constants import ColorMode
    with open(path, 'rb') as file:
        header = FileHeader.read(file)
        if header.depth != 8:
            return PSDImage.open(path).topil()
        color = ColorModeData.read(file)
        resources = ImageResources.read(file)
        size_format = '>Q' if header.version == 2 else '>I'
        size_bytes = struct.calcsize(size_format)
        layer_length = struct.unpack(size_format, file.read(size_bytes))[0]
        layer_start = file.tell()
        layer_count = 0
        if layer_length >= size_bytes + 2:
            info_length = struct.unpack(size_format, file.read(size_bytes))[0]
            if info_length >= 2:
                layer_count = struct.unpack('>h', file.read(2))[0]
        expected = {ColorMode.RGB:3, ColorMode.CMYK:4, ColorMode.GRAYSCALE:1}.get(header.color_mode)
        # Positive layer counts with extra channels may use tagged alpha metadata.
        # Preserve the original decoder for these and for 16/32-bit documents.
        if expected is None or (layer_count > 0 and header.channels > expected):
            return PSDImage.open(path).topil()
        file.seek(layer_start + layer_length)
        record = PSD(header=header, color_mode_data=color, image_resources=resources,
                     layer_and_mask_information=LayerAndMaskInformation(layer_info=LayerInfo(layer_count=layer_count)),
                     image_data=ImageData.read(file))
        return PSDImage(record).topil()


def load_image(path):
    if Path(path).suffix.lower() in ('.psd', '.psb'):
        image = read_psd_preview(path)
        if image is None:
            raise ValueError('PSD 没有可读取的合成预览；请在 Photoshop 中启用兼容性保存')
        return image.convert('RGBA')
    with Image.open(path) as image:
        image.seek(0)
        return ImageOps.exif_transpose(image).convert('RGBA')


def load_index_image(path):
    if Path(path).suffix.lower() in ('.psd', '.psb'):
        with PSD_LOCK:
            image = load_image(path)
            size = image.size
            image.thumbnail((512,512), Image.Resampling.LANCZOS)
            return image, size
    with Image.open(path) as image:
        image.seek(0)
        orientation = image.getexif().get(274,1)
        size = image.size[::-1] if orientation in (5,6,7,8) else image.size
        image.draft('RGB', (512,512))
        image.thumbnail((512,512), Image.Resampling.LANCZOS)
        return ImageOps.exif_transpose(image).convert('RGBA'), size


def foreground(image):
    image = image.copy()
    image.thumbnail((512, 512), Image.Resampling.LANCZOS)
    a = np.asarray(image)
    if a[:, :, 3].min() < 240:
        box = image.getchannel('A').point(lambda v: 255 if v > 20 else 0).getbbox()
        return image.crop(box) if box else image
    rgb = a[:, :, :3].astype(np.float32)
    corners = np.array([rgb[0, 0], rgb[-1, 0], rgb[0, -1], rgb[-1, -1]])
    if np.max(np.std(corners, axis=0)) < 12:
        mask = np.linalg.norm(rgb - np.median(corners, axis=0), axis=2) > 35
        ys, xs = np.where(mask)
        if len(xs) > 20:
            box = (max(0, int(xs.min())-2), max(0, int(ys.min())-2),
                   min(image.width, int(xs.max())+3), min(image.height, int(ys.max())+3))
            return image.crop(box)
    return image


def render(image, background, size=64):
    canvas = Image.new('RGBA', (size, size), (*background, 255))
    image = ImageOps.contain(image, (size-4, size-4), Image.Resampling.LANCZOS)
    canvas.alpha_composite(image, ((size-image.width)//2, (size-image.height)//2))
    return np.asarray(canvas.convert('RGB'))


def vector(image):
    image = foreground(image)
    parts = []
    for bg in [(245, 245, 245), (40, 40, 40)]:
        rgb = render(image, bg)
        small = cv2.resize(rgb, (12, 12)).astype(np.float32).reshape(-1) / 255
        gray = cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY).astype(np.float32) / 255
        dct = cv2.dct(gray)[:12, :12].flatten()[1:]
        dct = dct / max(np.linalg.norm(dct), 1e-6)
        hsv = cv2.cvtColor(rgb, cv2.COLOR_RGB2HSV)
        hist = cv2.calcHist([hsv], [0, 1], None, [12, 4], [0, 180, 0, 256]).flatten()
        hist /= max(np.linalg.norm(hist), 1e-6)
        parts.append(np.concatenate([small / np.sqrt(len(small)), dct * .9, hist * .5]))
    return np.stack(parts).astype(np.float16)


def thumbnail(image):
    image = image.copy()
    image.thumbnail((320, 320), Image.Resampling.LANCZOS)
    buff = io.BytesIO()
    image.save(buff, 'WEBP', quality=78, method=3)
    return buff.getvalue()


def cap_cache(db, mb):
    limit = int(mb) * 1024 * 1024
    total = db.execute('SELECT COALESCE(SUM(LENGTH(thumb)),0) FROM images').fetchone()[0]
    if total > limit:
        for path, size in db.execute('SELECT path,LENGTH(thumb) FROM images WHERE thumb IS NOT NULL ORDER BY used ASC').fetchall():
            db.execute('UPDATE images SET thumb=NULL WHERE path=?', (path,))
            total -= size
            if total <= limit:
                break
    db.commit()


def index_library(data, settings, queue):
    db = database(data)
    count = updated = unchanged = errors = 0
    seen = set()
    complete = []
    log = []
    exts = set(settings['extensions'])
    started = time.time()
    previous = {os.path.normcase(p):(mtime,size) for p,mtime,size in db.execute('SELECT path,mtime,size FROM images')}
    pending = []
    last_progress = 0.0
    for root in settings['roots']:
        root_errors = []
        def failed(exc):
            root_errors.append(str(exc))
            log.append(str(exc))
        try:
            # scandir forces inaccessible roots to surface instead of silently yielding nothing.
            with os.scandir(root):
                pass
            queue.put(('progress', f'正在遍历：{root}', count, 0))
            folders = [os.path.abspath(root)]
            while folders:
                folder = folders.pop()
                try:
                    with os.scandir(folder) as entries:
                        for entry in entries:
                            try:
                                if entry.is_dir(follow_symlinks=False):
                                    if settings['recursive'] and not entry.is_symlink() and not getattr(entry,'is_junction',lambda:False)():
                                        folders.append(entry.path)
                                    continue
                                if Path(entry.name).suffix.lower() not in exts or not entry.is_file():
                                    continue
                                path = os.path.abspath(entry.path)
                                key = os.path.normcase(path)
                                if key in seen:
                                    continue
                                seen.add(key)
                                count += 1
                                # Windows directory enumeration already supplies size and time.
                                stat = entry.stat()
                                if previous.get(key) == (stat.st_mtime_ns, stat.st_size):
                                    unchanged += 1
                                else:
                                    pending.append((path, stat.st_mtime_ns, stat.st_size))
                            except Exception as exc:
                                errors += 1
                                failed(exc)
                            if time.time()-last_progress > .3:
                                queue.put(('progress',f'正在检查文件变化：{count} 张 · 未变化 {unchanged} 张\n{folder}',count,0))
                                last_progress = time.time()
                except OSError as exc:
                    failed(exc)
                    errors += 1
            if not root_errors:
                complete.append(root)
        except Exception as exc:
            errors += 1
            log.append(f'{root}\n  {type(exc).__name__}: {exc}')
    planned = len(pending)
    processed = 0
    workers = max(1, min(8,int(settings.get('workers',4))))
    def encode(job):
        path,mtime,size = job
        image,(width,height) = load_index_image(path)
        if min(width,height) < settings['min_size']:
            return path,None
        return path,(path,mtime,size,width,height,vector(image).tobytes(),thumbnail(image),time.time())
    queue.put(('progress',f'检查完成：{count} 张 · 未变化 {unchanged} 张 · 需要处理 {planned} 张',0,planned))
    # Bound outstanding jobs to limit memory and network requests; only this thread writes SQLite.
    with ThreadPoolExecutor(max_workers=workers) as pool:
        jobs = iter(pending)
        running = {}
        def fill():
            while len(running) < workers:
                job = next(jobs,None)
                if job is None:
                    break
                running[pool.submit(encode,job)] = job[0]
        fill()
        while running:
            finished,_ = wait(running,timeout=.2,return_when=FIRST_COMPLETED)
            for future in finished:
                path = running.pop(future)
                try:
                    path,row = future.result()
                    if row is None:
                        db.execute('DELETE FROM images WHERE path=?',(path,))
                    else:
                        db.execute('INSERT OR REPLACE INTO images VALUES (?,?,?,?,?,?,?,?)',row)
                        updated += 1
                except Exception as exc:
                    errors += 1
                    log.append(f'{path}\n  {type(exc).__name__}: {exc}')
                processed += 1
            fill()
            if time.time()-last_progress>.3 or processed==planned:
                names = ' / '.join(Path(path).name for path in list(running.values())[:2])
                queue.put(('progress',f'生成索引：{processed}/{planned} 张 · 更新 {updated} · 未变化 {unchanged} · 失败 {errors}\n{names}',processed,planned))
                last_progress=time.time()
            if finished and (processed % 30 == 0 or processed==planned):
                db.commit()
            if finished and processed % 200 == 0:
                cap_cache(db,settings['cache_mb'])
    # Only remove missing entries from folders successfully traversed this time.
    for (path,) in db.execute('SELECT path FROM images').fetchall():
        if Path(path).suffix.lower() in exts and under(path, complete, settings['recursive']) and os.path.normcase(path) not in seen:
            db.execute('DELETE FROM images WHERE path=?', (path,))
    cap_cache(db, settings['cache_mb'])
    db.close()
    report = f'检查 {count} 张，更新 {updated} 张，未变化 {unchanged} 张，失败 {errors} 项。耗时 {time.time()-started:.1f} 秒。'
    (Path(data) / '最近扫描日志.txt').write_text(report + '\n\n' + '\n'.join(log), encoding='utf-8')
    queue.put(('done', report, errors))


def detail_score(query, candidate):
    sift = cv2.SIFT_create(nfeatures=180)
    def features(image):
        rgb = render(foreground(image), (245, 245, 245), 256)
        return sift.detectAndCompute(cv2.cvtColor(rgb, cv2.COLOR_RGB2GRAY), None)
    kq, dq = features(query)
    kc, dc = features(candidate)
    if dq is None or dc is None or len(dq) < 4 or len(dc) < 4:
        return 0.0
    pairs = cv2.BFMatcher().knnMatch(dq, dc, k=2)
    good = [a for pair in pairs if len(pair) == 2 for a,b in [pair] if a.distance < .72 * b.distance]
    if len(good) < 4:
        return 0.0
    pts1 = np.float32([kq[m.queryIdx].pt for m in good]).reshape(-1, 1, 2)
    pts2 = np.float32([kc[m.trainIdx].pt for m in good]).reshape(-1, 1, 2)
    _, mask = cv2.findHomography(pts1, pts2, cv2.RANSAC, 5)
    if mask is None:
        return 0.0
    n = int(mask.sum())
    return min(1, n / max(8, min(len(kq), len(kc)) * .4)) * (n / len(good))


def search_library(data, settings, image_bytes, queue):
    started = time.time()
    query = Image.open(io.BytesIO(image_bytes)).convert('RGBA')
    qv = vector(query).astype(np.float32)
    db = database(data)
    rows = [(p, w, h, v) for p,w,h,v in db.execute('SELECT path,width,height,vec FROM images')
            if Path(p).suffix.lower() in settings['extensions'] and under(p, settings['roots'], settings['recursive'])
            and min(w,h) >= settings['min_size']]
    if not rows:
        db.close()
        queue.put(('results', [], '当前选择的库没有索引，请先更新素材库。'))
        return
    queue.put(('progress', f'正在匹配 {len(rows)} 张素材…', 0, 0))
    arr = np.stack([np.frombuffer(r[3], np.float16).reshape(qv.shape) for r in rows]).astype(np.float32)
    # Compare white/dark composites across backgrounds; similarity is a ranking score, not probability.
    diffs = [np.mean((arr[:, a] - qv[b])**2, axis=1) for a in range(2) for b in range(2)]
    dist = np.min(diffs, axis=0)
    candidates = np.argsort(dist)[:60]
    results = []
    errors = 0
    for number, idx in enumerate(candidates):
        path,w,h,_ = rows[int(idx)]
        score = float(np.exp(-float(dist[idx]) * 180))
        try:
            thumb = db.execute('SELECT thumb FROM images WHERE path=?', (path,)).fetchone()[0]
            if thumb:
                candidate = Image.open(io.BytesIO(thumb)).convert('RGBA')
            else:
                candidate = load_image(path)
                thumb = thumbnail(candidate)
                db.execute('UPDATE images SET thumb=?,used=? WHERE path=?', (thumb,time.time(),path))
            detail = detail_score(query, candidate)
            results.append({'path':path, 'width':w, 'height':h, 'score':round(100 * (.7*score+.3*detail),1), 'thumb':thumb})
            db.execute('UPDATE images SET used=? WHERE path=?', (time.time(),path))
        except Exception:
            errors += 1
        if number % 10 == 0:
            queue.put(('progress', f'正在核对候选 {number+1}/{len(candidates)}…', number+1, len(candidates)))
    cap_cache(db, settings['cache_mb'])
    db.close()
    results.sort(key=lambda r:r['score'], reverse=True)
    queue.put(('results', results[:settings['limit']], f'搜索 {len(rows)} 张 · 耗时 {time.time()-started:.2f} 秒 · 无法读取候选 {errors} 张'))


def worker(kind, data, settings, image, queue):
    try:
        if kind == 'relocate':
            migrate_data(data,settings['destination'],queue)
        elif kind == 'index':
            index_library(data,settings,queue)
        else:
            search_library(data,settings,image,queue)
    except Exception:
        queue.put(('error',traceback.format_exc()))


def pil_pixmap(image):
    raw = image.convert('RGBA').tobytes()
    qimage = QImage(raw, image.width, image.height, image.width*4, QImage.Format.Format_RGBA8888).copy()
    return QPixmap.fromImage(qimage)


class CropLabel(QLabel):
    def __init__(self, image):
        super().__init__()
        self.image = image
        self.pix = pil_pixmap(image).scaled(820, 560, Qt.AspectRatioMode.KeepAspectRatio, Qt.TransformationMode.SmoothTransformation)
        self.setPixmap(self.pix)
        self.setFixedSize(self.pix.size())
        self.origin = QPoint()
        self.selection = QRect()
        self.band = QRubberBand(QRubberBand.Shape.Rectangle, self)

    def mousePressEvent(self, event):
        self.origin = event.position().toPoint()
        self.band.setGeometry(QRect(self.origin,QSize()))
        self.band.show()

    def mouseMoveEvent(self,event):
        self.selection = QRect(self.origin,event.position().toPoint()).normalized().intersected(self.rect())
        self.band.setGeometry(self.selection)

    def mouseReleaseEvent(self,event):
        self.mouseMoveEvent(event)

    def cropped(self):
        r = self.selection
        if r.width()<4 or r.height()<4:
            return self.image
        sx,sy = self.image.width/self.width(), self.image.height/self.height()
        return self.image.crop((int(r.x()*sx),int(r.y()*sy),int((r.x()+r.width())*sx),int((r.y()+r.height())*sy)))


class MainWindow(QMainWindow):
    def __init__(self):
        super().__init__()
        self.setWindowTitle(f'素材搜图 {VERSION}')
        self.resize(1200,800)
        DATA.mkdir(parents=True,exist_ok=True)
        database(DATA).close()
        self.process = None
        self.queue = None
        self.query = None
        self.results = []
        try:
            self.config = json.loads((DATA/'settings.json').read_text(encoding='utf-8'))
        except Exception:
            self.config = {'roots':[],
                           'groups':['PNG','JPG','WebP','BMP','TGA','TIFF','GIF','PSD / PSB'],
                           'recursive':True,'cache_mb':500,'min_size':0,'limit':30,'workers':4}
        central = QWidget()
        self.setCentralWidget(central)
        layout = QVBoxLayout(central)
        title = QLabel('素材搜图')
        title.setStyleSheet('font-size:26px;font-weight:700')
        layout.addWidget(title)
        layout.addWidget(QLabel('选素材库 → 更新索引 → 粘贴或拖入截图 → 框选道具 → 搜索原图'))
        split = QSplitter()
        layout.addWidget(split,1)
        left = QWidget()
        left.setMaximumWidth(380)
        ll = QVBoxLayout(left)
        ll.addWidget(QLabel('素材库路径（可以添加多个共享文件夹）'))
        self.roots = QListWidget()
        self.roots.addItems(self.config['roots'])
        self.roots.setWordWrap(True)
        ll.addWidget(self.roots)
        self.path_input = QLineEdit()
        self.path_input.setPlaceholderText('粘贴 \\服务器\共享\文件夹 或本地路径')
        ll.addWidget(self.path_input)
        row = QHBoxLayout()
        self.button(row,'添加路径',self.add_path)
        self.button(row,'浏览…',self.browse)
        self.button(row,'移除',self.remove_path)
        ll.addLayout(row)
        ll.addWidget(QLabel('参与扫描 / 搜索的格式'))
        self.formats={}
        formatsrow=None
        for idx,name in enumerate(GROUPS):
            if idx%4 == 0:
                formatsrow=QHBoxLayout()
                ll.addLayout(formatsrow)
            check=QCheckBox(name)
            check.setChecked(name in self.config['groups'])
            check.toggled.connect(self.save_settings)
            self.formats[name]=check
            formatsrow.addWidget(check)
        self.recursive=QCheckBox('包含子文件夹')
        self.recursive.setChecked(self.config['recursive'])
        self.recursive.toggled.connect(self.save_settings)
        ll.addWidget(self.recursive)
        self.min_size=self.spin(ll,'跳过短边小于（0 = 不过滤）',0,4096,self.config['min_size'],' px')
        self.cache_mb=self.spin(ll,'缩略图缓存上限',50,10000,self.config['cache_mb'],' MB')
        self.limit=self.spin(ll,'显示候选数量',5,60,self.config['limit'],' 张')
        self.workers=self.spin(ll,'更新并行数',1,8,self.config.get('workers',4),'')
        row=QHBoxLayout()
        self.index_button=self.button(row,'更新素材库',lambda:self.start_job('index'))
        self.cancel_button=self.button(row,'取消',self.cancel)
        self.cancel_button.setEnabled(False)
        ll.addLayout(row)
        self.stats=QLabel()
        self.stats.setWordWrap(True)
        ll.addWidget(self.stats)
        row=QHBoxLayout()
        self.clear_button=self.button(row,'清空缩略图缓存',self.clear_cache)
        self.button(row,'扫描日志',lambda:os.startfile(str(DATA/'最近扫描日志.txt')) if (DATA/'最近扫描日志.txt').exists() else None)
        ll.addLayout(row)
        self.data_path_label=QLabel()
        self.data_path_label.setWordWrap(True)
        self.data_path_label.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        ll.addWidget(self.data_path_label)
        row=QHBoxLayout()
        self.data_button=self.button(row,'数据存放目录…',self.choose_data_directory)
        self.button(row,'打开数据目录',lambda:os.startfile(str(DATA)))
        ll.addLayout(row)
        note=QLabel('PSD/PSB 读取合成预览，不搜索独立图层。\nGIF 读取第一帧。原图保留在原位置。\n排序分数用于比较候选，不代表准确率。')
        note.setWordWrap(True)
        ll.addWidget(note)
        split.addWidget(left)
        right=QWidget()
        rl=QVBoxLayout(right)
        row=QHBoxLayout()
        self.button(row,'打开参考图',self.open_query)
        self.button(row,'粘贴截图 Ctrl+V',self.paste)
        self.button(row,'框选道具',self.crop)
        self.search_button=self.button(row,'搜索原图',lambda:self.start_job('search'))
        rl.addLayout(row)
        self.preview=QLabel('把图片拖到窗口，或复制截图后按 Ctrl+V')
        self.preview.setFixedHeight(190)
        self.preview.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.preview.setStyleSheet('background:#e9edf3;border-radius:8px;color:#64748b')
        rl.addWidget(self.preview)
        self.gallery=QListWidget()
        self.gallery.setViewMode(QListWidget.ViewMode.IconMode)
        self.gallery.setResizeMode(QListWidget.ResizeMode.Adjust)
        self.gallery.setMovement(QListWidget.Movement.Static)
        self.gallery.setIconSize(QSize(144,144))
        self.gallery.setGridSize(QSize(190,205))
        self.gallery.setWordWrap(True)
        self.gallery.itemDoubleClicked.connect(self.open_result)
        self.gallery.currentItemChanged.connect(self.show_path)
        rl.addWidget(self.gallery,1)
        self.result_path=QLabel('双击候选打开原图；选中候选可以定位文件夹或复制路径。')
        self.result_path.setWordWrap(True)
        self.result_path.setTextInteractionFlags(Qt.TextInteractionFlag.TextSelectableByMouse)
        rl.addWidget(self.result_path)
        row=QHBoxLayout()
        self.button(row,'打开原图',self.open_result)
        self.button(row,'定位文件',self.locate)
        self.button(row,'复制路径',self.copy_path)
        rl.addLayout(row)
        split.addWidget(right)
        split.setSizes([350,850])
        self.progress=QProgressBar()
        self.progress.setRange(0,100)
        self.progress.setValue(0)
        layout.addWidget(self.progress)
        self.status=QLabel('首次使用请先更新素材库。所有图片处理都在本机完成。')
        self.status.setWordWrap(True)
        layout.addWidget(self.status)
        self.setAcceptDrops(True)
        self.timer=QTimer(self)
        self.timer.timeout.connect(self.poll)
        self.timer.start(100)
        self.refresh_stats()

    def button(self,row,text,slot):
        button=QPushButton(text)
        button.clicked.connect(slot)
        row.addWidget(button)
        return button

    def spin(self,layout,text,minimum,maximum,value,suffix):
        row=QHBoxLayout()
        row.addWidget(QLabel(text))
        spin=QSpinBox()
        spin.setRange(minimum,maximum)
        spin.setValue(value)
        spin.setSuffix(suffix)
        spin.valueChanged.connect(self.save_settings)
        row.addWidget(spin)
        layout.addLayout(row)
        return spin

    def settings(self):
        groups=[n for n,c in self.formats.items() if c.isChecked()]
        return {'roots':[self.roots.item(i).text() for i in range(self.roots.count())],
                'groups':groups,'extensions':[e for n in groups for e in GROUPS[n]],
                'recursive':self.recursive.isChecked(),'min_size':self.min_size.value(),
                'cache_mb':self.cache_mb.value(),'limit':self.limit.value(),'workers':self.workers.value()}

    def save_settings(self,*args):
        if not hasattr(self,'workers'):
            return
        (DATA/'settings.json').write_text(json.dumps(self.settings(),ensure_ascii=False,indent=2),encoding='utf-8')

    def add_path(self):
        path=self.path_input.text().strip().strip('"')
        if path and not any(os.path.normcase(os.path.normpath(self.roots.item(i).text()))==os.path.normcase(os.path.normpath(path)) for i in range(self.roots.count())):
            self.roots.addItem(os.path.normpath(path))
            self.path_input.clear()
            self.save_settings()

    def browse(self):
        path=QFileDialog.getExistingDirectory(self,'选择素材文件夹')
        if path:
            self.path_input.setText(path)
            self.add_path()

    def remove_path(self):
        self.roots.takeItem(self.roots.currentRow())
        self.save_settings()

    def refresh_stats(self):
        db=database(DATA)
        count,total=db.execute('SELECT COUNT(*),COALESCE(SUM(LENGTH(thumb)),0) FROM images').fetchone()
        db.close()
        actual=sum(p.stat().st_size for p in DATA.glob('index.sqlite*') if p.is_file())
        self.stats.setText(f'全部已建索引：{count:,} 张\n缩略图有效数据：{total/1048576:.1f} MB\n数据库磁盘占用：{actual/1048576:.1f} MB')
        self.data_path_label.setText(f'数据目录：{DATA}')

    def choose_data_directory(self):
        if self.process:
            return
        chosen=QFileDialog.getExistingDirectory(self,'选择索引、缓存、配置和日志的存放目录',str(DATA.parent))
        if not chosen or Path(chosen).resolve()==DATA.resolve():
            return
        answer=QMessageBox.question(self,'更改数据存放目录',
            f'把当前索引、缩略图、配置和日志复制到：\n{chosen}\n\n完成后立即切换，原目录保留备份。目标目录不能已有本工具的数据。',
            QMessageBox.StandardButton.Ok|QMessageBox.StandardButton.Cancel)
        if answer==QMessageBox.StandardButton.Ok:
            self.start_relocation(chosen)

    def start_relocation(self,destination):
        if self.process:
            return
        self.save_settings()
        self.queue=mp.get_context('spawn').Queue()
        self.process=mp.get_context('spawn').Process(target=worker,args=('relocate',str(DATA),{'destination':destination},None,self.queue),daemon=True)
        self.process.start()
        self.busy(True)
        # Migration activates the new location atomically; let this short operation finish.
        self.relocating=True
        self.cancel_button.setEnabled(False)
        self.centralWidget().setEnabled(False)
        self.status.setText('正在迁移数据，请等待完成…')

    def clear_cache(self):
        if self.process:
            return
        db=database(DATA)
        db.execute('UPDATE images SET thumb=NULL')
        db.commit()
        db.execute('PRAGMA wal_checkpoint(TRUNCATE)')
        db.execute('VACUUM')
        db.close()
        self.refresh_stats()
        self.status.setText('缩略图缓存已清空，索引保留。下次搜索将重新读取候选原图。')

    def set_query(self,image):
        self.query=image.convert('RGBA')
        self.preview.setPixmap(pil_pixmap(self.query).scaled(self.preview.width(),180,Qt.AspectRatioMode.KeepAspectRatio,Qt.TransformationMode.SmoothTransformation))
        self.status.setText(f'参考图 {image.width} × {image.height}，建议框选单个道具后搜索。')

    def open_query(self):
        path,_=QFileDialog.getOpenFileName(self,'打开参考图','','图片 (*.png *.jpg *.jpeg *.webp *.bmp *.tga *.tif *.tiff *.gif *.psd *.psb)')
        if path:
            # Loading a network PSD can take time: query imports run in a separate process as well.
            self.import_path(path)

    def import_path(self,path):
        if self.process:
            return
        self.queue=mp.get_context('spawn').Queue()
        self.process=mp.get_context('spawn').Process(target=import_worker,args=(path,self.queue),daemon=True)
        self.process.start()
        self.busy(True)
        self.status.setText('正在读取参考图…')

    def paste(self):
        clip=QApplication.clipboard()
        image=clip.image()
        if not image.isNull():
            image=image.convertToFormat(QImage.Format.Format_RGBA8888)
            pil=Image.frombytes('RGBA',(image.width(),image.height()),bytes(image.bits()),'raw','RGBA',image.bytesPerLine())
            self.set_query(pil)
        elif clip.mimeData().hasUrls():
            self.import_path(clip.mimeData().urls()[0].toLocalFile())
        else:
            self.status.setText('剪贴板里没有图片，请先复制截图。')

    def crop(self):
        if self.query is None:
            return
        dialog=QDialog(self)
        dialog.setWindowTitle('拖动框选一个道具，尽量避开文字和边框')
        layout=QVBoxLayout(dialog)
        label=CropLabel(self.query)
        layout.addWidget(label)
        buttons=QDialogButtonBox(QDialogButtonBox.StandardButton.Ok|QDialogButtonBox.StandardButton.Cancel)
        buttons.accepted.connect(dialog.accept)
        buttons.rejected.connect(dialog.reject)
        layout.addWidget(buttons)
        if dialog.exec():
            self.set_query(label.cropped())

    def busy(self,value):
        self.index_button.setEnabled(not value)
        self.search_button.setEnabled(not value)
        self.clear_button.setEnabled(not value)
        self.data_button.setEnabled(not value)
        self.cancel_button.setEnabled(value)
        self.progress.setRange(0,0 if value else 100)
        if not value:
            self.progress.setValue(100)

    def start_job(self,kind):
        if self.process:
            return
        settings=self.settings()
        if not settings['roots'] or not settings['extensions']:
            self.status.setText('请至少选择一个素材文件夹和一种图片格式。')
            return
        image_bytes=None
        if kind=='search':
            if self.query is None:
                self.status.setText('请先粘贴或打开参考图。')
                return
            buff=io.BytesIO()
            self.query.save(buff,'PNG')
            image_bytes=buff.getvalue()
        self.save_settings()
        self.queue=mp.get_context('spawn').Queue()
        self.process=mp.get_context('spawn').Process(target=worker,args=(kind,str(DATA),settings,image_bytes,self.queue),daemon=True)
        self.process.start()
        self.busy(True)
        self.status.setText('正在启动后台任务…')

    def poll(self):
        global DATA
        if not self.process:
            return
        import queue
        terminal=False
        for _ in range(40):
            try:
                message=self.queue.get_nowait()
            except queue.Empty:
                break
            kind=message[0]
            if kind=='progress':
                self.status.setText(message[1])
                if message[3]>0:
                    self.progress.setRange(0,message[3])
                    self.progress.setValue(message[2])
            elif kind=='imported':
                self.set_query(Image.open(io.BytesIO(message[1])).convert('RGBA'))
                terminal=True
            elif kind=='done':
                self.status.setText(message[1]+(' 详情见扫描日志。' if message[2] else ''))
                terminal=True
            elif kind=='relocated':
                DATA=Path(message[1])
                self.status.setText('数据目录已切换。原目录保留备份，确认正常后可自行清理。')
                terminal=True
            elif kind=='results':
                self.results=message[1]
                self.gallery.clear()
                from PySide6.QtGui import QIcon
                for result in self.results:
                    pix=QPixmap()
                    pix.loadFromData(result['thumb'])
                    item=QListWidgetItem(QIcon(pix),f"{result['score']:.1f} 分 · {result['width']}×{result['height']}\n{Path(result['path']).name}")
                    item.setData(Qt.ItemDataRole.UserRole,result['path'])
                    item.setToolTip(result['path'])
                    self.gallery.addItem(item)
                self.status.setText(message[2])
                terminal=True
            elif kind=='error':
                (DATA/'错误日志.txt').write_text(message[1],encoding='utf-8')
                self.status.setText('任务失败：'+message[1].strip().splitlines()[-1]+'；详情见错误日志.txt。')
                terminal=True
        if terminal or (not self.process.is_alive() and self.process.exitcode is not None):
            if not terminal and self.process.exitcode:
                self.status.setText(f'后台任务退出，退出码 {self.process.exitcode}。')
            self.process.join(timeout=.1)
            self.process=None
            self.queue.close()
            self.queue=None
            self.relocating=False
            self.centralWidget().setEnabled(True)
            self.busy(False)
            self.refresh_stats()

    def cancel(self):
        if self.process:
            self.process.terminate()
            self.process.join(timeout=.5)
            self.process=None
            self.queue.close()
            self.queue=None
            self.busy(False)
            self.refresh_stats()
            self.status.setText('已取消，已提交的索引保留，可再次更新。')

    def selected_path(self):
        item=self.gallery.currentItem()
        return item.data(Qt.ItemDataRole.UserRole) if item else None

    def show_path(self,*args):
        path=self.selected_path()
        if path:
            self.result_path.setText(path)

    def open_result(self,*args):
        path=self.selected_path()
        if path:
            try:
                os.startfile(path)
            except OSError as exc:
                self.status.setText(f'无法打开原图：{exc}')

    def locate(self):
        path=self.selected_path()
        if path:
            import subprocess
            subprocess.Popen(['explorer.exe','/select,',os.path.normpath(path)])

    def copy_path(self):
        path=self.selected_path()
        if path:
            QApplication.clipboard().setText(path)
            self.status.setText('文件路径已复制。')

    def keyPressEvent(self,event):
        if event.key()==Qt.Key.Key_V and event.modifiers() & Qt.KeyboardModifier.ControlModifier:
            self.paste()
        else:
            super().keyPressEvent(event)

    def dragEnterEvent(self,event):
        if event.mimeData().hasUrls() or event.mimeData().hasImage():
            event.acceptProposedAction()

    def dropEvent(self,event):
        if event.mimeData().hasUrls():
            self.import_path(event.mimeData().urls()[0].toLocalFile())
        elif event.mimeData().hasImage():
            QApplication.clipboard().setImage(event.mimeData().imageData())
            self.paste()

    def closeEvent(self,event):
        if getattr(self,'relocating',False) and self.process:
            event.ignore()
            return
        self.cancel()
        self.save_settings()
        event.accept()


def import_worker(path,queue):
    try:
        image=load_image(path)
        buff=io.BytesIO()
        image.save(buff,'PNG')
        queue.put(('imported',buff.getvalue()))
    except Exception:
        queue.put(('error',traceback.format_exc()))


def main():
    global DATA
    mp.freeze_support()
    app=QApplication(sys.argv)
    try:
        DATA=read_data_location()
        DATA.mkdir(parents=True,exist_ok=True)
        database(DATA).close()
    except Exception as exc:
        QMessageBox.critical(None,'数据目录无法使用',f'无法打开设置的数据目录：\n{exc}\n\n请恢复目录访问权限，或修改程序旁的“数据目录.json”中的路径。')
        return 1
    app.setStyle('Fusion')
    app.setStyleSheet('QWidget{font-family:"Microsoft YaHei UI";font-size:12px;} QPushButton{padding:7px 9px;} QListWidget{background:#ffffff;border:1px solid #dce2ea;border-radius:5px;} QMainWindow{background:#f5f7fa;}')
    window=MainWindow()
    window.show()
    if os.environ.get('ASSET_SEARCH_STARTUP_REPORT'):
        def startup_ready():
            Path(os.environ['ASSET_SEARCH_STARTUP_REPORT']).write_text(json.dumps(
                {'ready':True,'version':VERSION,'base':str(BASE),'data':str(DATA)},
                ensure_ascii=False),encoding='utf-8')
            if os.environ.get('ASSET_SEARCH_STARTUP_ONLY')=='1':
                window.close()
                app.quit()
        QTimer.singleShot(0,startup_ready)
    if os.environ.get('ASSET_SEARCH_SELFTEST'):
        from selftest import attach
        attach(app,window,DATA)
    return app.exec()


if __name__=='__main__':
    sys.exit(main())
