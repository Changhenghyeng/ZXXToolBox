# 素材搜图

使用本地截图/参考图查找素材原文件，支持多个本地或共享目录、增量索引、框选检索、PSD/PSB 合成预览和数据目录迁移。搜索在本机进行。

## 运行

Windows、Python 3.11+；在本目录执行：

```powershell
python -m venv .venv
.venv\Scripts\python -m pip install -r 源码/requirements.txt
.venv\Scripts\python 源码/app.py
```

保留原始依赖版本。源码 VERSION 为 0.1.3，本机启动脚本指向 0.1.4 成品；本次归档的是指定目录里的实际源码，不反推或伪造缺失的 0.1.4 源码。附带旧 [操作说明](发布资料/使用说明.md) 标题为 0.1.2，其打包名称并非当前交付文件。

## 打包

```powershell
.venv\Scripts\python 源码/build_onefile.py
# 或生成带可复用运行缓存的单 EXE：
.venv\Scripts\python 源码/build_cached.py
```

单文件/缓存打包脚本已将旧归档根目录推导改为当前 AssetSearch 目录，输出写入此模块的 outputs 和 work，不会写到仓库外。build_cached.py 还需要 Windows .NET Framework 4 的 C# 编译器。build_portable.py 可单独生成文件夹版，输出在 程序/；分发文件夹版时应同时附带 发布资料/第三方许可。

保留第三方许可文本，不复制依赖库本体。个人索引、缩略图、原素材、数据目录定位文件不纳入 Git。附带原 selftest.py；本次未构建或启动图形应用。
