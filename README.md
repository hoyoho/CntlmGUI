# CntlmGUI

CntlmGUI 是 [cntlm](https://cntlm.sourceforge.net/) 的 Windows 图形前端。cntlm 能为只支持 NTLM/NTLMv2 认证的上游代理做本地中转，但配置靠手写 `cntlm.ini`、起停靠命令行。CntlmGUI 把这些操作变成填表单、点按钮，并补上状态监控、日志、代理测速和托盘常驻。

- 语言 / 框架：C# / .NET 8 / Windows Forms（无第三方依赖）
- 系统要求：Windows 10/11 x64 + [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- 当前版本：1.2.4

## 工作原理

```
应用 / 局域网客户端
      ↓
【一级代理】本机 cntlm 监听端口（默认 127.0.0.1:8080）
      ↓
【二级代理】cntlm → 上游 parent proxy（NTLM 认证）→ 外网
```

默认只监听 `127.0.0.1:8080`，不对局域网开放。关闭"启用对外互联"后，一级代理的监听、访问控制、SOCKS5、Tunnel 仍然可用。

## 功能

- **配置生成**：界面参数实时生成 `cntlm.ini`，主界面可直接预览
- **启停管理**：子进程模式（`cntlm -f -c ...`）或 Windows 服务模式（`sc start/stop`）
- **状态监控**：定时探测监听端口，显示运行状态、监听地址与 PID
- **日志**：分屏显示 GUI 操作日志与 cntlm 输出；可选 `-T` trace 日志；超长自动裁剪
- **上游代理**：多行代理列表、PAC、SOCKS5（含认证）、免代地址、Header 替换、ISA 扫描器
- **上游认证**：用户名 / 域 / 工作站、`PassLM` / `PassNT` / `PassNTLMv2`、认证类型与 Flags；支持调用 cntlm 自动生成哈希，也支持 Windows 集成认证（SSPI）
- **一级代理**：监听地址与端口、Gateway、Allow / Deny 访问控制、Tunnel 端口转发
- **代理测速**：对候选上游做 TCP 延迟与整链可用性测试（临时拉起独立 cntlm 实例），可选限时限量的吞吐测试，结果排序后可直接用于配置
- **配置迁移**：首次运行自动导入本机已安装的 `cntlm.ini`，也可手动导入
- **安全**：密码经 Windows DPAPI 加密保存，不以明文落盘；生成哈希时密码通过 stdin 传递，不出现在命令行
- **托盘**：关闭窗口最小化到托盘，托盘菜单可直接启停

## 编译

需要 .NET SDK 8.0 或更高版本。

```powershell
dotnet build .\src\CntlmGui.csproj -c Release
```

发布为独立目录（依赖框架，与安装脚本一致）：

```powershell
dotnet publish .\src\CntlmGui.csproj -c Release -r win-x64 --self-contained false -o .\build\publish
```

或发布为不依赖 .NET 运行时的单文件：

```powershell
dotnet publish .\src\CntlmGui.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -o .\build\single
```

### 打包 MSI

需要 WiX Toolset 3.14（`candle.exe` / `light.exe`）。脚本会依次检查环境变量 `WIXTOOLS`、`WIX`、`%LOCALAPPDATA%\Tauri\WixTools314` 以及 `C:\Program Files (x86)\WiX Toolset v3.14`（自动识别其中的 `bin` 目录）。

```powershell
.\installer\build.ps1
```

产物为 `build\CntlmGui.msi`，包含程序本体、cntlm 安装包与卸载脚本，并创建桌面和开始菜单快捷方式。卸载时默认保留 cntlm，需要一并卸载时：

```powershell
msiexec /x build\CntlmGui.msi REMOVECNTLM=1 /qn
```

## 使用

1. 启动程序，打开 **设置**。
2. **二级代理**：启用"对外互联"，填写上游代理（`host:port`，每行一条）；可先点"代理列表编辑…"测速排序。
3. 配置上游认证，三种方式任选：
   - 使用当前 Windows 登录身份：勾选"使用 Windows 集成认证（SSPI）"，无需密码；
   - 推荐：填写用户名 / 域 / 密码后点 **生成哈希**，由 cntlm 计算并回填哈希；
   - 直接填写已有的 `PassLM` / `PassNT` / `PassNTLMv2`。
4. **一级代理**：设置监听地址与端口。需要共享给局域网时勾选 Gateway，并同时配置 Allow / Deny。
5. **运行**：确认 `cntlm.exe` 路径、配置文件输出路径、运行模式，以及是否自动启动、是否关闭到托盘。
6. 保存后回到主界面点 **启动**，状态变为"运行中"即可使用。
7. 把系统或应用的 HTTP/HTTPS 代理指向 `127.0.0.1:8080`。

程序会自行探测 cntlm 的安装位置：

- `C:\Program Files\Cntlm\cntlm.exe`
- `C:\Program Files (x86)\Cntlm\cntlm.exe`
- PATH 中 `where cntlm.exe` 能找到的路径

也可以手动指定。若安装包安装时一并安装了 cntlm，通常无需额外配置。

## 文件位置

| 用途 | 路径 |
| --- | --- |
| GUI 设置（密码为 DPAPI 密文） | `%APPDATA%\CntlmGUI\settings.json` |
| 生成的 cntlm 配置（可在设置中修改） | `%APPDATA%\CntlmGUI\cntlm.ini` |
| `-T` 详细日志 | `%APPDATA%\CntlmGUI\cntlm-trace.log` |
| 测速临时文件（自动清理） | `%APPDATA%\CntlmGUI\bench\` |

服务模式下，GUI 会写入服务实际读取的 ini（从注册表解析 `-c` 参数），通常需要以管理员身份运行。

## 常见问题

**提示"未找到 cntlm.exe"**
在 设置 → 运行 → 可执行文件 中点"检测"，或手动选择路径。

**服务模式启动失败，或修改配置不生效**
以管理员身份运行程序，并确认服务名正确（默认 `cntlm`）。设置页会显示解析到的服务配置路径。

**端口被占用**
日志会给出占用端口的 PID，并标明是否为 cntlm 进程。程序只会结束确认为 cntlm 的进程，其他占用需要自行处理或更换监听端口。

**看不到 cntlm 的详细输出**
默认不开启 `-T`，避免日志文件持续增长。排查问题时在 设置 → 运行 中临时勾选"记录详细日志"。服务模式不采集 trace 日志。

**密码怎么存的？**
只以 DPAPI 密文写入 `settings.json`，仅当前 Windows 用户可解密。如果选择回退到明文 `Password`，明文会出现在生成的 `cntlm.ini` 中，建议始终使用哈希或 SSPI。
