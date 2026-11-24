# CntlmGUI

CntlmGUI 是 [cntlm](https://cntlm.sourceforge.net/)（NTLM/NTLMv2 代理认证工具）的 Windows 图形前端。GUI 维护自己的配置，启动/停止时动态生成 `cntlm.ini`，并以子进程或 Windows 服务方式驱动 `cntlm.exe`。

- 技术栈：C# / .NET 8 / Windows Forms（无 NuGet 依赖）
- 系统要求：Windows 10/11 x64，.NET 8 Desktop Runtime
- 版本：1.2.4

## 产品模型

```
应用 / 局域网客户端
      ↓
【一级代理】本地互联：本机 cntlm 的监听端
      ↓
【二级代理】对外互联：本机 cntlm → 上游 parent proxy → 外网
```

- 默认只监听 `127.0.0.1:8080`，不会默认对局域网开放。
- 关闭「启用对外互联」只影响二级代理；一级的监听、访问控制、SOCKS5、Tunnel 仍然有效。

## 目录结构

```
src/                      应用源码
  CntlmGui.csproj
  Program.cs              单实例入口
  AppTheme.cs             主题（字体/颜色/按钮）
  MainForm.cs             主窗口（状态、双日志、托盘）
  SettingsForm.cs         设置窗口（一级/二级/运行）
  ProxySpeedForm.cs       代理列表与测速窗口
  ProxyResultList.cs      代理列表控件
  ToggleSwitch.cs         开关控件
  TextLines.cs            多行文本工具
  App.ico
  Models/
    GuiSettings.cs        配置模型与持久化
    ProxyBench.cs         测速结果
  Services/
    CntlmConfigGenerator.cs  ini 生成与导入
    CntlmController.cs       进程/服务启动停止与状态
    CntlmService.cs          服务实际 ini 路径解析
    Detect.cs                查找 cntlm.exe / cntlm.ini
    ProxySpeedTest.cs        代理测速
    SecretProtector.cs       DPAPI 密码保护
installer/
  CntlmGui.wxs            WiX 3 安装包定义
  en-us.wxl               本地化（en-US，代码页 936，支持中文条目）
  build.ps1               打包脚本
  uninstall-cntlm.ps1     卸载时询问是否同时卸载 cntlm
vendor/
  cntlm-0.94.0-win64.exe  cntlm 官方 Windows 安装包
docs/ISSUES.md
README.md
```

## 构建与运行

```powershell
dotnet build src\CntlmGui.csproj -c Release
dotnet run --project src\CntlmGui.csproj -c Release
```

输出：`src\bin\Release\net8.0-windows\CntlmGUI.exe`。

首次运行会生成 `%APPDATA%\CntlmGUI\settings.json`；若本机存在 `C:\Program Files (x86)\Cntlm\cntlm.ini` 或 `C:\Program Files\Cntlm\cntlm.ini`，会尝试导入初始参数。

## 生成安装包

需要 WiX Toolset 3.14（`candle.exe` / `light.exe`）。脚本按以下顺序查找：

1. 环境变量 `WIXTOOLS`
2. `%LOCALAPPDATA%\Tauri\WixTools314`
3. `C:\Program Files (x86)\WiX Toolset v3.14`
4. `C:\Program Files\WiX Toolset v3.14`

```powershell
powershell -ExecutionPolicy Bypass -File installer\build.ps1
```

输出：`build\CntlmGui.msi`。

安装包内容：CntlmGUI 程序文件、`cntlm-0.94.0-win64.exe`、`uninstall-cntlm.ps1`；注册表 `HKLM\Software\CntlmGui`（InstallDir、Version）；桌面与开始菜单快捷方式；完成页默认勾选运行 cntlm 安装程序。

卸载时默认保留 cntlm；如需一并卸载：

```powershell
msiexec /x build\CntlmGui.msi REMOVECNTLM=1 /qn
```

## 文件位置

| 用途 | 路径 |
| --- | --- |
| 设置 | `%APPDATA%\CntlmGUI\settings.json` |
| 生成的 ini | `%APPDATA%\CntlmGUI\cntlm.ini`（可在设置中修改） |
| trace 日志 | `%APPDATA%\CntlmGUI\cntlm-trace.log` |
| 测速临时文件 | `%APPDATA%\CntlmGUI\bench\` |

## 安全说明

- 密码只以 DPAPI（当前用户）密文形式写入 `settings.json`；明文只存在内存。
- 生成哈希时密码通过 stdin 传给 `cntlm -H`，不出现在命令行。
- 明文密码写入 ini 时会附带警告注释，建议改用哈希。
- 结束进程前先确认进程名为 `cntlm`，不会误杀占用端口的其它程序。
- 默认监听 `127.0.0.1`；改为 `0.0.0.0` 或启用 Gateway 前请同时配置访问控制。

## 已知问题

见 [docs/ISSUES.md](docs/ISSUES.md)。
