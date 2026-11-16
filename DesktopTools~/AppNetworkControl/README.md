# 应用联网控制

当前源码版本 1.1.1。Windows Forms / .NET 9，面向 Windows 10/11 x64。

功能包括多应用组管理、后代关联进程扫描、按 EXE 完整路径同步防火墙限制、规则状态检查、托盘和开机自启。详细行为及版本变化见 [使用说明](源码/README.txt)。

## 构建

安装 .NET 9 SDK 后，在此目录执行：

```powershell
dotnet build 源码/AppNetworkControl.csproj -c Release
dotnet publish 源码/AppNetworkControl.csproj -c Release -r win-x64 --self-contained true -o dist
```

运行程序需要管理员权限，会按用户勾选修改 Windows Defender Firewall 的应用规则。源码归档和编译本身不启动该程序，不修改防火墙或自启配置。

附带原有 `验证/Checks.csproj`，使用模拟规则对象检查分组、序列化及规则处理，也包含界面布局检查；构建通过不等于已经验证真实网络拦截效果。
