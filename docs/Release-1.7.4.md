# CleanC 1.7.4

- 左侧新增“内存分析与回收”，不再藏在系统修复页。显示物理已用/可用、已提交/提交上限、系统缓存、分页池与非分页池。
- 通过微软 GetProcessMemoryInfo / PROCESS_MEMORY_COUNTERS_EX 显示私有提交和工作集，按私有提交排序。60 秒只读观察可停止，比较相同 PID 且启动时间相同的进程，排除 PID 重用；增长只是线索，不能直接断言泄漏。
- 19.2 GB 这种提交上限是物理内存与分页文件共同支撑的承诺额度，并非被预留占满的 RAM。保留 Windows 管理分页文件，不禁用 SysMain/NDU，不修改未知注册表“优化项”。
- 手动回收仍使用微软 EmptyWorkingSet，回收前重验身份和保护条件并与系统写入互斥。默认不勾选，不结束程序、不清空系统待机列表，不把工作集下降谎报成私有提交释放。
- 驱动检测先发布本机设备结果，联网更新查询独立在后台进行，使用不确定进度条而不是固定 24%。120 秒仅为应用防挂起保护，不是 Windows 必须耗时；停止/超时保留本机结果但更新状态为未确认。
- 后台保存不可变扫描快照，返回页面和计时器直接读取，不把任务状态的唯一来源放在界面进度回调中。完成时丢弃延迟进度；排队的旧页面刷新不能覆盖更新的导航。
- 空间分析缓存最多保留 6 个界面树、64 份目录数据，防止持续浏览把越来越多 XAML 控件永久保留在内存中。
- 驱动处理只使用 Windows Update、PnPUtil 与 Windows 设备管理接口。没有官方适配包时打开 Windows 可选更新/设备管理器，不自动下载厂商网站或搜索结果中的 EXE。
- Windows 组件清理继续使用 DISM，系统修复使用 DISM/SFC/CHKDSK 并独立复检；文件清理保留 Win32 路径/身份/硬链接保护，不直接删除 WinSxS 或升级回退数据。
- 保留 1.7.3 的 16 位离线码及已有签名授权兼容，不重置授权到期时间。纯短码的抗客户端篡改能力仍低于签名凭证。

依据：
- https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file
- https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex
- https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset
- https://learn.microsoft.com/en-us/windows/win32/wua_sdk/guidelines-for-asynchronous-wua-operations

回归包含真实 WinUI 窗口中的实际 DriverService 扫描管线、跨页完成/取消、旧导航丢弃和内存入口。硬件/下载结果使用隔离夹具，另有可选真实只读 WUA 缓存查询；不代表所有厂商硬件已实机验证。安装包未配置商业代码签名证书。
