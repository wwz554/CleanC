# CleanC 1.6.6 Windows 构建步骤

## 需要安装

1. Windows 10 2004+ 或 Windows 11 x64
2. .NET 8 SDK x64
3. Inno Setup 6（已验证路径兼容当前用户安装，例如 `%LocalAppData%\Programs\Inno Setup 6\ISCC.exe`）
4. 首次构建时网络可访问 NuGet

不要求完整 Visual Studio。

## 安装命令

管理员 PowerShell：

```powershell
winget install --id Microsoft.DotNet.SDK.8 -e
winget install --id JRSoftware.InnoSetup -e -s winget --version 6.7.3 -i
```

检查：

```powershell
dotnet --version
Get-ChildItem "C:\Program Files","C:\Program Files (x86)","$env:LOCALAPPDATA" -Filter ISCC.exe -Recurse -ErrorAction SilentlyContinue | Select-Object FullName
```

## 一键构建

解压源码后，直接双击：

`build\build-all.cmd`

窗口不会自动关闭。

或者 PowerShell：

```powershell
cd "C:\你的路径\CleanC-1.6.6-source"
Set-ExecutionPolicy -Scope Process Bypass -Force
.\build\build-local.ps1
```

## 手工构建命令

```powershell
cd "C:\你的路径\CleanC-1.6.6-source"

dotnet restore .\CleanC.sln

dotnet build .\CleanC.sln -c Release -p:Platform=x64 --no-restore

dotnet publish .\src\CleanC.App\CleanC.App.csproj `
  -c Release `
  -p:Platform=x64 `
  -p:WindowsAppSDKSelfContained=true `
  -p:PublishSingleFile=false `
  -r win-x64 `
  --self-contained true `
  --no-restore `
  -o .\artifacts\publish

& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" ".\build\CleanC.iss"
```

如果 Inno Setup 安装在 Program Files，请把最后一条里的 ISCC.exe 路径换成实际位置。

## 产物

完整可运行目录：

`artifacts\publish\`

主程序：

`artifacts\publish\CleanC.exe`

正式安装包：

`artifacts\installer\CleanC-Setup.exe`

注意：WinUI 3 self-contained 发布不是“只复制一个 exe”的绿色单文件模式。测试 `CleanC.exe` 时要保留整个 `artifacts\publish` 目录；对外分发优先使用 `CleanC-Setup.exe`。

## 如果旧版已经出现“重启后打开即退出”

1.6.6 会自动恢复扫描数据库。若仍需手动清理旧扫描缓存，可运行：

```powershell
Set-ExecutionPolicy -Scope Process Bypass -Force
.\build\reset-scan-cache.ps1
```

它只处理：

`%LocalAppData%\CleanC\Data\CleanC.db*`

不会处理：

- `%ProgramData%\CleanC\License\<SID>\`
- `%ProgramData%\CleanC\Logs\<SID>\`
- `%ProgramData%\CleanC\Recovery\<SID>\`

所以不会主动清掉授权。
