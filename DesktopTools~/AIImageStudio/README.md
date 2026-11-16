# AI 图片工作室

保留两个独立入口：`server/` 是原网页服务，`desktop/` 是桌面版源码。不要混用两个目录中的 main.py 与 index.html。

## 环境与启动

Windows、Python 3.11+；桌面启动器需要 Google Chrome。在本目录运行：

```powershell
python -m venv .venv
.venv\Scripts\python -m pip install -r requirements.txt
cd desktop
..\.venv\Scripts\python launcher.py
```

使用网页版本时，回到本目录后：

```powershell
cd server
..\.venv\Scripts\python -m uvicorn main:app --host 127.0.0.1 --port 8000
```

浏览器打开 http://127.0.0.1:8000 。两个版本默认使用同一端口，不要同时启动。API Key 在自己的网页设置中输入，或通过 GRSAI_API_KEY 环境变量配置；本仓库没有提供密钥。实际生成使用第三方服务并可能计费；此次同步未调用生图或付费测试接口。

## 桌面打包

在 desktop 目录执行：

```powershell
..\.venv\Scripts\python -m PyInstaller --noconfirm AI图片工作室_v1.1.6.spec
```

输出在 desktop/dist。保留原始 v1.1.6 打包配置，但源码 APP_VERSION 仍为 1.1.5，故不能以 EXE 文件名宣称源码内版本已一致。models.json 是随源码附带的模型目录/价格快照，不保证在线供应商现状。

程序运行会创建本地图片、历史和日志；打包程序在旁边建立 AI-Image-Tool-Data。不要提交这些数据。依赖表依据源码导入补充，本次只做语法和路径检查，未重装依赖或验证在线接口。
