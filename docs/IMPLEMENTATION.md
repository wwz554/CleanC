# CleanC 实施记录

用户消息优先于附件中的占位配置。本项目使用真实 HTTPS API v3 / Lease v4，续租严格为 challenge + refresh，不调用废弃 validate/device/verify。

客户端只嵌入 Downloads/cleanc-public.pem。服务器源码作为本地只读协议参考，排除在交付包之外。

架构：C# / .NET 8 / WinUI 3，模块化 Core、Scanner、Cleaner、Repair、Licensing、Logging；先编译骨架，再分模块实现和验证。

