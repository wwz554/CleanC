using System.Collections.Concurrent;
namespace CleanC.Core;
public sealed record CleanupRule(string Id,string Root,string Label,TimeSpan MinimumAge,string[]? Extensions=null,string? NamePrefix=null);
public sealed class SafetyPolicy
{
 public const string Version="2026.09.25.170";
 readonly string[] systemReportRoots;
 readonly string cbsLogs;
 readonly string windows;
 readonly string localAppData;
 readonly string roamingAppData;
 readonly string localLowAppData;
 readonly string localProgramsRoot;
 readonly Lazy<InstalledApplicationIndex> installedApps=new(()=>new InstalledApplicationIndex());
 readonly ConcurrentDictionary<string,bool> orphanRoots=new(StringComparer.OrdinalIgnoreCase);
 readonly ConcurrentDictionary<string,bool> localProgramOrphanRoots=new(StringComparer.OrdinalIgnoreCase);
 readonly IReadOnlyList<CleanupRule> rules;
 readonly ConcurrentDictionary<string,bool> markers=new(StringComparer.OrdinalIgnoreCase);
 readonly string[] recoveryRoots;
 readonly string[] protectedRoots;
 readonly string appDataContainer;
 readonly string[] appDataRoots;
 readonly string[] markerBoundaries;
 readonly HashSet<string> criticalRootNames=new(StringComparer.OrdinalIgnoreCase){"bootmgr","bootnxt","ntldr","ntdetect.com","boot.ini","hiberfil.sys","pagefile.sys","swapfile.sys","dumpstack.log.tmp","memory.dmp"};
 public IReadOnlyList<CleanupRule> Rules=>rules;
 public bool IsVolatileRule(string? ruleId)=>!string.IsNullOrWhiteSpace(ruleId)&&(ruleId.StartsWith("Chrome-",StringComparison.OrdinalIgnoreCase)||ruleId.StartsWith("Edge-",StringComparison.OrdinalIgnoreCase)||ruleId.StartsWith("Firefox-",StringComparison.OrdinalIgnoreCase)||ruleId.StartsWith("AppCache:",StringComparison.OrdinalIgnoreCase)||ruleId.Equals("Windows-ThumbnailCache",StringComparison.OrdinalIgnoreCase));
 static readonly HashSet<string> Sensitive=new(StringComparer.OrdinalIgnoreCase){".exe",".dll",".sys",".msi",".msix",".vhd",".vhdx",".vmdk",".vdi",".qcow2",".gguf",".safetensors",".onnx",".pt",".pth",".ckpt",".git",".db",".sqlite",".sqlite3",".ini",".json",".config",".pem",".key",".pfx",".p12",".kdbx",".wallet",".env",".bak"};
 static readonly HashSet<string> UserContentExtensions=new(StringComparer.OrdinalIgnoreCase){".jpg",".jpeg",".png",".gif",".bmp",".webp",".heic",".raw",".mp4",".mkv",".mov",".avi",".wmv",".flv",".webm",".mp3",".wav",".flac",".aac",".m4a",".doc",".docx",".xls",".xlsx",".ppt",".pptx",".pdf",".txt",".rtf",".csv",".md",".markdown",".psd",".ai",".fig",".sketch",".blend",".dwg",".dxf",".sql",".cs",".cpp",".c",".h",".hpp",".py",".js",".ts",".tsx",".jsx",".java",".go",".rs",".php",".html",".css",".xml",".yaml",".yml",".toml",".ipynb",".r",".rmd",".tex",".epub",".mobi",".pages",".numbers",".key",".odt",".ods",".odp",".zip",".7z",".rar",".tar",".gz"};
 static readonly HashSet<string> CacheDirectoryNames=new(StringComparer.OrdinalIgnoreCase){"cache","caches","code cache","gpucache","dawncache","shadercache","d3dscache","grshadercache","dxcache","glcache","computecache","nv_cache","localcache","tempstate","inetcache","media cache","media cache files","httpcache","http cache","imagecache","image cache","videocache","video cache","browsercache","browser cache","web cache","cef_cache","cachedata","cache_data","thumbnailcache","thumbnail cache","tmp","temp"};
 static readonly TimeSpan RecentBrowserCacheAge=TimeSpan.FromDays(7);
 static readonly TimeSpan RecentAppCacheAge=TimeSpan.FromDays(7);
 static readonly HashSet<string> ExecutableStateExtensions=new(StringComparer.OrdinalIgnoreCase){".exe",".dll",".sys",".msi",".msix",".appx",".appxbundle",".msixbundle",".com",".bat",".cmd",".ps1"};
 static readonly HashSet<string> StatefulCacheExtensions=new(StringComparer.OrdinalIgnoreCase){".db",".sqlite",".sqlite3",".ini",".json",".xml",".config"};
 static readonly HashSet<string> HighChurnCacheNames=new(StringComparer.OrdinalIgnoreCase){"index","index.log","lock","lockfile","current","journal","metadata","cache.lock","cache_lock","visited links","last version"};

 static readonly HashSet<string> NeverOrphanNames=new(StringComparer.OrdinalIgnoreCase){"microsoft","packages","connecteddevicesplatform","comms","google","mozilla","apple computer","apple","nvidia","amd","intel","docker","dropbox","onedrive","programs","temp","crashdumps","d3dscache","publishers","credentials","crypto"};
 public SafetyPolicy(IReadOnlyList<CleanupRule>? customRules=null)
 {
  windows=Environment.GetFolderPath(Environment.SpecialFolder.Windows);
  var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
  var roaming=Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
  localAppData=local;roamingAppData=roaming;
  localProgramsRoot=Path.Combine(local,"Programs");
  var profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
  var programData=Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
  systemReportRoots=[Path.Combine(programData,@"Microsoft\Windows\WER\ReportArchive"),Path.Combine(programData,@"Microsoft\Windows\WER\ReportQueue")];
  cbsLogs=Path.Combine(windows,@"Logs\CBS");
  var systemDrive=Path.GetPathRoot(windows)!;
  var localLow=Path.Combine(profile,"AppData","LocalLow");
  localLowAppData=localLow;
  appDataContainer=Path.Combine(profile,"AppData");

  recoveryRoots=NormalizeRoots([
   Path.Combine(systemDrive,"Recovery"),
   Path.Combine(windows,"Recovery"),
   Path.Combine(windows,"System32","Recovery"),
   Path.Combine(programData,"Microsoft","Recovery"),
   Path.Combine(systemDrive,"System Volume Information")
  ]);

  protectedRoots=NormalizeRoots([
   Path.Combine(windows,"System32"),
   Path.Combine(windows,"SysWOW64"),
   Path.Combine(windows,"WinSxS"),
   Path.Combine(windows,"Installer"),
   Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
   Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
   programData,
   Path.Combine(systemDrive,"$Recycle.Bin"),
   Path.Combine(systemDrive,"Windows.old"),
   Path.Combine(systemDrive,"Boot"),
   Path.Combine(systemDrive,"EFI"),
   Path.Combine(systemDrive,"Config.Msi"),
   Path.Combine(systemDrive,"$WinREAgent"),
   Path.Combine(systemDrive,"$WINDOWS.~BT"),
   Path.Combine(systemDrive,"$WINDOWS.~WS"),
   Path.Combine(systemDrive,"$SysReset"),
   Path.Combine(systemDrive,"$GetCurrent"),
   Path.Combine(systemDrive,"RecoveryImage"),
   Path.Combine(systemDrive,"OEM"),
   Path.Combine(systemDrive,"Drivers"),
   Path.Combine(systemDrive,"OneDriveTemp"),
   Path.Combine(systemDrive,"Documents and Settings"),
   Path.Combine(systemDrive,"ESD"),
   Path.Combine(systemDrive,"inetpub"),
   Path.Combine(systemDrive,"Users","Default"),
   Path.Combine(profile,".ssh"),
   Path.Combine(profile,".gnupg"),
   Path.Combine(profile,".aws"),
   Path.Combine(profile,".azure"),
   Path.Combine(profile,".kube"),
   Path.Combine(profile,".docker"),
   Path.Combine(profile,".git-credentials"),
   Path.Combine(profile,".npmrc"),
   Path.Combine(profile,".pypirc"),
   Environment.GetEnvironmentVariable("OneDrive")??"",
   Environment.GetEnvironmentVariable("OneDriveConsumer")??"",
   Environment.GetEnvironmentVariable("OneDriveCommercial")??"",
   Path.Combine(profile,"OneDrive"),
   Path.Combine(profile,"Dropbox"),
   Path.Combine(profile,"iCloudDrive"),
   Path.Combine(local,@"Microsoft\WindowsApps"),
   AppPaths.UserData,
   AppPaths.MachineData
  ]);

  appDataRoots=NormalizeRoots([local,roaming,localLow]);
  markerBoundaries=NormalizeRoots([
   systemDrive,
   profile,
   Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
   Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
   Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
   Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
   Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
   Path.Combine(profile,"Downloads")
  ]);

  if(customRules is not null){rules=customRules;return;}
  var list=new List<CleanupRule>{
   new("user-temp",Path.Combine(local,"Temp"),"用户临时文件",TimeSpan.FromDays(2)),
   new("windows-temp",Path.Combine(windows,"Temp"),"Windows 临时文件",TimeSpan.FromDays(3)),
   new("windows-system-temp",Path.Combine(windows,"SystemTemp"),"系统服务临时文件",TimeSpan.FromDays(7)),
   new("crash",Path.Combine(local,"CrashDumps"),"应用崩溃转储",TimeSpan.FromDays(7),[".dmp"]),
   new("shader",Path.Combine(local,"D3DSCache"),"DirectX 着色器缓存",TimeSpan.FromDays(30)),
   new("shader-nvidia-dx",Path.Combine(local,@"NVIDIA\DXCache"),"NVIDIA DirectX 着色器缓存",TimeSpan.FromDays(30)),
   new("shader-nvidia-gl",Path.Combine(local,@"NVIDIA\GLCache"),"NVIDIA OpenGL 着色器缓存",TimeSpan.FromDays(30)),
   new("shader-amd-dx",Path.Combine(local,@"AMD\DxCache"),"AMD DirectX 着色器缓存",TimeSpan.FromDays(30)),
   new("shader-amd-dxc",Path.Combine(local,@"AMD\DxcCache"),"AMD 编译着色器缓存",TimeSpan.FromDays(30)),
   new("wer",Path.Combine(local,@"Microsoft\Windows\WER\ReportArchive"),"已归档错误报告",TimeSpan.FromDays(14)),
   new("wer-queue",Path.Combine(local,@"Microsoft\Windows\WER\ReportQueue"),"旧错误报告队列",TimeSpan.FromDays(14)),
   new("Windows-ThumbnailCache",Path.Combine(local,@"Microsoft\Windows\Explorer"),"Windows 缩略图缓存",TimeSpan.Zero,[".db"],"thumbcache_")
  };
  foreach(var browser in new[]{("Chrome",@"Google\Chrome\User Data"),("Edge",@"Microsoft\Edge\User Data")}) {
   var root=Path.Combine(local,browser.Item2);
   foreach(var profileDir in SafeDirectories(root).Where(p=>Path.GetFileName(p)=="Default"||Path.GetFileName(p).StartsWith("Profile ",StringComparison.Ordinal))) {
    foreach(var cache in new[]{"Cache","Code Cache","GPUCache","DawnCache","GrShaderCache"})
     list.Add(new(browser.Item1+"-"+Path.GetFileName(profileDir)+"-"+cache,Path.Combine(profileDir,cache),browser.Item1+" 浏览器缓存",RecentBrowserCacheAge));
   }
  }
  foreach(var profileDir in SafeDirectories(Path.Combine(local,@"Mozilla\Firefox\Profiles")))
   list.Add(new("Firefox-"+Path.GetFileName(profileDir),Path.Combine(profileDir,"cache2"),"Firefox 浏览器缓存",RecentBrowserCacheAge));
  rules=list;
 }
 static string[] NormalizeRoots(IEnumerable<string> roots)=>roots.Where(x=>!string.IsNullOrWhiteSpace(x)).Select(x=>Path.GetFullPath(x).TrimEnd('\\')).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
 static IEnumerable<string> SafeDirectories(string root){try{return Directory.Exists(root)?Directory.GetDirectories(root):[];}catch(UnauthorizedAccessException){return [];}catch(IOException){return [];}}
 public static bool Within(string path,string root)=>!string.IsNullOrWhiteSpace(root)&&(string.Equals(path.TrimEnd('\\'),root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)||path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase));
 public static bool HasReparseAncestor(string path)
 {
  for(var d=new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(path))!);d is not null;d=d.Parent)if((d.Attributes&FileAttributes.ReparsePoint)!=0)return true;
  return false;
 }
 public Classification ClassifyPath(string path,bool isDirectory)
 {
  var p=Path.GetFullPath(path).TrimEnd('\\');
  if(IsPersistentAppState(p)||IsPersistentCacheFile(p))return new(SafetyLevel.Protected,"应用持久数据","站点离线数据、会话和数据库不是可随意删除的缓存");
  if(TrySystemDiagnostic(p,null,DateTime.UtcNow,out var diagnostic))return diagnostic;
  if(Within(p,AppPaths.DriverBackups))return ClassifyDriverBackup(p);
  if(Within(p,AppPaths.DriverPackages))return new(SafetyLevel.Safe,"驱动安装程序","CleanC 保存的官方驱动安装包副本，可清理","cleanc-driver-package");
  if(recoveryRoots.Any(root=>Within(p,root)))return new(SafetyLevel.Protected,"系统恢复","Windows 恢复、还原点或恢复环境数据受到保护");
  if(TryClassifyLocalPrograms(p,isDirectory,DateTime.UtcNow,false,null,out var programsKind))return programsKind;
  if(protectedRoots.Any(root=>Within(p,root)))return new(SafetyLevel.Protected,"系统与应用","系统、程序、服务、凭据、云同步或应用公共数据受到保护");
  if(IsCriticalRootItem(p))return new(SafetyLevel.Protected,"启动与系统","Windows 启动、内存或系统状态文件受到保护");
  if(!isDirectory&&Sensitive.Contains(Path.GetExtension(p)))return new(SafetyLevel.Protected,"软件与项目","应用、驱动、配置、数据库、模型或虚拟磁盘受到保护");
  var rule=rules.FirstOrDefault(r=>Within(p,r.Root));
  if(rule is not null)
  {
   if(!isDirectory&&IsAlwaysOptionalCacheArtifact(p,rule.Id))return new(SafetyLevel.Optional,"可重建缓存结构","该索引/容器会很快重新生成，放在可选项避免安全清理后立即再次出现",rule.Id);
   if(!isDirectory&&UserContentExtensions.Contains(Path.GetExtension(p)))return new(SafetyLevel.UserData,"缓存目录中的个人文件","检测到图片、视频、文档或压缩包；即使位于缓存目录也交给用户确认",rule.Id);
   return new(SafetyLevel.Optional,rule.Label,IsBrowserRule(rule.Id)?"浏览器缓存目录；最近 7 天内容默认保留，旧缓存按文件逐项判断":"已知清理区域；具体文件按年龄和类型逐项判断",rule.Id);
  }
  if(IsActiveRuntimeCachePath(p))
   return new(SafetyLevel.Optional,"活动会话缓存","运行中的应用会频繁创建、移动或重建这些会话文件；默认暂不清理，避免扫描后路径立即失效","active-runtime-cache");
  if(TryKnownAppCacheRoot(p,out var appCacheRoot))
  {
   if(!isDirectory&&UserContentExtensions.Contains(Path.GetExtension(p)))return new(SafetyLevel.UserData,"应用缓存中的个人文件","检测到图片、视频、文档或压缩包；默认保留，由用户确认","AppCache:"+appCacheRoot);
   return new(SafetyLevel.Optional,"应用缓存","缓存目录本身可能正在被程序使用；文件按年龄和类型逐项判断","AppCache:"+appCacheRoot);
  }
  if(TryAppDataOwner(p,out var ownerRoot,out var ownerName)&&IsProbableOrphanRoot(ownerRoot,ownerName,DateTime.UtcNow,false,null))return new(SafetyLevel.Optional,"疑似卸载残留","未匹配到已安装程序且长期未更新；展开后确认内容再决定");
  if(Within(p,windows))return new(SafetyLevel.Protected,"Windows","Windows 系统目录仅供查看；仅明确白名单缓存允许清理");
  if(new[]{@"Google\Chrome\User Data",@"Microsoft\Edge\User Data",@"Mozilla\Firefox\Profiles"}.Any(x=>Within(p,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),x))))
   return new(SafetyLevel.Protected,"浏览器数据","浏览器账户、密码、历史与站点数据受到保护");
  if(Within(p,appDataContainer)||appDataRoots.Any(root=>Within(p,root)))
   return new(SafetyLevel.Protected,"应用数据","应用设置、账户、凭据或运行状态受到保护；只有明确白名单缓存才允许清理");
  return new(SafetyLevel.UserData,"仅供分析","无法证明为垃圾，空间分析默认只展示；删除前必须再次验证");
 }
 static Classification ClassifyDriverBackup(string path)
 {
  var root=Path.GetFullPath(AppPaths.DriverBackups).TrimEnd('\\');
  var full=Path.GetFullPath(path).TrimEnd('\\');
  if(string.Equals(full,root,StringComparison.OrdinalIgnoreCase))
   return new(SafetyLevel.Protected,"驱动备份目录","CleanC 驱动备份容器本身受到保护");

  var relative=Path.GetRelativePath(root,full);
  var first=relative.Split(new[]{Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar},StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
  if(string.IsNullOrWhiteSpace(first))
   return new(SafetyLevel.Protected,"驱动备份目录","无法确定备份子目录，保持保护");

  var folder=Path.Combine(root,first);
  var marker=Path.Combine(folder,"CleanC-backup-protected.flag");
  if(File.Exists(marker))
   return new(SafetyLevel.Protected,"待回退驱动备份","该驱动升级失败、尚未确认或仍可能需要回退，暂时禁止清理");

  return new(SafetyLevel.Optional,"备份的驱动","保留以便回退；只有确认不再需要时才手动选择清理，一键安全清理不会删除","cleanc-driver-backup");
 }
 bool IsCriticalRootItem(string path)
 {
  var root=Path.GetPathRoot(windows);if(string.IsNullOrWhiteSpace(root))return false;
  var parent=Path.GetDirectoryName(path.TrimEnd('\\'));
  return string.Equals(parent?.TrimEnd('\\'),root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)&&criticalRootNames.Contains(Path.GetFileName(path));
 }
 public Classification Classify(FileSnapshot f,DateTime utcNow,bool fresh=false,IDictionary<string,bool>? sessionMarkers=null)
 {
  var p=Path.GetFullPath(f.Path);var ext=Path.GetExtension(p);
  if(IsPersistentAppState(p)||IsPersistentCacheFile(p))return new(SafetyLevel.Protected,"应用持久数据","站点离线数据、会话和数据库不是可随意删除的缓存");
  if((f.Attributes&(FileAttributes.ReparsePoint|FileAttributes.System|FileAttributes.Offline|FileAttributes.Encrypted))!=0||f.Links>1)return new(SafetyLevel.Protected,"受保护","链接、系统属性、云端文件或硬链接");
  if(TrySystemDiagnostic(p,f,utcNow,out var diagnostic))return diagnostic;
  if(Within(p,AppPaths.DriverBackups))return ClassifyDriverBackup(p);
  if(Within(p,AppPaths.DriverPackages))return new(SafetyLevel.Safe,"驱动安装程序","CleanC 保存的官方驱动安装包副本，可清理","cleanc-driver-package");
  if(recoveryRoots.Any(root=>Within(p,root)))return new(SafetyLevel.Protected,"系统恢复","Windows 恢复、还原点或恢复环境数据受到保护");
  if(TryClassifyLocalPrograms(p,false,utcNow,fresh,sessionMarkers,out var programsKind))return programsKind;
  if(protectedRoots.Any(root=>Within(p,root)))return new(SafetyLevel.Protected,"系统与应用","系统、程序、服务、凭据、云同步或应用公共数据受到保护");
  if(IsCriticalRootItem(p))return new(SafetyLevel.Protected,"启动与系统","Windows 启动、内存或系统状态文件受到保护");

  var rule=rules.FirstOrDefault(r=>Within(p,r.Root));
  if(rule is not null) {
   if(IsAlwaysOptionalCacheArtifact(p,rule.Id))
    return new(SafetyLevel.Optional,rule.Id.Equals("Windows-ThumbnailCache",StringComparison.OrdinalIgnoreCase)?"Windows 缩略图缓存":"可重建缓存结构",
     "该文件属于会快速重新生成的缓存索引/容器；默认暂不清理，避免清理后马上再次出现",rule.Id);
   if(UserContentExtensions.Contains(ext))return new(SafetyLevel.UserData,"缓存目录中的个人文件","检测到图片、视频、文档或压缩包；即使位于缓存目录也交给用户确认",rule.Id);
   if(Sensitive.Contains(ext)||ExecutableStateExtensions.Contains(ext)||PortableWithin(p,rule.Root,fresh,sessionMarkers))return new(SafetyLevel.Protected,"软件 / 项目","检测到应用、配置、项目、模型或虚拟机");
   if(rule.Extensions is not null&&!rule.Extensions.Contains(ext,StringComparer.OrdinalIgnoreCase))return new(SafetyLevel.UserData,"未知临时数据","未匹配明确可清理的文件类型");
   if(rule.NamePrefix is not null&&!Path.GetFileName(p).StartsWith(rule.NamePrefix,StringComparison.OrdinalIgnoreCase))return new(SafetyLevel.UserData,"用户数据","不符合缓存名称规则");
   if(IsRecent(f,utcNow,rule.MinimumAge))
   {
    var reason=IsBrowserRule(rule.Id)
     ?"最近 7 天浏览器缓存默认保留，可减少常用网站重新下载资源和首次加载变慢"
     :rule.Id.StartsWith("shader",StringComparison.OrdinalIgnoreCase)
      ?"近期着色器缓存可能影响游戏/图形程序启动与首轮渲染，默认暂不清理"
      :"文件较新或仍可能被系统/应用频繁使用，默认暂不清理";
    return new(SafetyLevel.Optional,rule.Label,reason,rule.Id);
   }
   if(f.Size>1024L*1024*1024)return new(SafetyLevel.Optional,rule.Label,"单文件大于 1 GB，避免误删异常的大型缓存，需手动确认",rule.Id);
   return new(SafetyLevel.Safe,rule.Label,IsBrowserRule(rule.Id)
    ?"浏览器缓存已超过 7 天；不包含 Cookie、登录状态、历史记录、IndexedDB、Local Storage 或 Service Worker 数据"
    :"明确临时/诊断缓存且超过保留期；删除不会移除应用程序本体或用户文档",rule.Id);
  }

  if(IsActiveRuntimeCachePath(p))
   return new(SafetyLevel.Optional,"活动会话缓存","应用正在使用或会快速重建此会话目录；默认不清理，避免清理后立即重建或扫描结果失效","active-runtime-cache");

  if(TryKnownAppCacheRoot(p,out var appCacheRoot))
  {
   var cacheRule="AppCache:"+appCacheRoot;
   if(UserContentExtensions.Contains(ext))return new(SafetyLevel.UserData,"应用缓存中的个人文件","检测到图片、视频、文档或压缩包；默认保留，由用户确认",cacheRule);
   if(ExecutableStateExtensions.Contains(ext)||Sensitive.Contains(ext))
    return new(SafetyLevel.Protected,"应用程序文件","缓存目录中出现程序、驱动、模型或虚拟磁盘文件，无法证明可安全删除",cacheRule);
   if(StatefulCacheExtensions.Contains(ext))
    return new(SafetyLevel.Protected,"应用缓存状态","数据库/配置型文件可能承载索引、会话或应用状态，无法证明删除后无影响，因此受保护",cacheRule);
   if(IsHighChurnCacheArtifact(p))
    return new(SafetyLevel.Optional,"可重建应用缓存","缓存索引/锁/日志等文件会很快重新生成，放到可选项避免安全清理后立即再次出现",cacheRule);
   if(IsRecent(f,utcNow,RecentAppCacheAge))
    return new(SafetyLevel.Optional,"近期应用缓存","最近 7 天生成或更新，可能正在被常用软件利用；默认保留以减少重新缓存和启动变慢",cacheRule);
   if(f.Size>1024L*1024*1024)
    return new(SafetyLevel.Optional,"大型应用缓存","单文件大于 1 GB，需手动确认",cacheRule);
   return new(SafetyLevel.Safe,"旧应用缓存","明确位于缓存目录且超过 7 天；不包含程序、配置、数据库或用户文档",cacheRule);
  }

  if(TryAppDataOwner(p,out var ownerRoot,out var ownerName)&&IsProbableOrphanRoot(ownerRoot,ownerName,utcNow,fresh,sessionMarkers))
  {
   var orphanId="Orphan:"+ownerRoot;
   if(UserContentExtensions.Contains(ext))return new(SafetyLevel.UserData,"卸载残留中的个人文件","疑似卸载软件残留，但检测到照片、视频、文档或压缩包；必须由用户展开后手动选择",orphanId);
   if(Sensitive.Contains(ext))return new(SafetyLevel.Optional,"卸载残留","疑似卸载软件残留，包含程序、配置或数据库文件；默认保留，展开后手动选择",orphanId);
   return new(SafetyLevel.Optional,"疑似卸载残留","目录长期未更新且未匹配到当前已安装程序；AppData 可能仍承载便携软件状态，因此默认不自动删除",orphanId);
  }

  if(Within(p,windows))return new(SafetyLevel.Protected,"Windows","Windows 文件必须由官方工具维护");

  if(new[]{@"Google\Chrome\User Data",@"Microsoft\Edge\User Data",@"Mozilla\Firefox\Profiles"}.Any(x=>Within(p,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),x))))
   return new(SafetyLevel.Protected,"浏览器数据","浏览器账户、密码、历史与站点数据受到保护");

  if(Within(p,appDataContainer)||appDataRoots.Any(root=>Within(p,root)))
   return new(SafetyLevel.Protected,"应用数据","应用设置、账户、凭据或运行状态受到保护；只有明确白名单缓存才允许清理");

  if(PortableNearby(p,fresh,sessionMarkers))
   return new(SafetyLevel.Protected,"软件 / 项目","检测到便携软件、开发项目、游戏或应用数据目录");

  if(Sensitive.Contains(ext))return new(SafetyLevel.Protected,ext is ".gguf" or ".safetensors" or ".onnx" or ".pt" or ".pth" or ".ckpt"?"AI 模型":ext is ".vhd" or ".vhdx" or ".vmdk" or ".vdi" or ".qcow2"?"虚拟机":"软件与项目","应用、配置或工作数据不清理");
  return new(SafetyLevel.UserData,"用户数据","无法证明为垃圾，默认保留");
 }
 bool TrySystemDiagnostic(string path,FileSnapshot? snapshot,DateTime utcNow,out Classification result)
 {
  result=new(SafetyLevel.Protected,"系统诊断数据","仅明确的旧报告和已轮转日志允许清理");
  var report=systemReportRoots.Any(root=>Within(path,root));
  var cbs=Within(path,cbsLogs);
  if(!report&&!cbs)return false;
  // Do not whitelist all of ProgramData, Windows\Logs, current CBS.log or report attachments.
  var name=Path.GetFileName(path);var ext=Path.GetExtension(path);
  var accepted=report?new[]{".wer",".dmp",".hdmp",".cab"}.Contains(ext,StringComparer.OrdinalIgnoreCase)
   :System.Text.RegularExpressions.Regex.IsMatch(name,@"^CbsPersist_\d+(?:_\d+)?\.(?:cab|log)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase|System.Text.RegularExpressions.RegexOptions.CultureInvariant);
  if(!accepted)return true;
  var age=TimeSpan.FromDays(report?14:30);var id=report?"system-wer":"system-cbs-archive";
  var label=report?"系统旧错误报告":"Windows 已轮转组件日志";
  result=new(snapshot is not null&&!IsRecent(snapshot,utcNow,age)?SafetyLevel.Safe:SafetyLevel.Optional,label,
   "仅清理明确诊断文件；保留近期报告、当前日志及不匹配的附件。删除后无法用该旧报告排障。",id);
  return true;
 }
 bool TryClassifyLocalPrograms(string path,bool isDirectory,DateTime utcNow,bool fresh,IDictionary<string,bool>? sessionMarkers,out Classification result)
 {
  result=new(SafetyLevel.Protected,"本地安装程序","当前用户安装的软件目录受到保护");
  if(!Within(path,localProgramsRoot))return false;
  if(string.Equals(path.TrimEnd('\\'),localProgramsRoot.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))return true;

  var relative=Path.GetRelativePath(localProgramsRoot,path);
  var ownerName=relative.Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
  if(string.IsNullOrWhiteSpace(ownerName))return true;
  var ownerRoot=Path.Combine(localProgramsRoot,ownerName);

  if(!IsProbableLocalProgramOrphan(ownerRoot,ownerName,utcNow,fresh,sessionMarkers))
  {
   result=new(SafetyLevel.Protected,"本地安装程序","检测到仍可能存在可运行程序、当前安装记录或近期活动；为保护正常/便携程序不自动清理");
   return true;
  }

  if(isDirectory)
  {
   result=new(SafetyLevel.Optional,"疑似卸载程序残留","未匹配当前安装记录、超过 30 天且未检测到可运行程序；展开后按文件风险处理","OrphanProgram:"+ownerRoot);
   return true;
  }

  var ext=Path.GetExtension(path);
  var ruleId="OrphanProgram:"+ownerRoot;
  if(UserContentExtensions.Contains(ext))
  {
   result=new(SafetyLevel.UserData,"卸载残留中的个人文件","卸载残留目录中发现文档、图片、视频或压缩包，保留给用户确认",ruleId);
   return true;
  }
  if(ExecutableStateExtensions.Contains(ext)||StatefulCacheExtensions.Contains(ext)||Sensitive.Contains(ext))
  {
   result=new(SafetyLevel.Optional,"卸载程序残留","已高置信度判断为卸载残留，但程序/配置/数据库文件默认不自动删除，可由用户手动选择",ruleId);
   return true;
  }

  result=new(SafetyLevel.Optional,"疑似卸载程序残留","缺少安装记录并不能证明数据无用；请确认不再需要后手动选择，一键安全清理默认保留",ruleId);
  return true;
 }

 bool IsProbableLocalProgramOrphan(string ownerRoot,string ownerName,DateTime utcNow,bool fresh,IDictionary<string,bool>? sessionMarkers)
 {
  if(fresh)
  {
   var key="__program_orphan__:"+ownerRoot;
   if(sessionMarkers is not null&&sessionMarkers.TryGetValue(key,out var cached))return cached;
   var value=EvaluateLocalProgramOrphan(ownerRoot,ownerName,utcNow,new InstalledApplicationIndex());
   if(sessionMarkers is not null)sessionMarkers[key]=value;
   return value;
  }
  return localProgramOrphanRoots.GetOrAdd(ownerRoot,_=>EvaluateLocalProgramOrphan(ownerRoot,ownerName,utcNow,installedApps.Value));
 }

 static bool EvaluateLocalProgramOrphan(string ownerRoot,string ownerName,DateTime utcNow,InstalledApplicationIndex apps)
 {
  try
  {
   if(!apps.Available||apps.LooksInstalled(ownerName)||!Directory.Exists(ownerRoot))return false;
   if(ContainsLikelyLiveExecutable(ownerRoot))return false;
   var latest=LatestTreeActivityUtc(ownerRoot,out var complete);
   if(!complete)return false; // large/opaque tree: fail closed and protect it
   if(utcNow-latest<TimeSpan.FromDays(30))return false;
   return true;
  }
  catch{return false;}
 }

 static bool ContainsLikelyLiveExecutable(string ownerRoot)
 {
  try
  {
   var options=new EnumerationOptions{IgnoreInaccessible=true,RecurseSubdirectories=true,ReturnSpecialDirectories=false,AttributesToSkip=FileAttributes.ReparsePoint};
   var seen=0;
   foreach(var file in Directory.EnumerateFiles(ownerRoot,"*.exe",options))
   {
    if(++seen>1024)return true; // too many executables to prove this is residue
    var name=Path.GetFileNameWithoutExtension(file);
    if(name.StartsWith("unins",StringComparison.OrdinalIgnoreCase)||
       name.StartsWith("uninstall",StringComparison.OrdinalIgnoreCase)||
       name.StartsWith("update",StringComparison.OrdinalIgnoreCase)||
       name.StartsWith("updater",StringComparison.OrdinalIgnoreCase)||
       name.StartsWith("crashreport",StringComparison.OrdinalIgnoreCase)||
       name.StartsWith("repair",StringComparison.OrdinalIgnoreCase))continue;
    return true;
   }
   return false;
  }
  catch{return true;}
 }

 static DateTime LatestTreeActivityUtc(string root,out bool complete)
 {
  complete=true;var latest=DateTime.UnixEpoch;var seen=0;
  try
  {
   var options=new EnumerationOptions{IgnoreInaccessible=true,RecurseSubdirectories=true,ReturnSpecialDirectories=false,AttributesToSkip=FileAttributes.ReparsePoint};
   foreach(var path in Directory.EnumerateFileSystemEntries(root,"*",options))
   {
    if(++seen>8192){complete=false;return DateTime.UtcNow;}
    try
    {
     var write=File.GetLastWriteTimeUtc(path);if(write>latest)latest=write;
     var create=File.GetCreationTimeUtc(path);if(create>latest)latest=create;
    }
    catch{complete=false;return DateTime.UtcNow;}
   }
   var rootWrite=Directory.GetLastWriteTimeUtc(root);if(rootWrite>latest)latest=rootWrite;
   return latest;
  }
  catch{complete=false;return DateTime.UtcNow;}
 }

 bool IsActiveRuntimeCachePath(string path)
 {
  if(!Within(path,localAppData))return false;
  var normalized=path.Replace('/','\\');
  if(normalized.Contains(@"\local-agent-mode-sessions\",StringComparison.OrdinalIgnoreCase)||
     normalized.Contains(@"\agent-mode-sessions\",StringComparison.OrdinalIgnoreCase))return true;
  if(normalized.Contains(@"\Packages\",StringComparison.OrdinalIgnoreCase)&&
     (normalized.Contains(@"\LocalCache\",StringComparison.OrdinalIgnoreCase)||
      normalized.Contains(@"\TempState\",StringComparison.OrdinalIgnoreCase))&&
     (normalized.Contains(@"\session\",StringComparison.OrdinalIgnoreCase)||
      normalized.Contains(@"\sessions\",StringComparison.OrdinalIgnoreCase)||
      normalized.Contains(@"\skills-plugin\",StringComparison.OrdinalIgnoreCase)))return true;
  return false;
 }

 bool TryKnownAppCacheRoot(string path,out string root)
 {
  root="";
  foreach(var baseRoot in new[]{localAppData,roamingAppData,localLowAppData})
  {
   if(!Within(path,baseRoot))continue;
   var relative=Path.GetRelativePath(baseRoot,path);var parts=relative.Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries);
   if(parts.Length<2)continue;
   // Check every descendant before accepting a cache ancestor: a directory name is not proof of disposable data.
   if(parts.Any(IsPersistentStateDirectory))return false;
   for(var i=1;i<parts.Length;i++)
   {
    if(parts[i].Equals("CacheStorage",StringComparison.OrdinalIgnoreCase)||parts[i].Equals("Service Worker",StringComparison.OrdinalIgnoreCase)||parts[i].Equals("IndexedDB",StringComparison.OrdinalIgnoreCase)||parts[i].Equals("Local Storage",StringComparison.OrdinalIgnoreCase)||parts[i].Equals("Session Storage",StringComparison.OrdinalIgnoreCase))return false;
    if(!CacheDirectoryNames.Contains(parts[i]))continue;
    root=Path.Combine(baseRoot,Path.Combine(parts[..(i+1)]));return true;
   }
  }
  return false;
 }
 static bool IsPersistentStateDirectory(string part)=>part.Equals("CacheStorage",StringComparison.OrdinalIgnoreCase)||part.Equals("Service Worker",StringComparison.OrdinalIgnoreCase)||part.Equals("IndexedDB",StringComparison.OrdinalIgnoreCase)||part.Equals("Local Storage",StringComparison.OrdinalIgnoreCase)||part.Equals("Session Storage",StringComparison.OrdinalIgnoreCase);
 bool IsPersistentAppState(string path)=>appDataRoots.Any(root=>Within(path,root))&&path.Split(new[]{'\\','/'},StringSplitOptions.RemoveEmptyEntries).Any(IsPersistentStateDirectory);
 static bool IsPersistentCacheFile(string path)
 {
  var name=Path.GetFileName(path);
  return new[]{"Cookies","Login Data","Web Data","Preferences","Secure Preferences","History","Bookmarks",".env"}.Contains(name,StringComparer.OrdinalIgnoreCase)||
   name.EndsWith("-wal",StringComparison.OrdinalIgnoreCase)||name.EndsWith("-shm",StringComparison.OrdinalIgnoreCase)||name.EndsWith("-journal",StringComparison.OrdinalIgnoreCase);
 }
 bool TryAppDataOwner(string path,out string ownerRoot,out string ownerName)
 {
  foreach(var baseRoot in new[]{localAppData,roamingAppData,localLowAppData})
  {
   if(!Within(path,baseRoot))continue;var rel=Path.GetRelativePath(baseRoot,path);var first=rel.Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
   if(string.IsNullOrWhiteSpace(first))continue;ownerName=first;ownerRoot=Path.Combine(baseRoot,first);return true;
  }
  ownerRoot=ownerName="";return false;
 }
 bool IsProbableOrphanRoot(string ownerRoot,string ownerName,DateTime utcNow,bool fresh,IDictionary<string,bool>? sessionMarkers)
 {
  if(NeverOrphanNames.Contains(ownerName)||ownerName.StartsWith("{",StringComparison.Ordinal)||ownerName.Length<4)return false;
  if(fresh)
  {
   var key="__appdata_orphan__:"+ownerRoot;
   if(sessionMarkers is not null&&sessionMarkers.TryGetValue(key,out var cached))return cached;
   var value=EvaluateOrphanRoot(ownerRoot,ownerName,utcNow,new InstalledApplicationIndex());
   if(sessionMarkers is not null)sessionMarkers[key]=value;
   return value;
  }
  return orphanRoots.GetOrAdd(ownerRoot,_=>EvaluateOrphanRoot(ownerRoot,ownerName,utcNow,installedApps.Value));
 }
 static bool EvaluateOrphanRoot(string ownerRoot,string ownerName,DateTime utcNow,InstalledApplicationIndex apps)
 {
  try
  {
   if(!apps.Available||apps.LooksInstalled(ownerName)||!Directory.Exists(ownerRoot))return false;
   if(IsMarkerDirectory(ownerRoot)||ContainsLikelyLiveExecutable(ownerRoot))return false;
   var latest=LatestTreeActivityUtc(ownerRoot,out var complete);
   if(!complete||utcNow-latest<TimeSpan.FromDays(30))return false;
   return true;
  }catch{return false;}
 }

 static bool IsBrowserRule(string? ruleId)=>!string.IsNullOrWhiteSpace(ruleId)&&(
  ruleId.StartsWith("Chrome-",StringComparison.OrdinalIgnoreCase)||
  ruleId.StartsWith("Edge-",StringComparison.OrdinalIgnoreCase)||
  ruleId.StartsWith("Firefox-",StringComparison.OrdinalIgnoreCase));

 static bool IsRecent(FileSnapshot f,DateTime utcNow,TimeSpan age)
 {
  if(age<=TimeSpan.Zero)return false;
  var newest=f.LastWriteUtc>f.CreationUtc?f.LastWriteUtc:f.CreationUtc;
  if(newest>utcNow.AddMinutes(5))return true;
  return utcNow-newest<age;
 }

 static bool IsAlwaysOptionalCacheArtifact(string path,string? ruleId)
 {
  if(ruleId?.Equals("Windows-ThumbnailCache",StringComparison.OrdinalIgnoreCase)==true)return true;
  if(IsBrowserRule(ruleId))
  {
   var name=Path.GetFileName(path);
   if(name.Equals("index",StringComparison.OrdinalIgnoreCase)||name.Equals("index.log",StringComparison.OrdinalIgnoreCase))return true;
   if(name.Length==6&&name.StartsWith("data_",StringComparison.OrdinalIgnoreCase)&&name[5]>='0'&&name[5]<='3')return true;
   for(var dir=Path.GetDirectoryName(path);dir is not null;dir=Path.GetDirectoryName(dir))
    if(Path.GetFileName(dir).Equals("index-dir",StringComparison.OrdinalIgnoreCase))return true;
  }
  return false;
 }

 static bool IsHighChurnCacheArtifact(string path)
 {
  var name=Path.GetFileName(path);
  if(HighChurnCacheNames.Contains(name))return true;
  if(name.StartsWith("MANIFEST-",StringComparison.OrdinalIgnoreCase))return true;
  var ext=Path.GetExtension(name);
  return ext.Equals(".lock",StringComparison.OrdinalIgnoreCase)||
         ext.Equals(".journal",StringComparison.OrdinalIgnoreCase)||
         ext.Equals(".tmp-journal",StringComparison.OrdinalIgnoreCase);
 }

 bool PortableWithin(string file,string ruleRoot,bool fresh,IDictionary<string,bool>? sessionMarkers)
 {
  for(var dir=Path.GetDirectoryName(file);dir is not null&&Within(dir,ruleRoot);dir=Path.GetDirectoryName(dir)){
   if(Marker(dir,fresh,sessionMarkers))return true;
  }return false;
 }
 bool PortableNearby(string file,bool fresh,IDictionary<string,bool>? sessionMarkers)
 {
  var dir=Path.GetDirectoryName(file);var depth=0;
  while(dir is not null&&depth<6)
  {
   if(markerBoundaries.Any(root=>string.Equals(dir.TrimEnd('\\'),root.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase)))break;
   if(Marker(dir,fresh,sessionMarkers))return true;
   var parent=Path.GetDirectoryName(dir);
   if(string.IsNullOrWhiteSpace(parent)||string.Equals(parent,dir,StringComparison.OrdinalIgnoreCase))break;
   dir=parent;depth++;
  }
  return false;
 }
 bool Marker(string dir,bool fresh,IDictionary<string,bool>? sessionMarkers)
 {
  if(sessionMarkers is not null){if(!sessionMarkers.TryGetValue(dir,out var marked)){marked=IsMarkerDirectory(dir);sessionMarkers[dir]=marked;}return marked;}
  return fresh?IsMarkerDirectory(dir):markers.GetOrAdd(dir,IsMarkerDirectory);
 }
 static bool IsMarkerDirectory(string dir)
 {
  try{
   if(new[]{".git",".svn","node_modules","venv",".venv","plugins","steamapps"}.Any(x=>Directory.Exists(Path.Combine(dir,x))))return true;
   if(new[]{"package.json","Cargo.toml","pyproject.toml","go.mod","CMakeLists.txt","steam_appid.txt","portable.ini"}.Any(x=>File.Exists(Path.Combine(dir,x))))return true;
   return Directory.EnumerateFiles(dir).Any(x=>Path.GetExtension(x).ToLowerInvariant() is ".exe" or ".dll" or ".sln" or ".csproj");
  }catch(IOException){return true;}catch(UnauthorizedAccessException){return true;}
 }
}
