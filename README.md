# CleanC 1.6.6

## 1.6.6 扫描/智能缓存与清理收尾并行化

1.6.6 在 BuildFix2 的安全基线上增加两条流水线：扫描事务 Commit 后即时把已提交范围交给 Smart Clean 缓存消费者；清理时每个文件的 Deleted / Gone / Skipped 结果即时交给后台数据库复核消费者。扫描完成后如果缓存仍在收尾，会先显示“正在构建智能缓存”，缓存 Ready 后才自动进入清理页；安全清理也只有“文件处理 + 数据库/智能缓存收尾”都结束后才显示真正完成。详见 `FIX-1.6.6.md`。

## 1.6.0 安全与正确性加固

本版直接基于 1.3.2 数据库锁与三级退出加固版继续修改，不重做已经正常的 UI、清理分类、退出状态机、授权或数据库逻辑。重点修复扫描到删除之间的 Windows 文件身份校验、卸载残留 fresh 复核、便携程序保护、回收站 Unknown 状态、Windows Update 驱动硬件 ID 匹配、重启前驱动备份保护、ContentDialog 并发以及 DISM/SFC/CHKDSK 独立验证。任何无法证明安全或无法验证成功的情况都改为跳过/未知，不会扩大自动清理范围，也不会把未知状态显示成成功。

详细变更与微软官方依据见 `FIX-1.6.0.md`。

# CleanC 1.0.18

## 1.0.18 文件夹三态选择 + 驱动修复

智能清理文件夹复选框现在按该真实目录内全部“可选择项目”计算状态：全部选中显示绿色对号；部分选中显示带留白的实心绿色小方块；全部未选为空框。点击文件夹复选框会在“全选全部可清理文件 / 全部取消”之间切换，受保护文件永远不会被选中。展开后的单个文件继续使用绿色对号，已选文件排在最前并按大小降序。

左侧“空间分析”下面新增“驱动修复”。全面扫描读取本机 PnP 驱动与设备管理器状态，并通过 Windows Update Agent 官方驱动通道检查当前电脑可安装的驱动更新；有适用更新显示安装/升级按钮，正常驱动显示灰色不可点击“驱动正常”，异常设备在官方源没有可用包时提供已识别厂商官网入口。安装驱动前会尽量创建 Windows 系统还原点。CleanC 不使用第三方驱动包站点，也不会通过网页抓取猜测驱动是否适用于当前硬件。

## 1.0.18 推荐勾选 + 混合目录视图

智能清理改为与主流清理软件一致的“推荐选择”模型：安全项默认勾选但用户可取消；可选项和用户数据默认不选；受保护只读。所有分类按真实文件夹分组，展开文件夹时同时显示同目录中的安全、可选、用户数据和受保护项目，避免把有用文件藏在其他标签页。回收站固定在可选项第一行并默认不勾选。

安全规则同时扩大到更完整的 Temp、Windows Temp、浏览器/应用缓存和 Windows 缩略图缓存；图片、视频、文档、压缩包即使位于缓存目录也默认交给用户确认。

## 1.0.18 文件夹化清理

智能清理四个分类现在全部按文件夹分组。安全缓存自动选择；可选和用户数据展开文件夹后逐个勾选；受保护只读。回收站固定在可选项第一行，默认不清理。新增通用 AppData 缓存识别和保守的卸载残留识别；浏览器会重建的 index/data_0~data_3 改为可选。


本版在 1.0.4 的 XAML/PRI 稳定性修复和 1.0.6 的后台缓存基础上继续优化交互：导航使用透明玻璃选中层和自然渐变，页面切换用合成动画掩盖布局刷新；智能清理、空间分析、系统修复、设置页面尽量复用缓存视图。扫描状态在概览/智能清理/空间分析之间统一同步，扫描与系统检查可并行运行。

## 1.0.18 安全分类强化

`C:\Recovery`、Windows Recovery、System Volume Information、ProgramData、Boot/EFI/Windows 更新恢复目录、AppData 应用状态、凭据/云同步目录以及检测到的软件/项目目录均进入“受保护”。只有明确清理规则命中的缓存才可能进入安全/可选；升级到本版后会自动失效旧扫描缓存，需重新扫描一次得到新的分类结果。

运行窗口会显式设置新版 `CleanC.ico` 到窗口、标题栏和任务栏；安装/发布目录也会携带该 ICO。

# CleanC

Windows 10 2004 / Windows 11 x64 原生桌面清理软件，C#、.NET 8、WinUI 3。

## 运行

推荐使用桌面的 `CleanC-Setup.exe` 安装；也可运行桌面 `CleanC/CleanC.exe`，需保留完整目录。正式 EXE 按用户要求声明 `requireAdministrator`。

源码入口：`CleanC.sln`。运行说明与本版边界见 `docs/CleanC-User-Guide.txt`。

## 构建

使用 .NET SDK 8.0.425 或兼容的 .NET 8 SDK，在 Windows x64 上执行：

```powershell
dotnet run --project tests/CleanC.Tests -c Release
dotnet publish src/CleanC.App -c Release -p:Platform=x64 -r win-x64 --self-contained true -o artifacts/publish
```

`build/build.ps1` 支持通过参数指定 SDK 和 Inno Setup 编译器路径。

## 安全与授权

扫描只读，SQLite 缓存元数据。安全清理需要用户确认，删除前锁定目录链与文件句柄，复核文件 ID、卷、链接、大小、属性及时间；文件变化、占用、链接或权限不足均跳过。1.0.18 起，智能清理与空间分析手动清理均直接永久删除，不进入回收站，也不创建新的恢复副本；旧版本已经存在的恢复区内容仍可在设置中恢复。

只嵌入 `src/CleanC.Licensing/Crypto/cleanc-public.pem`。API v3 / Lease v4 的原始 signedPayload 字节采用 P-256 SHA-256 / IEEE P1363 验签，不信任外层 lease。首次激活一次请求，正常续租仅 challenge + refresh。有效 Lease 启动零请求。永久授权与离线租约时间分别处理。

签名时间加单调运行时间、DPAPI 检查点与回拨检测共同管理期限。设备 CNG 密钥优先使用 TPM，不可导出；无 TPM 时回退 CNG 软件提供程序。设备身份按 Windows 用户隔离。

当前服务器没有返回签名的 activatedAt；界面明确显示“本机首次确认”，不伪造首次激活日期。

## 验证

历史版本曾包含 48 项自动化检查（隔离目录真实删除、Dry Run、文件替换/占用、目录链接、硬链接、恢复与不覆盖、DPAPI、签名、可信时间、服务层授权门控及请求次数）。1.6.6 当前源码已做静态一致性检查；最终 WinUI/.NET 编译与运行验证以 Windows 本机 `build\build-all.cmd` 为准。

未替用户运行 DISM/SFC/CHKDSK 修改系统，未做独立 Windows 10 / 不同 TPM 硬件验收，未做管理员 MFT 性能基准测试。安装包没有商业 Authenticode 签名。本版包含保守的卸载残留识别；仍不自动清理 Windows.old，也不调度开机 chkdsk /f。

服务器协议参考：[CleanC-License-Server](https://github.com/wwz554/CleanC-License-Server)。Logo 来自用户提供的共享图片。正式包不包含服务器源码、服务器私钥、开发测试程序或测试密钥。
## 1.6.0 BuildFix2
See `FIX-1.6.0-BUILDFIX2.md` for the safety, driver, SQLite, responsiveness, and dark-theme fixes layered on 1.6.0 BuildFix1.

## 1.6.6 BuildFix2 cache lifetime

Smart Clean cache is now owned by the current C-drive scan generation rather than by UI visibility. Hiding/reopening CleanC while driver or repair work remains active reuses the same in-memory cache. A new Smart Clean cache generation is created only by starting a new C-drive scan; cleanup only refreshes the existing in-memory view index.
