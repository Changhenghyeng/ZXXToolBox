"""Windows portable launcher for AI Image Studio."""

from __future__ import annotations

import ctypes
import os
import socket
import subprocess
import threading
import time
import urllib.request
import webbrowser
import winreg
from pathlib import Path

import uvicorn

from main import DATA_ROOT, app


HOST = "127.0.0.1"
PORT = int(os.environ.get("AI_IMAGE_TOOL_PORT", "8000"))
URL = f"http://{HOST}:{PORT}/"


def _message(title: str, text: str, error: bool = False) -> None:
    flags = 0x10 if error else 0x40
    ctypes.windll.user32.MessageBoxW(None, text, title, flags)


def _port_is_open() -> bool:
    try:
        with socket.create_connection((HOST, PORT), timeout=0.4):
            return True
    except OSError:
        return False


def _is_image_studio() -> bool:
    try:
        with urllib.request.urlopen(URL, timeout=1.5) as response:
            html = response.read(32768).decode("utf-8", errors="ignore")
        return "<title>我的 AI 出图工具</title>" in html or "Image Studio" in html
    except Exception:
        return False


def _find_chrome() -> str | None:
    registry_paths = (
        (winreg.HKEY_CURRENT_USER, r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
        (winreg.HKEY_LOCAL_MACHINE, r"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe"),
    )
    for hive, key_path in registry_paths:
        try:
            with winreg.OpenKey(hive, key_path) as key:
                value, _ = winreg.QueryValueEx(key, None)
            if value and Path(value).is_file():
                return value
        except OSError:
            pass

    candidates = (
        Path(os.environ.get("PROGRAMFILES", "")) / "Google/Chrome/Application/chrome.exe",
        Path(os.environ.get("PROGRAMFILES(X86)", "")) / "Google/Chrome/Application/chrome.exe",
        Path(os.environ.get("LOCALAPPDATA", "")) / "Google/Chrome/Application/chrome.exe",
    )
    return next((str(path) for path in candidates if path.is_file()), None)


def _launch_app_window() -> subprocess.Popen | None:
    chrome = _find_chrome()
    if not chrome:
        return None
    profile = DATA_ROOT / "chrome-profile"
    profile.mkdir(parents=True, exist_ok=True)
    return subprocess.Popen(
        [
            chrome,
            f"--app={URL}",
            f"--user-data-dir={profile}",
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-background-mode",
            "--disable-session-crashed-bubble",
            "--disable-features=Translate",
        ],
        creationflags=subprocess.CREATE_NO_WINDOW,
    )


def main() -> int:
    if _port_is_open():
        if _is_image_studio():
            if os.environ.get("AI_IMAGE_TOOL_NO_BROWSER") != "1":
                if _launch_app_window() is None:
                    webbrowser.open(URL)
            return 0
        _message("AI 图片工作室", f"端口 {PORT} 已被其它程序占用。\n请关闭占用程序后再启动。", True)
        return 1

    config = uvicorn.Config(
        app,
        host=HOST,
        port=PORT,
        log_level="warning",
        access_log=False,
        log_config=None,
    )
    server = uvicorn.Server(config)
    server_thread = threading.Thread(target=server.run, name="image-studio-server", daemon=True)
    server_thread.start()

    started_at = time.monotonic()
    while server_thread.is_alive() and not server.started:
        time.sleep(0.15)
        if time.monotonic() - started_at > 60:
            started_at = time.monotonic()

    if not server.started:
        _message("AI 图片工作室", "本地服务启动失败，请查看数据目录中的 logs 日志。", True)
        return 1

    try:
        if os.environ.get("AI_IMAGE_TOOL_TEST_MODE") == "1":
            while True:
                time.sleep(0.5)
        else:
            app_window = None
            if os.environ.get("AI_IMAGE_TOOL_NO_BROWSER") != "1":
                app_window = _launch_app_window()
            if app_window is not None:
                app_window.wait()
            elif os.environ.get("AI_IMAGE_TOOL_NO_BROWSER") != "1":
                webbrowser.open(URL)
                _message(
                    "AI 图片工作室",
                    "未检测到 Google Chrome，已使用默认浏览器打开。\n\n关闭此提示将停止本地服务。",
                    True,
                )
            else:
                while True:
                    time.sleep(0.5)
    except (EOFError, KeyboardInterrupt):
        pass
    finally:
        server.should_exit = True
        server_thread.join(timeout=8)

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
