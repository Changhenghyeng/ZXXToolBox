import os
import sys
import io
import json
import re
import time
import uuid
import asyncio
import threading
import hashlib
import base64
import logging
import datetime
import functools
import contextvars
import subprocess
import winreg
import requests
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Callable
from fastapi import FastAPI, Form, UploadFile, File, Request
from fastapi.responses import JSONResponse, FileResponse
from dotenv import load_dotenv

APP_VERSION = "1.1.5"

# ========== 资源与用户数据目录 ==========
# PyInstaller 单文件模式会把内置资源释放到临时目录；运行数据必须放在
# EXE 旁边，避免退出后丢失，也便于用户备份和迁移。
ASSET_ROOT = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parent))
if getattr(sys, "frozen", False):
    DATA_ROOT = Path(sys.executable).resolve().parent / "AI-Image-Tool-Data"
else:
    DATA_ROOT = Path(__file__).resolve().parent

DATA_ROOT.mkdir(parents=True, exist_ok=True)
LOG_DIR = DATA_ROOT / "logs"
LOG_DIR.mkdir(parents=True, exist_ok=True)

# ========== 日志打点系统 ==========
LOG_FMT = logging.Formatter(
    "%(asctime)s | %(levelname)-5s | %(message)s",
    datefmt="%m-%d %H:%M:%S",
)

def _make_logger(name: str, file: str) -> logging.Logger:
    lg = logging.getLogger(name)
    lg.setLevel(logging.DEBUG)
    # Windowed EXE 没有 stdout/stderr；源码调试时才输出到控制台。
    if sys.stderr is not None:
        ch = logging.StreamHandler()
        ch.setLevel(logging.DEBUG)
        ch.setFormatter(LOG_FMT)
        lg.addHandler(ch)
    # 文件: 单文件最大 3MB, 保留最近 3 个备份, 总计不超过 12MB
    fh = RotatingFileHandler(
        LOG_DIR / file, encoding="utf-8",
        maxBytes=3 * 1024 * 1024, backupCount=3,
    )
    fh.setLevel(logging.DEBUG)
    fh.setFormatter(LOG_FMT)
    lg.addHandler(fh)
    return lg

app_log = _make_logger("app", "app.log")
api_log = _make_logger("api", "api.log")

_trace_ctx: contextvars.ContextVar[str] = contextvars.ContextVar("trace_id", default="-")

def trace_id() -> str:
    return _trace_ctx.get()

def new_trace() -> str:
    tid = datetime.datetime.now().strftime("%m%d%H%M%S") + "-" + uuid.uuid4().hex[:6]
    _trace_ctx.set(tid)
    return tid

app = FastAPI()


# ========== 启动自检 & 运行维护 ==========
@app.on_event("startup")
def startup_check():
    """进程启动只做本地检查；联网验证需要浏览器提供保存在 localStorage 的 Key。"""
    app_log.info("服务已启动，等待网页执行模型清单、Host 与 API Key 联合验证")


def _load_models_config() -> list[dict]:
    """加载模型配置文件"""
    # 如果数据目录里有自定义配置则优先使用，否则读取 EXE 内置默认配置。
    fp = DATA_ROOT / "models.json"
    if not fp.exists():
        fp = ASSET_ROOT / "models.json"
    if not fp.exists():
        return []
    try:
        return json.loads(fp.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return []


LATEST_MODELS: list[dict] = []
MODELS_SOURCE_URL = "https://grsai.com/zh/dashboard/models"


def _fetch_latest_image_models(timeout: float = 12) -> list[dict]:
    """从 Grsai 官方模型页内嵌的 Next.js 数据中读取当前可用图片模型。"""
    response = requests.get(MODELS_SOURCE_URL, timeout=timeout)
    response.raise_for_status()
    decoder = json.JSONDecoder()
    pattern = r'self\.__next_f\.push\(\[1,("(?:\\.|[^"\\])*")\]\)</script>'
    raw_models = None
    for match in re.finditer(pattern, response.text):
        chunk = json.loads(match.group(1))
        marker = '"models":'
        pos = chunk.find(marker)
        if pos < 0:
            continue
        try:
            value, _ = decoder.raw_decode(chunk, pos + len(marker))
        except json.JSONDecodeError:
            continue
        if isinstance(value, list):
            raw_models = value
            break
    if raw_models is None:
        raise ValueError("官网页面中未找到模型数据")

    result = []
    for item in raw_models:
        if item.get("type") != "image" or item.get("closeModel"):
            continue
        name = str(item.get("name") or "").strip()
        if not name:
            continue
        if name.startswith("gpt-image"):
            group = "GPT Image"
        elif name.startswith("nano-banana"):
            group = "Nano Banana"
        elif name.startswith("flux"):
            group = "Flux"
        else:
            group = "其他图片模型"
        cost = item.get("cost")
        price = f"{cost / 20000:g}" if isinstance(cost, (int, float)) else "?"
        result.append({
            "id": name,
            "group": group,
            "desc": str(item.get("desc") or "").strip(),
            "price": price,
            "credits": int(cost) if isinstance(cost, (int, float)) else None,
            "disabled": False,
        })
    if not result:
        raise ValueError("官网没有返回可用图片模型")
    return result


@app.get("/health")
def health_check():
    """手动健康检查 (不消耗 API 额度)"""
    result = {"status": "ok", "checks": {}, "ping": {}}

    # 各候选地址延迟检测
    for base in API_CANDIDATES:
        t0 = time.time()
        try:
            r = requests.get(base, timeout=5, allow_redirects=True)
            result["ping"][base] = round((time.time() - t0) * 1000)
        except Exception:
            result["ping"][base] = -1

    # API 连通性
    try:
        r = requests.get(API_BASE, timeout=5, allow_redirects=True)
        result["checks"]["api_reachable"] = True
    except Exception:
        result["checks"]["api_reachable"] = False
        result["status"] = "degraded"

    # 各端点存活
    for name, ep in {"nano-banana": "/v1/draw/nano-banana", "gpt": "/v1/draw/completions", "flux": "/v1/draw/flux"}.items():
        try:
            r = requests.head(f"{API_BASE}{ep}", timeout=5)
            result["checks"][name] = "up" if r.status_code < 500 else "down"
        except Exception:
            result["checks"][name] = "down"
            result["status"] = "degraded"

    # 历史记录和本地图片统计
    records = load_history()
    result["stats"] = {
        "total_generations": len(records),
        "cached_images": len(list(IMAGES_DIR.glob("*"))),
        "ref_images": len(list(REFS_DIR.glob("*"))),
    }

    return JSONResponse(result)


@app.post("/reprobe")
def reprobe():
    """重新探测所有 API 地址的延迟并排序 (用户切换 Host 后触发)"""
    auto_detect_api_base()
    results = {}
    for base in API_CANDIDATES:
        host = base.replace("https://", "").replace("http://", "").split("/")[0]
        try:
            import socket
            t0 = time.time()
            s = socket.create_connection((host, 443), timeout=3)
            latency = round((time.time() - t0) * 1000)
            s.close()
            results[base] = {"latency_ms": latency, "reachable": True}
        except Exception:
            results[base] = {"latency_ms": -1, "reachable": False}
    return JSONResponse({
        "status": "ok",
        "current": API_BASE,
        "candidates": results,
    })


# FastAPI 中间件: 为每个请求生成 trace_id
@app.middleware("http")
async def trace_middleware(request: Request, call_next):
    tid = new_trace()
    app_log.info("[%s] --> %s %s", tid, request.method, request.url.path)
    t0 = time.perf_counter()
    response = await call_next(request)
    elapsed = time.perf_counter() - t0
    app_log.info("[%s] <-- %s %s HTTP %s (%.2fs)",
                 tid, request.method, request.url.path, response.status_code, elapsed)
    return response

# 读取 .env 里的 key
load_dotenv(dotenv_path=DATA_ROOT / ".env")
GRSAI_API_KEY = os.getenv("GRSAI_API_KEY")

# grsai 的接口地址 (按优先级排序, 启动时自动检测)
API_CANDIDATES = [
    "https://api.grsai.ai",
    "https://api.grsai.com",
    "https://grsai.dakka.com.cn",
]
DEFAULT_API_BASE = "https://api.grsai.ai"
API_BASE = DEFAULT_API_BASE  # 仅供健康检查展示；生成请求以界面选择为准
_api_base_lock = threading.Lock()

# 全局连接池 (每个 base 一个 session, 复用 TCP 连接)
_sessions: dict[str, requests.Session] = {}


def _probe_api_host(base: str, api_key: str, timeout: float = 7) -> dict:
    """用官方余额查询验证节点和 Key；该请求不生成内容、不消耗绘图额度。"""
    started = time.perf_counter()
    try:
        response = requests.get(
            f"{base}/client/common/getCredits",
            params={"apikey": api_key},
            timeout=timeout,
        )
        latency = round((time.perf_counter() - started) * 1000)
        try:
            payload = response.json()
        except ValueError:
            payload = {}
        code = payload.get("code")
        key_valid = response.status_code == 200 and code == 0
        return {
            "host": base,
            "reachable": response.status_code < 500,
            "key_valid": key_valid,
            "latency_ms": latency,
            "message": "API Key 有效" if key_valid else str(payload.get("msg") or f"HTTP {response.status_code}"),
        }
    except Exception as exc:
        return {
            "host": base,
            "reachable": False,
            "key_valid": False,
            "latency_ms": -1,
            "message": f"{type(exc).__name__}: {exc}",
        }


def _extract_balance(payload) -> float | int | None:
    if isinstance(payload, dict):
        for key in ("credits", "credit", "balance", "remainingCredits", "apiKeyCredits", "totalCredits"):
            value = payload.get(key)
            if isinstance(value, (int, float)):
                return value
            if isinstance(value, str):
                try:
                    return float(value.replace(",", ""))
                except ValueError:
                    pass
        # 只递归容器，绝不能把 code: 0、count 等普通数字误认为余额。
        for value in payload.values():
            if isinstance(value, (dict, list)):
                found = _extract_balance(value)
                if found is not None:
                    return found
    elif isinstance(payload, list):
        for value in payload:
            if isinstance(value, (dict, list)):
                found = _extract_balance(value)
                if found is not None:
                    return found
    return None


@app.post("/balance")
def official_balance(api_key: str = Form(""), host: str = Form("")):
    """读取 Grsai 官方 API Key 当前积分余额。"""
    key = api_key.strip() or (GRSAI_API_KEY or "").strip()
    if not key:
        return JSONResponse({"ok": False, "error": "请先填写 API Key"}, status_code=400)
    base = host.strip().rstrip("/")
    if base not in API_CANDIDATES:
        base = API_BASE if API_BASE in API_CANDIDATES else DEFAULT_API_BASE
    try:
        response = requests.get(
            f"{base}/client/common/getCredits",
            params={"apikey": key}, timeout=10,
        )
        payload = response.json()
        if response.status_code != 200 or payload.get("code") != 0:
            return JSONResponse({"ok": False, "error": str(payload.get("msg") or "余额查询失败")}, status_code=400)
        balance = _extract_balance(payload)
        # 某些节点会直接把余额作为 data 数值返回。
        if balance is None:
            raw_data = payload.get("data")
            if isinstance(raw_data, (int, float)):
                balance = raw_data
            elif isinstance(raw_data, str):
                try:
                    balance = float(raw_data.replace(",", ""))
                except ValueError:
                    pass
        if balance is None:
            return JSONResponse({"ok": False, "error": "Grsai 返回成功，但未识别余额字段"}, status_code=502)
        return {"ok": True, "balance": balance, "host": base, "checked_at": time.strftime("%H:%M:%S")}
    except Exception as exc:
        return JSONResponse({"ok": False, "error": f"余额查询失败: {type(exc).__name__}"}, status_code=502)


@app.post("/startup-check")
def startup_validation(
    api_key: str = Form(""),
    preferred_host: str = Form(""),
):
    """每次打开网页时并行刷新官网模型、验证 Key，并选出最快可用节点。"""
    import concurrent.futures

    global API_BASE, LATEST_MODELS
    key = api_key.strip() or (GRSAI_API_KEY or "").strip()
    preferred = preferred_host.strip().rstrip("/")
    if preferred not in API_CANDIDATES:
        preferred = ""

    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        models_future = pool.submit(_fetch_latest_image_models)
        probe_futures = {
            pool.submit(_probe_api_host, base, key): base
            for base in API_CANDIDATES
        }
        probes = [future.result() for future in probe_futures]
        try:
            models = models_future.result()
            models_error = ""
        except Exception as exc:
            models = []
            models_error = f"{type(exc).__name__}: {exc}"

    probes.sort(key=lambda item: item["latency_ms"] if item["latency_ms"] >= 0 else 999999)
    valid_probes = [item for item in probes if item["key_valid"]]
    selected = None
    fallback = False
    if preferred:
        selected = next((item for item in valid_probes if item["host"] == preferred), None)
        if selected is None and valid_probes:
            selected = valid_probes[0]
            fallback = True
    elif valid_probes:
        selected = valid_probes[0]

    if selected is not None:
        with _api_base_lock:
            API_BASE = selected["host"]
    if models:
        LATEST_MODELS = models

    key_valid = bool(valid_probes)
    models_ok = bool(models)
    return JSONResponse({
        "ready": bool(key and key_valid and models_ok and selected),
        "key_present": bool(key),
        "key_valid": key_valid,
        "models_ok": models_ok,
        "models_error": models_error,
        "models_source": MODELS_SOURCE_URL,
        "models": models,
        "model_count": len(models),
        "selected_host": selected["host"] if selected else "",
        "selected_latency_ms": selected["latency_ms"] if selected else -1,
        "preferred_host": preferred,
        "fallback": fallback,
        "hosts": probes,
    })


@app.post("/host-check")
def host_validation(api_key: str = Form(""), preferred_host: str = Form("")):
    """后台并发刷新节点延迟；不抓模型、不阻塞当前生成请求。"""
    import concurrent.futures

    global API_BASE
    key = api_key.strip() or (GRSAI_API_KEY or "").strip()
    preferred = preferred_host.strip().rstrip("/")
    if preferred not in API_CANDIDATES:
        preferred = ""
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        probes = list(pool.map(lambda base: _probe_api_host(base, key), API_CANDIDATES))
    probes.sort(key=lambda item: item["latency_ms"] if item["latency_ms"] >= 0 else 999999)
    valid = [item for item in probes if item["key_valid"]]
    selected = next((item for item in valid if item["host"] == preferred), None) if preferred else None
    selected = selected or (valid[0] if valid else None)
    if selected:
        with _api_base_lock:
            API_BASE = selected["host"]
    return {
        "key_valid": bool(valid),
        "selected_host": selected["host"] if selected else "",
        "selected_latency_ms": selected["latency_ms"] if selected else -1,
        "hosts": probes,
        "checked_at": time.strftime("%H:%M:%S"),
    }


def _get_session(base: str) -> requests.Session:
    """获取或创建指定 base 的连接池"""
    if base not in _sessions:
        s = requests.Session()
        s.headers.update({"Authorization": f"Bearer {GRSAI_API_KEY}", "Content-Type": "application/json"})
        _sessions[base] = s
    return _sessions[base]


def _is_connection_error(err: Exception) -> bool:
    """
    判断是否为真正的连接层错误 (请求没有到达服务器)。
    注意: 不包含 ReadTimeout (服务器可能已收到并正在处理, 重试会重复计费)。
    """
    import requests as _r
    # 连接错误: DNS 失败、TCP 被拒/重置、TLS 握手失败 → 服务器没收到请求
    if isinstance(err, _r.ConnectionError):
        return True
    # 仅 ConnectTimeout (TCP 连接超时) → 服务器没收到请求
    if isinstance(err, _r.exceptions.ConnectTimeout):
        return True
    # SSLError → TLS 握手失败, 请求未发出
    if isinstance(err, _r.exceptions.SSLError):
        return True
    # 注意: ReadTimeout 不在此列 —— 请求已发出, 服务器可能在处理, 重试 = 重复计费
    return False


def _resolve_api_base(api_base: str = "") -> str:
    """只接受界面内置 Host；留空时使用本次启动验证选出的最快节点。"""
    base = (api_base or "").strip().rstrip("/")
    return base if base in API_CANDIDATES else API_BASE


def preflight_check(api_key: str = "", timeout: float = 5, api_base: str = "") -> str:
    """
    轻量预检: 用 HEAD 请求遍历候选地址, 找到第一个可达的就切换过去 (不消耗额度)。
    API_CANDIDATES 已按延迟排序, 所以始终优先选延迟最低的可用地址。
    加锁保护: 多线程同时生成时只做一次预检。
    返回 True/False。
    """
    global API_BASE
    base = _resolve_api_base(api_base)
    with _preflight_lock:
        key = api_key.strip() or GRSAI_API_KEY
        headers = {"Authorization": f"Bearer {key}"} if key else {}
        try:
            r = requests.head(base, headers=headers, timeout=timeout, allow_redirects=True)
            reachable = r.status_code < 500
            api_log.info("[预检] %s → HTTP %s (%s)", base, r.status_code,
                         "可达" if reachable else "不可达")
            if reachable:
                return base
        except Exception as e:
            api_log.warning("[预检] %s 不可达: %s", base, e)

        # 当前节点失效时才并发探测备用节点，正常生成不增加额外等待。
        import concurrent.futures
        alternatives = [candidate for candidate in API_CANDIDATES if candidate != base]

        def probe(candidate: str):
            started = time.perf_counter()
            try:
                response = requests.head(
                    candidate, headers=headers, timeout=timeout, allow_redirects=True
                )
                if response.status_code < 500:
                    return round((time.perf_counter() - started) * 1000), candidate
            except Exception:
                pass
            return 999999, ""

        with concurrent.futures.ThreadPoolExecutor(max_workers=len(alternatives) or 1) as pool:
            results = list(pool.map(probe, alternatives))
        results.sort()
        if results and results[0][1]:
            API_BASE = results[0][1]
            api_log.warning("[预检] 自动切换备用节点: %s", API_BASE)
            return API_BASE
        return ""


# 预检锁 (避免多线程重复探测)
_preflight_lock = threading.Lock()


def api_request(method: str, path: str, api_key: str = "", api_base: str = "", **kwargs) -> requests.Response:
    """
    API 请求: 预检已确认当前 API_BASE 可达, 直接发请求, 不重试不切换。
    目标是: 1 次用户操作 = 1 次 POST (不多花一分钱)。
    """
    custom_key = api_key.strip() if api_key else ""
    timeout = kwargs.pop("timeout", 30)
    base = _resolve_api_base(api_base)

    if custom_key:
        return requests.request(method, f"{base}{path}",
            headers={"Authorization": f"Bearer {custom_key}", "Content-Type": "application/json"},
            timeout=timeout, **kwargs)
    else:
        sess = _get_session(base)
        return sess.request(method, f"{base}{path}", timeout=timeout, **kwargs)

def auto_detect_api_base():
    """启动时按延迟排序 API 地址, 最快的优先使用"""
    global API_BASE, API_CANDIDATES
    import socket
    results = []
    for base in API_CANDIDATES:
        host = base.replace("https://", "").replace("http://", "").split("/")[0]
        try:
            t0 = time.time()
            s = socket.create_connection((host, 443), timeout=3)
            latency = round((time.time() - t0) * 1000)
            s.close()
            results.append((latency, base))
            app_log.info("API %s 延迟: %dms", base, latency)
        except Exception:
            app_log.warning("API %s 不可达", base)
            results.append((9999, base))
    results.sort(key=lambda x: x[0])
    API_CANDIDATES = [b for _, b in results]
    API_BASE = API_CANDIDATES[0]
    app_log.info("API 地址选定 (最低延迟): %s", API_BASE)

# 历史记录文件
HISTORY_FILE = DATA_ROOT / "history.json"
REFS_DIR = DATA_ROOT / "refs"
REFS_DIR.mkdir(parents=True, exist_ok=True)
IMAGES_DIR = DATA_ROOT / "images"
IMAGES_DIR.mkdir(parents=True, exist_ok=True)


def _directory_stats(path: Path) -> dict:
    files = [item for item in path.rglob("*") if item.is_file()] if path.exists() else []
    return {"files": len(files), "bytes": sum(item.stat().st_size for item in files)}


@app.get("/app-info")
def app_info():
    records = load_history()
    return JSONResponse({
        "version": APP_VERSION,
        "data_root": str(DATA_ROOT),
        "history": {"files": len(records), "bytes": HISTORY_FILE.stat().st_size if HISTORY_FILE.exists() else 0},
        "images": _directory_stats(IMAGES_DIR),
        "refs": _directory_stats(REFS_DIR),
        "logs": _directory_stats(LOG_DIR),
    })


@app.post("/open-data-folder")
def open_data_folder(kind: str = Form("root")):
    targets = {
        "root": DATA_ROOT,
        "images": IMAGES_DIR,
        "refs": REFS_DIR,
        "logs": LOG_DIR,
    }
    target = targets.get(kind)
    if target is None:
        return JSONResponse({"error": "未知的数据目录"}, status_code=400)
    target.mkdir(parents=True, exist_ok=True)
    try:
        os.startfile(str(target))
        return {"ok": True}
    except Exception as exc:
        return JSONResponse({"error": str(exc)}, status_code=500)


def _find_system_chrome() -> str | None:
    for hive, key_path in (
        (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
    ):
        try:
            with winreg.OpenKey(hive, key_path) as key:
                value, _ = winreg.QueryValueEx(key, None)
            if value and Path(value).is_file():
                return value
        except OSError:
            pass
    for path in (
        Path(os.environ.get("PROGRAMFILES", "")) / "Google/Chrome/Application/chrome.exe",
        Path(os.environ.get("PROGRAMFILES(X86)", "")) / "Google/Chrome/Application/chrome.exe",
        Path(os.environ.get("LOCALAPPDATA", "")) / "Google/Chrome/Application/chrome.exe",
    ):
        if path.is_file():
            return str(path)
    return None


def _chrome_profile_active_times() -> dict[str, float]:
    local_state = Path(os.environ.get("LOCALAPPDATA", "")) / "Google/Chrome/User Data/Local State"
    try:
        data = json.loads(local_state.read_text(encoding="utf-8"))
        info_cache = data.get("profile", {}).get("info_cache", {})
    except (OSError, json.JSONDecodeError):
        return {}
    profiles = {}
    for directory, info in info_cache.items():
        if not isinstance(info, dict):
            continue
        try:
            profiles[directory] = float(info.get("active_time") or 0)
        except (TypeError, ValueError):
            profiles[directory] = 0
    return profiles


def _chrome_window_handles() -> set[int]:
    if os.name != "nt":
        return set()
    try:
        import ctypes
        from ctypes import wintypes

        handles: set[int] = set()
        user32 = ctypes.windll.user32
        callback_type = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)

        def collect(hwnd, _):
            if not user32.IsWindowVisible(hwnd):
                return True
            class_name = ctypes.create_unicode_buffer(128)
            user32.GetClassNameW(hwnd, class_name, len(class_name))
            if class_name.value == "Chrome_WidgetWin_1":
                handles.add(int(hwnd))
            return True

        user32.EnumWindows(callback_type(collect), 0)
        return handles
    except Exception:
        return set()


def _open_grsai_after_profile_choice(
    chrome: str,
    before_profiles: dict[str, float],
    before_windows: set[int],
) -> None:
    """Wait for Chrome's own picker, then open Grsai in the chosen profile."""
    target_url = "https://grsai.com/zh/dashboard"
    seen_picker_windows: set[int] = set()
    guest_transition_at: float | None = None
    deadline = time.monotonic() + 120

    while time.monotonic() < deadline:
        time.sleep(0.5)
        active = _chrome_profile_active_times()
        changed = [
            (value, directory)
            for directory, value in active.items()
            if value > before_profiles.get(directory, 0) + 0.01
        ]
        if changed:
            _, directory = max(changed)
            try:
                subprocess.Popen(
                    [chrome, f"--profile-directory={directory}", target_url],
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
                app_log.info("Chrome profile handoff complete: %s", directory)
            except Exception:
                app_log.exception("Unable to open Grsai in selected Chrome profile")
            return

        current_new_windows = _chrome_window_handles() - before_windows
        if (
            seen_picker_windows
            and (seen_picker_windows - current_new_windows)
            and current_new_windows
            and guest_transition_at is None
        ):
            guest_transition_at = time.monotonic()
        if current_new_windows:
            seen_picker_windows.update(current_new_windows)

        # Guest mode has no persistent profile entry. After the picker window
        # is replaced by a new Chrome window and no profile became active,
        # continue explicitly in Guest mode.
        if guest_transition_at and time.monotonic() - guest_transition_at >= 1.5:
            try:
                subprocess.Popen(
                    [chrome, "--guest", target_url],
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
                app_log.info("Chrome guest handoff complete")
            except Exception:
                app_log.exception("Unable to open Grsai in Chrome Guest mode")
            return

    app_log.warning("Chrome profile handoff timed out before a profile was selected")


@app.post("/open-official")
def open_official():
    chrome = _find_system_chrome()
    if not chrome:
        return JSONResponse({"ok": False, "error": "未找到 Google Chrome"}, status_code=400)
    before_profiles = _chrome_profile_active_times()
    before_windows = _chrome_window_handles()
    try:
        subprocess.Popen(
            [chrome, "chrome://profile-picker/"],
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
        threading.Thread(
            target=_open_grsai_after_profile_choice,
            args=(chrome, before_profiles, before_windows),
            daemon=True,
            name="chrome-profile-handoff",
        ).start()
        return {"ok": True}
    except Exception as exc:
        return JSONResponse({"ok": False, "error": str(exc)}, status_code=500)

# ---------- 模型分组 ----------
NANO_BANANA_MODELS = {
    "nano-banana", "nano-banana-fast", "nano-banana-pro",
    "nano-banana-pro-vt", "nano-banana-pro-cl", "nano-banana-pro-vip",
    "nano-banana-2", "nano-banana-2-cl", "nano-banana-2-4k-cl",
}
GPT_IMAGE_MODELS = {
    "gpt-image-1.5", "gpt-image-2", "gpt-image-2-vip",
}
FLUX_MODELS = {
    "flux-pro", "flux-pro-1.1", "flux-kontext-pro",
}


def get_endpoint(model: str) -> str:
    if model in NANO_BANANA_MODELS:
        return "/v1/draw/nano-banana"
    elif model in GPT_IMAGE_MODELS:
        return "/v1/draw/completions"
    elif model in FLUX_MODELS:
        return "/v1/draw/flux"
    else:
        if any(model.startswith(p) for p in ("nano-banana",)):
            return "/v1/draw/nano-banana"
        elif any(model.startswith(p) for p in ("gpt-image",)):
            return "/v1/draw/completions"
        elif any(model.startswith(p) for p in ("flux",)):
            return "/v1/draw/flux"
        return "/v1/draw/nano-banana"


# ---------- 历史记录 ----------
def load_history() -> list[dict]:
    if not HISTORY_FILE.exists():
        return []
    try:
        return json.loads(HISTORY_FILE.read_text(encoding="utf-8"))
    except (json.JSONDecodeError, OSError):
        return []


def save_history(records: list[dict]):
    HISTORY_FILE.write_text(
        json.dumps(records, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


# ---------- 路由 ----------
@app.get("/")
def home():
    return FileResponse(
        str(ASSET_ROOT / "index.html"),
        headers={"Cache-Control": "no-cache, no-store, must-revalidate"},
    )


@app.get("/images/{filename}")
def serve_image(filename: str):
    """提供本地缓存的图片, 解决 CDN 过期问题"""
    fp = IMAGES_DIR / filename
    if not fp.exists():
        # 如果文件不存在, 尝试从文件名中提取 id 查询 source_url 做重定向
        return JSONResponse({"error": "图片不存在或已过期"}, status_code=404)
    return FileResponse(str(fp))


@app.get("/models")
def models_get():
    """返回本次使用前从 Grsai 官网刷新的可用图片模型。"""
    return JSONResponse(LATEST_MODELS)


@app.get("/refresh-models")
def models_refresh():
    """应用运行期间手动或定时刷新官网模型与最新价格。"""
    global LATEST_MODELS
    try:
        models = _fetch_latest_image_models()
        LATEST_MODELS = models
        return {"ok": True, "models": models, "checked_at": time.strftime("%H:%M:%S")}
    except Exception as exc:
        return JSONResponse({"ok": False, "error": str(exc)}, status_code=502)


@app.get("/history")
def history_get():
    records = load_history()
    return JSONResponse(list(reversed(records)))


@app.delete("/history/{record_id}")
def history_delete(record_id: str):
    records = load_history()
    item = next((r for r in records if r.get("id") == record_id), None)
    records = [r for r in records if r.get("id") != record_id]

    # 清理本地图片
    for f in IMAGES_DIR.glob(f"{record_id}*"):
        try:
            f.unlink()
        except OSError:
            pass

    # 清理参考图 (仅当无其他记录引用相同哈希时才删除)
    if item and item.get("ref_hashes"):
        all_hashes: set[str] = set()
        for r in records:
            for h in r.get("ref_hashes", []):
                all_hashes.add(h)
        for h in item["ref_hashes"]:
            if h not in all_hashes:
                fp = REFS_DIR / h
                try:
                    fp.unlink()
                except OSError:
                    pass

    save_history(records)
    return {"ok": True}


@app.post("/history/batch-delete")
async def history_batch_delete(request: Request):
    """批量删除历史记录"""
    data = await request.json()
    ids = data.get("ids", [])
    records = load_history()
    removed: list[dict] = []

    for record_id in ids:
        item = next((r for r in records if r.get("id") == record_id), None)
        if not item:
            continue
        records = [r for r in records if r.get("id") != record_id]
        removed.append(item)

        for f in IMAGES_DIR.glob(f"{record_id}*"):
            try:
                f.unlink()
            except OSError:
                pass

    if removed:
        all_hashes: set[str] = set()
        for r in records:
            for h in r.get("ref_hashes", []):
                all_hashes.add(h)
        for item in removed:
            for h in item.get("ref_hashes", []):
                if h not in all_hashes:
                    fp = REFS_DIR / h
                    try:
                        fp.unlink()
                    except OSError:
                        pass

    save_history(records)
    return {"ok": True, "deleted": len(removed)}


@app.get("/history/{record_id}/refs")
def history_refs(record_id: str):
    """返回某条历史记录的参考图 (base64 data URL 数组)"""
    records = load_history()
    item = next((r for r in records if r.get("id") == record_id), None)
    if not item:
        return JSONResponse([], status_code=404)

    ref_hashes = item.get("ref_hashes", [])
    # 兼容旧数据: 有 has_ref 但无 ref_hashes, 按旧命名规则查找
    if not ref_hashes and item.get("has_ref"):
        old_files = sorted(REFS_DIR.glob(f"{record_id}_*"))
        ref_hashes = [f.name for f in old_files]

    if not ref_hashes:
        return JSONResponse([], status_code=404)

    refs = []
    for fname in ref_hashes:
        fp = REFS_DIR / fname
        if fp.exists():
            ext = fp.suffix.lstrip(".") or "png"
            b64 = base64.b64encode(fp.read_bytes()).decode()
            refs.append(f"data:image/{ext};base64,{b64}")
    return JSONResponse(refs)


async def _build_payload(
    prompt: str, model: str, aspect_ratio: str, image_size: str,
    images: list, labels_str: str, tid: str,
) -> tuple[dict, str]:
    """构建 API 请求体, 返回 (payload, ref_info)。
    images: List[UploadFile] (可为空列表)
    labels_str: JSON 数组字符串, 如 '["人物A","衣服B"]'
    """
    payload: dict = {
        "model": model,
        "prompt": prompt,
        "urls": [],
        "shutProgress": True,
    }
    if aspect_ratio and aspect_ratio != "auto":
        payload["aspectRatio"] = aspect_ratio
    if image_size:
        payload["imageSize"] = image_size

    # 解析标签
    labels = []
    if labels_str:
        try:
            labels = json.loads(labels_str)
            if not isinstance(labels, list):
                labels = []
        except (json.JSONDecodeError, TypeError):
            labels = []

    ref_info = "none"
    if images:
        ref_urls, ref_names, ref_labels = [], [], []
        for i, ref in enumerate(images):
            if ref is None:
                continue
            raw = await ref.read()
            raw = _compress_ref_image(raw, ref.content_type)
            b64 = base64.b64encode(raw).decode()
            ref_urls.append(f"data:image/jpeg;base64,{b64}")
            ref_names.append(ref.filename or "?")
            label = (labels[i] if i < len(labels) else "").strip()
            if label:
                ref_labels.append(label)
        payload["urls"] = ref_urls
        if ref_labels and len(ref_labels) == len(ref_urls):
            desc = "、".join(f"第{j+1}张参考图是「{lb}」" for j, lb in enumerate(ref_labels))
            payload["prompt"] = f"【参考图说明】{desc}。\n{prompt}"
        ref_info = f"files={ref_names} labels={ref_labels}"
    return payload, ref_info


@app.post("/generate")
async def generate(
    prompt: str = Form(...),
    model: str = Form("nano-banana-2"),
    images: list[UploadFile] = File(default=[]),
    labels: str = Form("[]"),
    aspect_ratio: str = Form("auto"),
    image_size: str = Form(""),
    count: int = Form(1),
    api_key: str = Form(""),
    host: str = Form(""),
    request: Request = None,
):
    tid = trace_id()
    t_total = time.perf_counter()
    count = max(1, min(count, 4))
    effective_key = api_key.strip() or GRSAI_API_KEY
    if not effective_key:
        return JSONResponse({"error": "未配置 API Key，请在设置面板中填入"}, status_code=401)

    endpoint = get_endpoint(model)
    headers = {
        "Authorization": f"Bearer {effective_key}",
        "Content-Type": "application/json",
    }
    app_log.info("[%s] 开始生成 | model=%s prompt=%.60s... count=%d", tid, model, prompt, count)

    payload, ref_info = await _build_payload(
        prompt, model, aspect_ratio, image_size,
        images, labels, tid,
    )
    orig_prompt = payload["prompt"]

    # 客户端断开标志 (线程间共享)
    cancelled = threading.Event()

    # 生成 count 张图 (并行)
    import concurrent.futures

    # ====== 批次预检: 整个批次只做一次 HEAD, 不消耗 API 额度 ======
    selected_base = preflight_check(api_key=api_key, timeout=3, api_base=host)
    if not selected_base:
        api_log.warning("[%s] 预检失败: API 不可达", tid)
        return JSONResponse({"error": "API 无法连接，请检查网络或切换 Host 地址"}, status_code=503)

    def _call_api_once(idx: int):
        """单次 API 调用: 只发一次 POST, 不重试 (省钱)"""
        if cancelled.is_set():
            return {"idx": idx, "error": "客户端已取消"}

        t0 = time.perf_counter()
        try:
            api_log.info("[%s] >>> grsai %s (%d/%d) | model=%s", tid, endpoint, idx + 1, count, model)
            r = api_request("POST", endpoint, api_key=api_key, api_base=selected_base,
                            json=payload, timeout=120)
            r.raise_for_status()
            data = _parse_api_response(r)
        except Exception as e:
            api_log.error("[%s] <<< grsai FAIL (%.2fs) | %s", tid, time.perf_counter() - t0, e)
            return {"idx": idx, "error": f"提交失败: {e}"}

        status = _extract_status(data)
        image_url = _extract_image_url(data)
        task_id = _extract_task_id(data)

        if image_url:
            api_log.info("[%s] <<< grsai OK (%.2fs) | url=%.60s...", tid, time.perf_counter() - t0, image_url)
            return {"idx": idx, "url": image_url}
        elif status in ("failed", "error"):
            reason = (data.get("failure_reason") or data.get("error") or "?") if isinstance(data, dict) else "?"
            return {"idx": idx, "error": reason}
        elif task_id:
            # 需要轮询
            api_log.info("[%s] 第%d张需要轮询 | task_id=%s", tid, idx + 1, task_id)
            poll_url = _poll_single_sync(task_id, headers, model, tid, cancelled, selected_base)
            if poll_url:
                return {"idx": idx, "url": poll_url}
            else:
                return {"idx": idx, "error": "客户端取消" if cancelled.is_set() else "轮询超时"}
        else:
            return {"idx": idx, "error": f"未识别的响应 (status={status})"}

    # 提取参考图 URL，用于存历史
    ref_urls_for_history = list(payload.get("urls") or [])

    # 后台检测客户端断开
    async def _watch_disconnect():
        while True:
            if await request.is_disconnected():
                cancelled.set()
                app_log.info("[%s] 客户端断开，取消 %d 个请求", tid, count)
                break
            await asyncio.sleep(0.5)

    import asyncio
    watch_task = asyncio.create_task(_watch_disconnect())

    # 并行执行所有请求
    with concurrent.futures.ThreadPoolExecutor(max_workers=count) as executor:
        futures = [executor.submit(_call_api_once, i) for i in range(count)]
        results_list = [f.result() for f in concurrent.futures.as_completed(futures)]

    watch_task.cancel()

    # 按原始顺序整理结果；同批图片共享该批次的总耗时。
    results_list.sort(key=lambda r: r["idx"])
    total = time.perf_counter() - t_total
    image_urls = []
    record_ids = []
    errors = []
    for r in results_list:
        if r.get("url"):
            image_urls.append(r["url"])
            hid = _add_history(
                orig_prompt, model, r["url"], ref_urls_for_history, count,
                duration_seconds=total, host=selected_base,
            )
            record_ids.append(hid)
            app_log.info("[%s] 第%d张成功 | url=%.80s...", tid, r["idx"] + 1, r["url"])
        else:
            errors.append(f"第{r['idx']+1}张: {r.get('error', '?')}")

    app_log.info("[%s] ====== 完成 | 成功%d/失败%d (总%.2fs) ======",
                 tid, len(image_urls), len(errors), total)

    if image_urls:
        result = {"image_urls": image_urls, "image_url": image_urls[0], "ids": record_ids}
        if errors:
            result["warnings"] = errors
        return result
    elif errors:
        return JSONResponse({"error": "; ".join(errors)}, status_code=500)

    return JSONResponse({"error": "生成失败，未获取到任何图片"}, status_code=500)


@app.post("/generate-stream")
async def generate_stream(
    prompt: str = Form(...),
    model: str = Form("nano-banana-2"),
    images: list[UploadFile] = File(default=[]),
    labels: str = Form("[]"),
    aspect_ratio: str = Form("auto"),
    image_size: str = Form(""),
    count: int = Form(1),
    api_key: str = Form(""),
    host: str = Form(""),
    request: Request = None,
):
    """SSE 流式生成: 出一张推一张"""
    from fastapi.responses import StreamingResponse
    import concurrent.futures

    tid = trace_id()
    t_total = time.perf_counter()
    count = max(1, min(count, 4))
    effective_key = api_key.strip() or GRSAI_API_KEY
    if not effective_key:
        return JSONResponse({"error": "没读到 GRSAI_API_KEY"}, status_code=500)

    endpoint = get_endpoint(model)
    headers = {
        "Authorization": f"Bearer {effective_key}",
        "Content-Type": "application/json",
    }
    app_log.info("[%s] SSE流式生成 | model=%s count=%d", tid, model, count)
    payload, ref_info = await _build_payload(
        prompt, model, aspect_ratio, image_size,
        images, labels, tid,
    )

    cancelled = threading.Event()

    async def _watch_disconnect():
        while True:
            if await request.is_disconnected():
                cancelled.set()
                app_log.info("[%s] SSE 客户端断开", tid)
                break
            await asyncio.sleep(0.5)

    watch_task = asyncio.create_task(_watch_disconnect())

    # ====== 批次预检: 整个批次只做一次 HEAD, 不消耗 API 额度 ======
    selected_base = preflight_check(api_key=effective_key, timeout=3, api_base=host)
    if not selected_base:
        api_log.warning("[%s] SSE 预检失败: API 不可达", tid)
        return JSONResponse({"error": "API 无法连接，请检查网络或切换 Host 地址"}, status_code=503)

    def _call_one(idx: int):
        if cancelled.is_set():
            return {"idx": idx, "error": "客户端已取消"}

        t0 = time.perf_counter()
        try:
            api_log.info("[%s] SSE grsai %s (%d/%d) | model=%s", tid, endpoint, idx + 1, count, model)
            r = api_request("POST", endpoint, api_key=effective_key, api_base=selected_base,
                            json=payload, timeout=120)
            r.raise_for_status()
            data = _parse_api_response(r)
        except Exception as e:
            api_log.error("[%s] SSE FAIL #%d | %s (%.2fs)", tid, idx + 1, e, time.perf_counter() - t0)
            return {"idx": idx, "error": str(e)}

        status = _extract_status(data)
        url = _extract_image_url(data)
        task_id = _extract_task_id(data)

        if url:
            api_log.info("[%s] SSE OK #%d | url=%.60s... (%.2fs)", tid, idx + 1, url, time.perf_counter() - t0)
            return {"idx": idx, "url": url}
        elif task_id:
            poll_url = _poll_single_sync(task_id, headers, model, tid, cancelled, selected_base)
            if poll_url:
                return {"idx": idx, "url": poll_url}
        return {"idx": idx, "error": data.get("failure_reason") or data.get("error") or "生成失败"}

    def event_generator():
        """逐条产出 SSE 事件"""
        refs = list(payload.get("urls") or [])
        with concurrent.futures.ThreadPoolExecutor(max_workers=count) as executor:
            futures = {executor.submit(_call_one, i): i for i in range(count)}
            for future in concurrent.futures.as_completed(futures):
                if cancelled.is_set():
                    break
                r = future.result()
                if r.get("url"):
                    hid = _add_history(
                        prompt, model, r["url"], refs, count,
                        duration_seconds=time.perf_counter() - t_total,
                        host=selected_base,
                    )
                    yield f"data: {json.dumps({'idx': r['idx'], 'url': r['url'], 'id': hid}, ensure_ascii=False)}\n\n"
                else:
                    yield f"data: {json.dumps({'idx': r['idx'], 'error': r.get('error', '?')}, ensure_ascii=False)}\n\n"
        yield "data: [DONE]\n\n"

    return StreamingResponse(
        event_generator(),
        media_type="text/event-stream",
        headers={"X-Accel-Buffering": "no", "Cache-Control": "no-cache"},
    )


async def _poll_single(task_id: str, headers: dict, model: str, tid: str) -> str | None:
    """轮询单个任务直到完成, 返回图片 URL 或 None"""
    for i in range(60):
        time.sleep(3)
        try:
            rr = api_request("POST", "/v1/draw/result",
                               headers=headers, json={"id": task_id}, timeout=30)
            rr.raise_for_status()
            result = _parse_api_response(rr)
        except Exception as e:
            # 连接层错误: 轮询无意义, 提前退出
            if _is_connection_error(e):
                api_log.warning("[%s] 轮询中断: API 不可达 (%s)", tid, type(e).__name__)
                return None
            continue
        info = result.get("data", result)
        status = info.get("status", "")
        if status in ("succeeded", "success", "completed"):
            url = _extract_image_url(info)
            if url:
                app_log.info("[%s] 轮询成功 | task_id=%s url=%.60s...", tid, task_id, url)
                return url
        if status in ("failed", "error"):
            app_log.info("[%s] 轮询失败 | task_id=%s reason=%s", tid, task_id,
                         info.get("failure_reason") or info.get("error"))
            return None
    return None


def _poll_single_sync(task_id: str, headers: dict, model: str, tid: str,
                      cancelled: threading.Event | None = None, api_base: str = "") -> str | None:
    """同步版轮询 (供线程池使用), 支持取消信号"""
    for _ in range(60):
        if cancelled and cancelled.is_set():
            api_log.info("[%s] 轮询取消 | task_id=%s", tid, task_id)
            return None
        time.sleep(3)
        if cancelled and cancelled.is_set():
            return None
        try:
            rr = api_request("POST", "/v1/draw/result", api_base=api_base,
                               headers=headers, json={"id": task_id}, timeout=30)
            rr.raise_for_status()
            result = _parse_api_response(rr)
        except Exception as e:
            # 连接层错误: 轮询无意义, 提前退出
            if _is_connection_error(e):
                api_log.warning("[%s] 轮询中断: API 不可达 (%s)", tid, type(e).__name__)
                return None
            continue
        info = result.get("data", result)
        status = info.get("status", "")
        if status in ("succeeded", "success", "completed"):
            url = _extract_image_url(info)
            if url:
                return url
        if status in ("failed", "error"):
            return None
    return None


# ---------- 辅助函数 ----------
def _parse_api_response(response: requests.Response) -> dict:
    """解析 API 响应, 处理 SSE 格式。返回最后一个有效的 JSON 对象"""
    text = response.text.strip()

    lines = text.split("\n")
    last_json = None
    for line in lines:
        line = line.strip()
        if line.startswith("data: "):
            line = line[6:]
        if line.startswith("{"):
            try:
                last_json = json.loads(line)
            except json.JSONDecodeError:
                continue

    if last_json is not None:
        return last_json

    if text.startswith("data: "):
        text = text[6:]
    return json.loads(text)


def _extract_status(info) -> str:
    """兼容顶层或 data/result 内嵌的任务状态。"""
    if isinstance(info, dict):
        for key in ("status", "state", "task_status", "taskStatus"):
            value = info.get(key)
            if isinstance(value, str) and value:
                return value.lower()
        for key in ("data", "result", "output"):
            value = info.get(key)
            status = _extract_status(value)
            if status:
                return status
    elif isinstance(info, list):
        for item in info:
            status = _extract_status(item)
            if status:
                return status
    return ""


def _extract_task_id(info) -> str | None:
    """兼容 id/task_id 位于顶层或 data/result 内部。"""
    if isinstance(info, dict):
        for key in ("task_id", "taskId", "id"):
            value = info.get(key)
            if isinstance(value, (str, int)) and str(value):
                return str(value)
        for key in ("data", "result", "output"):
            task_id = _extract_task_id(info.get(key))
            if task_id:
                return task_id
    elif isinstance(info, list):
        for item in info:
            task_id = _extract_task_id(item)
            if task_id:
                return task_id
    return None


def _extract_image_url(info) -> str | None:
    """递归搜索整个 JSON, 找到图片 URL"""
    if isinstance(info, dict):
        for key in ("url", "imageUrl", "image_url", "src", "link"):
            val = info.get(key)
            if isinstance(val, str) and _looks_like_image_url(val):
                return val

        for key in ("results", "urls", "imageUrls", "images", "outputs", "data"):
            arr = info.get(key)
            if isinstance(arr, list) and arr:
                first = arr[0]
                if isinstance(first, dict):
                    url = first.get("url") or first.get("imageUrl") or first.get("src")
                    if isinstance(url, str) and _looks_like_image_url(url):
                        return url
                elif isinstance(first, str) and _looks_like_image_url(first):
                    return first

        for val in info.values():
            result = _extract_image_url(val)
            if result:
                return result

    elif isinstance(info, list):
        for item in info:
            result = _extract_image_url(item)
            if result:
                return result

    return None


def _looks_like_image_url(s: str) -> bool:
    if not isinstance(s, str):
        return False
    if s.startswith("http") and not s.startswith("https://api."):
        return True
    if s.startswith("https://") and any(
        ext in s.lower() for ext in (".png", ".jpg", ".jpeg", ".webp", ".gif", "image")
    ):
        return True
    return False


def _compress_ref_image(raw: bytes, content_type: str) -> bytes:
    """压缩参考图: 最长边 ≤ 4096px, 保持原格式, PNG 保留透明度"""
    MAX_DIM = 4096
    try:
        from PIL import Image
        from io import BytesIO
        img = Image.open(BytesIO(raw))
        w, h = img.size
        if max(w, h) > MAX_DIM:
            ratio = MAX_DIM / max(w, h)
            img = img.resize((int(w * ratio), int(h * ratio)), Image.LANCZOS)

        # 判断是否有透明度
        has_alpha = img.mode in ("RGBA", "PA", "LA")
        is_palette_transparent = img.mode == "P" and "transparency" in img.info

        buf = BytesIO()
        if has_alpha or is_palette_transparent:
            # 有透明通道 → 用 PNG
            if img.mode == "P":
                img = img.convert("RGBA")
            img.save(buf, format="PNG", optimize=True)
        elif img.format == "PNG":
            # PNG 无透明 → 看大小, 太大就转 JPEG
            if len(raw) > 1024 * 1024:
                img = img.convert("RGB")
                img.save(buf, format="JPEG", quality=92, optimize=True)
            else:
                img.save(buf, format="PNG", optimize=True)
        else:
            # 其他格式 → JPEG
            if img.mode in ("RGBA", "P"):
                img = img.convert("RGB")
            img.save(buf, format="JPEG", quality=92, optimize=True)
        return buf.getvalue()
    except Exception:
        return raw


def _save_ref_unique(ref_data_url: str) -> str | None:
    """保存单张参考图到 refs/，使用内容 MD5 去重。返回文件名或 None。"""
    try:
        header, b64 = ref_data_url.split(",", 1)
        ext = header.split("/")[1].split(";")[0] if "/" in header else "png"
        raw = base64.b64decode(b64)
        h = hashlib.md5(raw).hexdigest()[:16]
        filename = f"{h}.{ext}"
        filepath = REFS_DIR / filename
        if not filepath.exists():
            filepath.write_bytes(raw)
        return filename
    except Exception:
        return None


def _add_history(
    prompt: str,
    model: str,
    image_url: str,
    ref_urls: list[str] | None = None,
    count: int = 1,
    duration_seconds: float | None = None,
    host: str = "",
) -> str:
    """保存历史记录, 返回记录 ID"""
    if not image_url:
        return ""
    hid = uuid.uuid4().hex[:12]
    local_filename = ""

    # 下载 CDN 图片到本地 images/ 目录, 防止 CDN 过期
    try:
        r = requests.get(image_url, timeout=30)
        if r.status_code == 200 and len(r.content) > 100:
            ct = r.headers.get("content-type", "image/png")
            ext = ct.split("/")[-1].split(";")[0] or "png"
            local_filename = f"{hid}.{ext}"
            (IMAGES_DIR / local_filename).write_bytes(r.content)
            app_log.info("图片已缓存到本地 %s (%d bytes)", local_filename, len(r.content))
    except Exception as e:
        app_log.warning("缓存图片失败 %s: %s", image_url[:60], e)

    # 保存参考图到 refs/ 目录 (内容哈希去重)
    ref_hashes = []
    if ref_urls:
        for ref_url in ref_urls:
            if ref_url.startswith("data:"):
                fname = _save_ref_unique(ref_url)
                if fname:
                    ref_hashes.append(fname)

    records = load_history()
    records.append({
        "id": hid,
        "prompt": prompt,
        "model": model,
        "image_url": f"/images/{local_filename}" if local_filename else image_url,
        "source_url": image_url,  # 保留原始 CDN URL 备查
        "created_at": time.strftime("%Y-%m-%d %H:%M:%S"),
        "ref_hashes": ref_hashes,  # 参考图哈希列表 (兼容前端 has_ref 展示)
        "has_ref": bool(ref_hashes),
        "count": count,
        "duration_seconds": round(duration_seconds, 1) if duration_seconds is not None else None,
        "host": host.replace("https://", ""),
        "status": "success",
    })
    save_history(records)
    return hid


@app.get("/logs")
def get_logs(lines: int = 80):
    """返回最近 N 行日志, 合并 app.log 和 api.log"""
    entries = []
    for log_file, tag in [(LOG_DIR / "app.log", "APP"), (LOG_DIR / "api.log", "API")]:
        if not log_file.exists():
            continue
        try:
            text = log_file.read_text(encoding="utf-8")
            log_lines = text.strip().split("\n")
            for line in log_lines[-lines:]:
                if line.strip():
                    entries.append({"tag": tag, "text": line})
        except OSError:
            pass

    # 按时间排序, 取最后 lines 条
    entries.sort(key=lambda e: e["text"])
    return JSONResponse(entries[-lines:])


@app.post("/enhance")
async def enhance_prompt(
    prompt: str = Form(...),
    model: str = Form("nano-banana-2"),
    api_key: str = Form(""),
    host: str = Form(""),
):
    """使用 AI 增强提示词"""
    effective_key = api_key.strip() or GRSAI_API_KEY
    if not effective_key:
        return JSONResponse({"error": "未配置 API Key"}, status_code=401)

    # 构建增强请求
    system_prompt = (
        "You are a prompt engineer for AI image generation. "
        "Expand the user's short prompt into a rich, detailed image description. "
        "Include: subject details, facial features, lighting, colors, composition, atmosphere, style. "
        "Keep it under 200 words. Output ONLY the enhanced prompt, no explanations."
    )
    payload = {
        "model": model,
        "prompt": prompt,
        "enhanceOnly": True,
        "messages": [
            {"role": "system", "content": system_prompt},
            {"role": "user", "content": f"Enhance this prompt: {prompt}"}
        ],
    }
    effective_key = api_key.strip() or GRSAI_API_KEY
    endpoint = get_endpoint(model)

    # 预检连通性 (不消耗额度)
    selected_base = preflight_check(api_key=effective_key, timeout=5, api_base=host)
    if not selected_base:
        api_log.warning("[%s] 增强提示词预检失败: API 不可达", tid)
        return {"enhanced": prompt}  # 兜底返回原提示词

    try:
        r = api_request("POST", endpoint, api_key=effective_key, api_base=selected_base,
                        json=payload, timeout=30)
        if r.status_code == 200:
            data = _parse_api_response(r)
            enhanced = data.get("enhanced_prompt") or data.get("prompt") or prompt
            return {"enhanced": enhanced}
        # 如果 enhance 接口不支持，用 nano-banana 生成描述
        r2 = api_request("POST", "/v1/draw/nano-banana", api_key=effective_key,
                         api_base=selected_base, json={
            "model": "nano-banana-2",
            "prompt": f"Generate a detailed image description for: {prompt}. Output only the description.",
            "enhanceOnly": True,
        }, timeout=30)
        if r2.status_code == 200:
            data2 = _parse_api_response(r2)
            enhanced = data2.get("enhanced_prompt") or data2.get("text") or prompt
            return {"enhanced": enhanced}
    except Exception:
        pass
    # 兜底：简单扩展
    return {"enhanced": f"{prompt}, high quality, detailed, professional lighting, 8K"}


@app.post("/clipboard")
async def read_clipboard():
    """读取 Windows 剪贴板中的图片 (支持 PS 大图 CF_DIB 格式)"""
    import struct

    # 先试 PIL ImageGrab (简单场景)
    try:
        from PIL import ImageGrab
        img = ImageGrab.grabclipboard()
        if img is not None:
            raw = io.BytesIO()
            img.save(raw, format="PNG")
            compressed = _compress_ref_image(raw.getvalue(), "image/png")
            b64 = base64.b64encode(compressed).decode()
            mime = "image/jpeg" if len(compressed) != raw.tell() else "image/png"
            return {"data_url": f"data:{mime};base64,{b64}"}
    except ImportError:
        pass

    # 再试 win32clipboard 直接读 CF_DIB (PS 大图)
    try:
        import win32clipboard
        from PIL import Image

        win32clipboard.OpenClipboard()
        try:
            for fmt_id in (8, 17, 2):  # CF_DIB, CF_DIBV5, CF_BITMAP
                if win32clipboard.IsClipboardFormatAvailable(fmt_id):
                    dib = bytes(win32clipboard.GetClipboardData(fmt_id))
                    header_size = struct.unpack_from("<I", dib, 0)[0]
                    bmp_header = struct.pack(
                        "<2sIHHI", b"BM", 14 + len(dib), 0, 0, 14 + header_size
                    )
                    img = Image.open(io.BytesIO(bmp_header + dib))
                    raw = io.BytesIO()
                    img.save(raw, format="PNG")
                    compressed = _compress_ref_image(raw.getvalue(), "image/png")
                    b64 = base64.b64encode(compressed).decode()
                    mime = "image/jpeg" if len(compressed) != raw.tell() else "image/png"
                    return {"data_url": f"data:{mime};base64,{b64}"}
        finally:
            win32clipboard.CloseClipboard()
    except Exception:
        pass

    return JSONResponse({"error": "剪贴板中没有图片"}, status_code=404)
