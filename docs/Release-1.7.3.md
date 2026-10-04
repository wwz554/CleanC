# CleanC 1.7.3

- 离线激活新增纯 16 位码：手机扫码领取后在电脑输入，不需要 U 盘或签名文件。原授权服务到期时间不重置；短码按分钟向下取整，最多少于 60 秒，不延长授权。已有签名凭证仍验证并保留。
- 明确安全边界：纯短码采用会话 MAC 和本机 DPAPI 保存，不能提供公钥签名文件同等的抗客户端篡改能力。真正断网无法即时接收撤销状态，不宣传为不可破解。
- 驱动官方查询使用 Windows Update 异步接口，120 秒超时、阶段计时、停止扫描；在线查询失败仍保留本机设备结果，不能误报为最新版。
- 扫描完成不抢占当前页面；缓存页面始终只有一个控件所有者，减少切换回来时的过期或重复绑定状态。进度合并为最新一条，避免后台任务淹没界面队列。
- 文件结果不再等待 Windows 组件分析；只读组件分析最多 180 秒。组件清理仍走 DISM 官方接口，维护中不能强杀，完成后复检。
- 组件只读分析不再占用文件清理写锁；同一组件服务仍防止重复分析/清理。重复分类说明采用最多 512 项的共享字符串池，减少大量扫描结果的重复内存。
- 增补超过 30 天的标准 Windows Update ETL 诊断日志；只匹配官方格式和根目录，近期、嵌套、未知文件仍保留。更新组件、回退包不能因此被当成普通日志删除。
- 已有设备密钥不可用时保留旧设备码和记录，不再悄悄生成新身份；明确显示恢复提示，拒绝自动改绑。
- 系统修复增加内存诊断与手动后台工作集回收。使用 Windows GetPerformanceInfo / EmptyWorkingSet，不结束程序、不关闭服务、不定时强制清空内存；跳过前台、可见窗口、系统关键进程、非当前用户、身份变化的进程。结果为实测值，不宣称治愈内存泄漏。
- Windows.old、升级回退数据、系统安装组件和个人文件继续保护。WinSxS 不能按资源管理器显示的全部大小删除，只清理 Windows 判断可回收的旧组件，不使用 ResetBase。

微软依据：
- https://learn.microsoft.com/en-us/sysinternals/downloads/rammap
- https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-emptyworkingset
- https://learn.microsoft.com/en-us/windows/win32/wua_sdk/guidelines-for-asynchronous-wua-operations
- https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder

安装包未进行商业代码签名（仓库未配置签名证书）；Windows 安全提示不能当作已消除。正式发布前必须通过构建、回归、安装包启动及授权流程测试。
