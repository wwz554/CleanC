using CleanC.Core;
using CleanC.Cleaner;
using System.Threading.Channels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
namespace CleanC.App;
public sealed partial class MainWindow
{
 int resultsPage;SafetyLevel resultsKind=SafetyLevel.Safe;
 readonly object cleanupCacheLock=new();
 readonly SemaphoreSlim selectionWriteGate=new(1,1);
 Dictionary<SafetyLevel,List<ScanItem>> cleanupGroups=new();
 Dictionary<SafetyLevel,List<CleanupFolderGroup>> cleanupFolderGroups=new();
 Dictionary<long,ScanItem> cleanupById=new();
 HashSet<long> cleanupSelectedIds=new();
 readonly HashSet<string> cleanupExpandedFolders=new(StringComparer.OrdinalIgnoreCase);
 readonly Dictionary<string,int> cleanupFolderChildPages=new(StringComparer.OrdinalIgnoreCase);
 long cleanupSelectedBytes;int cleanupSelectedCount;
 RecycleBinInfo recycleBinInfo;bool recycleBinSelected,recycleBinLoading;Task<RecycleBinInfo>? recycleBinLoadTask;
 bool cleanupCacheReady;Task? cleanupCacheTask;CancellationTokenSource? cleanupCacheCancellation;int cleanupCacheGeneration;string? cleanupCacheError;
 TextBlock? cleanupSelectedBytesText,cleanupSelectedCountText;Button? cleanupCleanButton;UIElement? scanProgressView,cleanupProgressView;
 ProgressBar? scanBar,cleanupBar;TextBlock? scanPercentText,scanDetailText,scanStatusText,cleanupPercentText,cleanupDetailText,cleanupStatusText;
 double scanPercentValue,cleanupPercentValue;string scanDetail="正在准备扫描…",scanStatus="正在扫描 C 盘…",cleanupDetail="正在准备清理…",cleanupStatus="正在安全清理";
 const int CleanupFolderPageSize=24,CleanupChildPageSize=20;
 bool cleanupPreparing;
 UIElement? cleanupResultView;

 sealed class CleanupFolderGroup
 {
  public required string Path{get;init;}
  public required List<ScanItem> Items{get;init;}
  long? cachedBytes;Dictionary<SafetyLevel,(int Count,long Bytes)>? cachedByLevel;
  public string Name{get{var n=System.IO.Path.GetFileName(Path.TrimEnd('\\'));return string.IsNullOrWhiteSpace(n)?Path:n;}}
  public long Bytes=>cachedBytes??=Items.Sum(x=>x.File.Size);
  void EnsureStats()
  {
   if(cachedByLevel is not null)return;var d=new Dictionary<SafetyLevel,(int Count,long Bytes)>();
   foreach(var level in Enum.GetValues<SafetyLevel>())d[level]=(0,0);
   foreach(var item in Items){var level=item.Classification.Safety;var x=d[level];d[level]=(x.Count+1,x.Bytes+item.File.Size);}
   cachedByLevel=d;
  }
  public int CountFor(SafetyLevel level){EnsureStats();return cachedByLevel![level].Count;}
  public long BytesFor(SafetyLevel level){EnsureStats();return cachedByLevel![level].Bytes;}
 }
 sealed class CleanupCacheSnapshot
 {
  public Dictionary<SafetyLevel,List<ScanItem>> Groups{get;}=new();
  public Dictionary<SafetyLevel,List<CleanupFolderGroup>> FolderGroups{get;}=new();
  public Dictionary<long,ScanItem> ById{get;}=new();
  public HashSet<long> SelectedIds{get;}=new();
  public long SelectedBytes{get;set;}
  public int SelectedCount=>SelectedIds.Count;
  public RecycleBinInfo RecycleBin{get;set;}
 }
 sealed class CleanupPreflight
 {
  public List<ScanItem> Ready{get;}=[];
  public List<(long Id,ScanItem? Item)> CacheChanges{get;}=[];
  public int RemovedMissing{get;set;}
  public int Reclassified{get;set;}
 }


 static double EstimateScanPercent(ScanProgress p)
 {
  var work=Math.Max(0d,p.Files)+Math.Max(0d,p.Directories)*12d;
  return Math.Clamp(4d+92d*(1d-Math.Exp(-work/180000d)),4d,96d);
 }
 string CleanupGroupPath(ScanItem item)
 {
  // UI grouping follows the real parent directory. This keeps mixed cache/user/protected
  // files in one place so the user can review the folder as it actually exists on disk.
  return System.IO.Path.GetDirectoryName(item.File.Path)??vm.ScanRoot;
 }

 Task? InvalidateCleanupCache()
 {
  Task? previous;lock(cleanupCacheLock){previous=cleanupCacheTask;cleanupCacheCancellation?.Cancel();cleanupCacheCancellation?.Dispose();cleanupCacheCancellation=null;cleanupCacheGeneration++;cleanupCacheReady=false;cleanupCacheTask=null;cleanupCacheError=null;cleanupGroups=new();cleanupFolderGroups=new();cleanupById=new();cleanupSelectedIds=new();cleanupSelectedBytes=0;cleanupSelectedCount=0;recycleBinInfo=default;recycleBinSelected=false;recycleBinLoading=false;recycleBinLoadTask=null;cleanupExpandedFolders.Clear();cleanupFolderChildPages.Clear();}
  ClearSpaceCache();InvalidateOverviewCache();return previous;
 }
 Task WaitForCurrentCleanupCacheAsync()
 {
  lock(cleanupCacheLock)
  {
   if(cleanupCacheReady)return Task.CompletedTask;
   return cleanupCacheTask??Task.CompletedTask;
  }
 }
 bool CleanupCacheBuildInProgress
 {
  get{lock(cleanupCacheLock)return !cleanupCacheReady&&cleanupCacheTask is not null&&!cleanupCacheTask.IsCompleted;}
 }
 CleanupCacheSnapshot BuildCleanupSnapshot(IEnumerable<ScanItem> source,CancellationToken token)
 {
  var unique=new Dictionary<string,ScanItem>(StringComparer.OrdinalIgnoreCase);
  foreach(var item in source)
  {
   token.ThrowIfCancellationRequested();var key=item.File.Path;
   if(!unique.TryGetValue(key,out var existing)||item.Classification.Safety>existing.Classification.Safety||(item.Classification.Safety==existing.Classification.Safety&&item.Id>existing.Id))unique[key]=item;
  }
  return BuildCleanupSnapshotFromUnique(unique.Values,token);
 }
 CleanupCacheSnapshot BuildCleanupSnapshotFromUnique(IEnumerable<ScanItem> uniqueItems,CancellationToken token)
 {
  var snap=new CleanupCacheSnapshot();foreach(var level in Enum.GetValues<SafetyLevel>()){snap.Groups[level]=new List<ScanItem>();snap.FolderGroups[level]=new List<CleanupFolderGroup>();}
  var folderMap=new Dictionary<string,List<ScanItem>>(StringComparer.OrdinalIgnoreCase);
  foreach(var item in uniqueItems)
  {
   token.ThrowIfCancellationRequested();snap.Groups[item.Classification.Safety].Add(item);snap.ById[item.Id]=item;
   if(item.Selected&&item.Classification.Safety!=SafetyLevel.Protected&&snap.SelectedIds.Add(item.Id))snap.SelectedBytes+=item.File.Size;
   var folder=CleanupGroupPath(item);if(!folderMap.TryGetValue(folder,out var list)){list=[];folderMap[folder]=list;}list.Add(item);
  }
  var allFolders=folderMap.Select(g=>new CleanupFolderGroup{Path=g.Key,Items=g.Value
    .OrderBy(x=>x.Classification.Safety switch{SafetyLevel.UserData=>0,SafetyLevel.Optional=>1,SafetyLevel.Protected=>2,_=>3})
    .ThenByDescending(x=>x.File.Size).ThenBy(x=>x.File.Path,StringComparer.OrdinalIgnoreCase).ToList()}).ToList();
  foreach(var level in Enum.GetValues<SafetyLevel>())
   snap.FolderGroups[level]=allFolders.Where(g=>g.Items.Any(x=>x.Classification.Safety==level))
    .OrderByDescending(g=>level==SafetyLevel.Safe&&SafetyPolicy.Within(g.Path,AppPaths.DriverBackups)?2:level==SafetyLevel.Safe&&SafetyPolicy.Within(g.Path,AppPaths.DriverPackages)?1:0).ThenByDescending(g=>g.BytesFor(level)).ThenBy(g=>g.Path,StringComparer.OrdinalIgnoreCase).ToList();
  return snap;
 }

 void PublishCleanupSnapshot(int generation,CleanupCacheSnapshot snapshot)
 {
  lock(cleanupCacheLock)
  {
   if(generation!=cleanupCacheGeneration)return;
   cleanupGroups=snapshot.Groups;cleanupFolderGroups=snapshot.FolderGroups;cleanupById=snapshot.ById;cleanupCacheError=null;cleanupSelectedIds=snapshot.SelectedIds;cleanupSelectedBytes=snapshot.SelectedBytes;cleanupSelectedCount=snapshot.SelectedCount;recycleBinInfo=default;recycleBinSelected=false;recycleBinLoading=true;cleanupCacheReady=true;cleanupCacheTask=null;cleanupCacheCancellation?.Dispose();cleanupCacheCancellation=null;
  }
  services.Log.Write("Cleanup","UiCache","Ready",detail:$"{snapshot.ById.Count} items");
  StartRecycleBinRefresh(generation);
 }
 (ChannelWriter<(long First,long Last)> Writer,Task Task) StartScanCachePipeline()
 {
  int generation;CancellationToken token;
  lock(cleanupCacheLock)
  {
   generation=cleanupCacheGeneration;cleanupCacheCancellation=new();token=cleanupCacheCancellation.Token;
  }
  var channel=Channel.CreateUnbounded<(long First,long Last)>(new UnboundedChannelOptions{SingleReader=true,SingleWriter=true,AllowSynchronousContinuations=false});
  var task=BuildCleanupCacheFromCommittedRangesAsync(generation,channel.Reader,token);
  lock(cleanupCacheLock){if(generation==cleanupCacheGeneration)cleanupCacheTask=task;}
  return(channel.Writer,task);
 }
 async Task BuildCleanupCacheFromCommittedRangesAsync(int generation,ChannelReader<(long First,long Last)> reader,CancellationToken token)
 {
  Interlocked.Increment(ref transientDatabaseReaders);
  try
  {
   var unique=await Task.Run(async()=>
   {
    var byPath=new Dictionary<string,ScanItem>(StringComparer.OrdinalIgnoreCase);
    await foreach(var range in reader.ReadAllAsync(token))
    {
     token.ThrowIfCancellationRequested();
     var batch=services.Database.ItemsRange(range.First,range.Last,token);
     foreach(var item in batch)
     {
      var key=item.File.Path;
      if(!byPath.TryGetValue(key,out var existing)||item.Classification.Safety>existing.Classification.Safety||(item.Classification.Safety==existing.Classification.Safety&&item.Id>existing.Id))byPath[key]=item;
     }
    }
    return byPath;
   },token);
   var snapshot=await Task.Run(()=>BuildCleanupSnapshotFromUnique(unique.Values,token),token);
   PublishCleanupSnapshot(generation,snapshot);
  }
  catch(Exception e)
  {
   lock(cleanupCacheLock)
   {
    if(generation==cleanupCacheGeneration)
    {
     cleanupCacheTask=null;cleanupCacheCancellation?.Dispose();cleanupCacheCancellation=null;
     if(e is not OperationCanceledException)cleanupCacheError=e.Message;
    }
   }
   if(e is OperationCanceledException)return;
   services.Log.Write("Cleanup","ScanPipelineCache","Failed",detail:e.ToString());
  }
  finally
  {
   Interlocked.Decrement(ref transientDatabaseReaders);TryFinishPendingClose();
  }
 }

 void StartRecycleBinRefresh(int generation)
 {
  lock(cleanupCacheLock)
  {
   if(recycleBinLoadTask is not null)return;
   recycleBinLoading=true;
   recycleBinLoadTask=Task.Run(()=>services.RecycleBin.QueryAll());
  }
  _=FinishRecycleBinRefreshAsync(generation);
 }
 async Task FinishRecycleBinRefreshAsync(int generation)
 {
  Task<RecycleBinInfo>? task;lock(cleanupCacheLock)task=recycleBinLoadTask;
  if(task is null)return;
  try
  {
   var info=await task;
   lock(cleanupCacheLock)
   {
    if(generation!=cleanupCacheGeneration)return;
    recycleBinInfo=info;recycleBinLoading=false;recycleBinLoadTask=null;
   }
   if(currentPage=="clean"&&!scanRunning&&!cleanupRunning&&!cleanupFinalizing)await TransitionContentAsync(()=>ShowCleanup(),true,true);
  }
  catch(Exception e)
  {
   lock(cleanupCacheLock){if(generation==cleanupCacheGeneration){recycleBinLoading=false;recycleBinLoadTask=null;}}
   services.Log.Write("Cleanup","RecycleBinUiQuery","Failed",detail:e.Message);
  }
 }

 void ApplyRecycleBinCleanupResult(RecycleBinCleanupResult result)
 {
  lock(cleanupCacheLock)
  {
   // Recycle Bin is intentionally outside cleanupById. Update only this row from
   // the post-clean verification already performed by RecycleBinService; never
   // rebuild the Smart Clean generation just because the bin changed.
   recycleBinInfo=result.FinalState;
   recycleBinLoading=false;
   recycleBinLoadTask=null;
   if(!recycleBinInfo.QueryComplete||recycleBinInfo.Items<=0)recycleBinSelected=false;
   else if(result.Success)recycleBinSelected=false;
   // Partial cleanup with a fully known remainder keeps the user's selection so
   // they can explicitly retry the remaining items without a cache rebuild.
  }
  services.Log.Write("Cleanup","RecycleBinCache","Updated",detail:$"success={result.Success}; items={recycleBinInfo.Items}; bytes={recycleBinInfo.Bytes}; complete={recycleBinInfo.QueryComplete}");
  UpdateSelectionSummaryVisual();
 }

 void ShowCacheLoading()
 {
  string? error;bool building;
  lock(cleanupCacheLock){error=cleanupCacheError;building=!cleanupCacheReady&&cleanupCacheTask is not null&&!cleanupCacheTask.IsCompleted;}
  if(!string.IsNullOrWhiteSpace(error)||!building)
  {
   var detail=string.IsNullOrWhiteSpace(error)?"本次扫描对应的智能缓存不可用。CleanC 不会从数据库偷偷重建另一份缓存；请重新扫描 C 盘创建新一代智能缓存。":"智能缓存构建失败："+error;
   pageHost.Content=Ui.Stack(24,Heading("SMART CLEAN","智能缓存不可用","智能缓存只在开始 C 盘扫描时同步创建；不会因为隐藏/重新打开 UI 而重新构建。"),Ui.GlassCard(Ui.Stack(14,Ui.T("需要重新扫描",20,true),Ui.T(detail,12,false,Ui.Muted),Ui.Button("重新扫描",()=>_=Guard(StartScan),true)),new Thickness(24)));
   return;
  }
  var glass=Ui.GlassCard(Ui.Stack(12,Ui.T("C 盘扫描已经完成",20,true),Ui.T("C 盘扫描已经结束，扫描期间智能缓存也一直在同步构建。当前只剩最后的缓存收尾，完成后会自动进入智能清理界面。",12,false,Ui.Muted)),new Thickness(24));
  pageHost.Content=Ui.Stack(24,Heading("SMART CLEAN","扫描完成 · 正在构建智能缓存","缓存与扫描已经并行完成大部分工作；正在合并最后一批已提交扫描结果。"),glass);
 }

 void ShowScanProgressPage()
 {
  if(scanProgressView is not null){pageHost.Content=scanProgressView;UpdateScanVisual();return;}
  scanStatusText=Ui.T(scanStatus,28,true);scanPercentText=Ui.T($"{scanPercentValue:0}%",13,true,Ui.Accent);scanDetailText=Ui.T(scanDetail,12,false,Ui.Muted);
  scanBar=new ProgressBar{Minimum=0,Maximum=100,Value=scanPercentValue,Height=8,HorizontalAlignment=HorizontalAlignment.Stretch};
  var stop=Ui.Button("停止扫描",()=>scanCancellation?.Cancel());
  scanProgressView=Ui.Stack(24,Heading("READ ONLY SCAN","正在分析 C 盘","扫描在后台线程执行，你可以同时切换页面或进行系统检查。"),Ui.GlassCard(Ui.Stack(16,scanStatusText,scanBar,Ui.Row(10,scanPercentText,scanDetailText),stop)));
  pageHost.Content=scanProgressView;
 }
 void UpdateScanVisual()
 {
  if(scanBar is not null)scanBar.Value=scanPercentValue;if(scanPercentText is not null)scanPercentText.Text=$"{scanPercentValue:0}%";if(scanDetailText is not null)scanDetailText.Text=scanDetail;if(scanStatusText is not null)scanStatusText.Text=scanStatus;
 }

 async Task StartScan()
 {
  if(scanRunning){SetStatus("扫描已经在后台运行。");return;}
  if(ComponentWorkRunning){await Notice("组件维护正在运行","请等待 Windows 组件分析或清理完成后重新扫描。");return;}
  if(cleanupPreparing||cleanupRunning||cleanupFinalizing){await Notice("清理正在进行","清理或清理后的安全整理仍在后台进行，请稍后再重新扫描，避免扫描数据库同时被修改。");return;}
  services.License.Context.Demand(FeatureCapability.Scan);
  cleanupResultView=null;var previousCache=InvalidateCleanupCache();scanRunning=true;scanCancellation=new();scanPercentValue=0;scanDetail="正在准备扫描…";scanStatus="正在扫描 C 盘…";scanProgressView=null;resultsKind=SafetyLevel.Safe;resultsPage=0;
  ChannelWriter<(long First,long Last)>? cacheWriter=null;Task? scanCacheTask=null;Task? componentScanTask=null;
  try
  {
   currentPage="clean";UpdateNavigationSelection(true);await TransitionContentAsync(()=>ShowScanProgressPage(),false,false);
  }
  catch
  {
   scanRunning=false;scanCancellation?.Dispose();scanCancellation=null;scanProgressView=null;InvalidateOverviewCache();
   throw;
  }
  services.Log.Write("Scanner","UiProgressReady","Completed",vm.ScanRoot);services.Log.Write("Scanner","UiStart","Started",vm.ScanRoot);await Task.Yield();
  if(previousCache is not null){scanDetail="正在释放上一次结果缓存…";UpdateScanVisual();try{await previousCache;}catch(OperationCanceledException){}catch(Exception e){services.Log.Write("Cleanup","UiCacheStop","Ignored",detail:e.GetType().Name);}}
  double shown=0;
  try
  {
   componentScanTask=AnalyzeComponentsDuringScan();
   var pipeline=StartScanCachePipeline();cacheWriter=pipeline.Writer;scanCacheTask=pipeline.Task;var activeCacheWriter=pipeline.Writer;
   using var progress=new DispatcherProgress<ScanProgress>(DispatcherQueue,p=>{
    var next=Math.Max(shown,EstimateScanPercent(p));shown=next;scanPercentValue=next;scanDetail=$"已扫描 {p.Files:N0} 个文件 · {p.Directories:N0} 个目录 · 跳过 {p.Skipped:N0} 项 · 智能缓存同步构建中";UpdateScanVisual();
   },e=>services.Log.Write("Scanner","UiProgress","Failed",detail:e.ToString()));
   var completedScan=await services.Scanner.ScanAsync(vm.ScanRoot,progress,scanCancellation.Token,services.Preferences.QuietScan,(first,last)=>activeCacheWriter.TryWrite((first,last)));
   cacheWriter.TryComplete();cacheWriter=null;
   services.Log.Write("Scanner","UiAwait","Completed",vm.ScanRoot,$"{completedScan.Files} files; {completedScan.Skipped} skipped; canceled={completedScan.Canceled}");

   if(completedScan.Canceled)
   {
    vm.LastScan=null;scanStatus="扫描已停止";scanDetail=$"已读取 {completedScan.Files:N0} 个文件 · 本次结果未完成，已丢弃临时扫描索引";UpdateScanVisual();
    cleanupCacheCancellation?.Cancel();
    try{if(scanCacheTask is not null)await scanCacheTask;}catch(OperationCanceledException){}
    try
    {
     services.Database.Reset();services.Log.Write("Scanner","DiscardIncompleteScan","Completed",vm.ScanRoot,$"{completedScan.Files} partial files discarded");
    }
    catch(Exception e){services.Log.Write("Scanner","DiscardIncompleteScan","Failed",services.Database.DatabasePath,e.ToString());}
    _=InvalidateCleanupCache();SetStatus("扫描已停止 · 本次扫描未完成，半成品结果不会用于清理或空间分析，请重新完整扫描。");
   }
   else
   {
    vm.LastScan=completedScan;scanPercentValue=97;scanStatus="文件扫描完成，正在收尾";scanDetail=$"{completedScan.Files:N0} 个文件 · {completedScan.Elapsed.TotalSeconds:0.0} 秒";UpdateScanVisual();
    // Keep ownership through cache publication and DISM analysis; a second scan must not reset this database.
    if(scanCacheTask is not null&&!scanCacheTask.IsCompleted)
    {
     SetStatus($"扫描完成 · {completedScan.Files:N0} 个文件 · 正在构建智能缓存…");
     if(currentPage=="clean")await TransitionContentAsync(()=>ShowCacheLoading(),false,true);
    }
    if(scanCacheTask is not null)await scanCacheTask;
    // Hidden shutdown deliberately cancels this display-only cache reader. Do not
    // immediately recreate it while the exit state machine is waiting to own CleanC.db.
    if(closingPending)return;
    if(!cleanupCacheReady)
    {
     lock(cleanupCacheLock){cleanupCacheTask=null;cleanupCacheError??="扫描对应的智能缓存没有完成发布。";}
     SetStatus("扫描完成，但本次智能缓存未能完成。请重新扫描；CleanC 不会从数据库重新构建另一份缓存。");
     if(currentPage=="clean")await TransitionContentAsync(()=>ShowCacheLoading(),false,true);
     return;
    }
    if(componentScanTask is {IsCompleted:false})
    {
     scanStatus="文件扫描完成 · 等待并行组件分析收尾";scanDetail="Windows 组件分析从扫描开始时已同步进行；普通文件已扫描完毕。";UpdateScanVisual();
     if(currentPage=="clean")await TransitionContentAsync(()=>ShowScanProgressPage(),false,true);
    }
    if(componentScanTask is not null)await componentScanTask;
    scanPercentValue=100;
    if(closingPending)return;
    scanRunning=false;
    SetStatus($"文件扫描完成 · {completedScan.Files:N0} 个文件 · {completedScan.Elapsed.TotalSeconds:0.0} 秒；"+componentStatus);
    if(!closingPending)
    {
     currentPage="clean";UpdateNavigationSelection(true);await TransitionContentAsync(()=>ShowCleanup(),false,true);
     if(currentPage=="space"){await WarmSpaceRootAsync();if(currentPage=="space")await TransitionContentAsync(()=>ShowSpace(vm.ScanRoot),false,true);}else _=WarmSpaceRootAsync();
    }
   }
  }
  catch(Exception e)
  {
   cacheWriter?.TryComplete(e);cleanupCacheCancellation?.Cancel();
   if(scanCacheTask is not null){try{await scanCacheTask;}catch{}}
   services.Log.Write("Scanner","UiAwait","Failed",vm.ScanRoot,e.ToString());App.WriteCrashLog("Scan.UiAwait",e);throw;
  }
  finally
  {
   cacheWriter?.TryComplete();if(componentScanTask is not null)await componentScanTask;scanRunning=false;scanCancellation?.Dispose();scanCancellation=null;scanBar=null;scanPercentText=null;scanDetailText=null;scanStatusText=null;scanProgressView=null;InvalidateOverviewCache();
   if(!closingPending)
   {
    if(vm.LastScan is null)ClearSpaceCache();
    if(currentPage=="overview")await TransitionContentAsync(()=>ShowOverview(),false,true);
    else if(currentPage=="clean"&&!cleanupCacheReady&&vm.LastScan is not null)await TransitionContentAsync(()=>ShowCacheLoading(),false,true);
    else if(currentPage=="space"&&vm.LastScan is null)await TransitionContentAsync(()=>ShowSpace(vm.ScanRoot),false,true);
   }
   if(closingPending)TryFinishPendingClose();
  }
 }

 async void SwitchCleanupKind(SafetyLevel kind)
 {
  if(resultsKind==kind)return;resultsKind=kind;resultsPage=0;
  if(currentPage=="clean"&&!scanRunning&&!cleanupRunning)await TransitionContentAsync(()=>ShowCleanup(),true,true);
 }
 async void SwitchCleanupPage(int page)
 {
  if(resultsPage==page)return;resultsPage=Math.Max(0,page);
  if(currentPage=="clean"&&!scanRunning&&!cleanupRunning)await TransitionContentAsync(()=>ShowCleanup(),true,true);
 }

 void ShowCleanup()
 {
  if(scanRunning){ShowScanProgressPage();return;}
  if(cleanupRunning){ShowCleanupProgressPage();return;}
  if(cleanupPreparing){pageHost.Content=Ui.Stack(20,Heading("SMART CLEAN","正在复核所选项目","清理前检查路径、文件变化与保护规则；此时尚未删除文件。"),new ProgressBar{IsIndeterminate=true});return;}
  if(cleanupResultView is not null){pageHost.Content=cleanupResultView;return;}
  if(cleanupFinalizing){pageHost.Content=Ui.Stack(24,Heading("SMART CLEAN","清理已经完成","正在后台更新清理结果与智能缓存；无需等待，可以切换到其他页面。"),Ui.GlassCard(Ui.Stack(12,Ui.T("正在后台整理最新结果…",20,true),Ui.T("删除与安全复核已经结束。这里只是在更新列表和缓存，不会继续删除文件。",12,false,Ui.Muted)),new Thickness(24)));return;}
  if(vm.LastScan is null){pageHost.Content=Ui.Stack(24,Heading("SMART CLEAN","智能清理","先扫描，再按真实文件夹查看。推荐安全项默认勾选，但任何可删除文件都由你最终决定。"),Ui.Card(Ui.Stack(24,Ui.Icon("\uE74D",40),Ui.T("按文件夹整理，一眼看清缓存、个人文件和受保护内容。",22,true),Ui.T("安全项默认勾选，可随时取消；可选和用户数据默认不勾选；受保护内容永远不能删除。",14,false,Ui.Muted),Ui.Button("开始扫描",()=>_=Guard(StartScan),true))));return;}
  lock(cleanupCacheLock){if(!cleanupCacheReady){ShowCacheLoading();return;}}

  long selected;int selectedCount;List<CleanupFolderGroup> folders;int totalPages;
  lock(cleanupCacheLock)
  {
   selected=cleanupSelectedBytes+(recycleBinSelected?recycleBinInfo.Bytes:0);selectedCount=cleanupSelectedCount+(recycleBinSelected?1:0)+(componentSelected?1:0);
   folders=cleanupFolderGroups.TryGetValue(resultsKind,out var g)?g:new List<CleanupFolderGroup>();
   totalPages=Math.Max(1,(int)Math.Ceiling(folders.Count/(double)CleanupFolderPageSize));if(resultsPage>=totalPages)resultsPage=totalPages-1;
   folders=folders.Skip(resultsPage*CleanupFolderPageSize).Take(CleanupFolderPageSize).ToList();
  }

  cleanupSelectedBytesText=Ui.T(Display.Bytes(selected),40,true,Ui.Accent);cleanupSelectedCountText=Ui.T($"{selectedCount:N0} 项已选 · 组件清理释放量另计，可随时取消",12,false,Ui.Muted);
  cleanupCleanButton=Ui.Button("开始清理   "+Display.Bytes(selected),()=>{},true);cleanupCleanButton.Click+=async(_,_)=>await Guard(()=>RunCleanup(false));
  var stats=Ui.Columns(-1,-1);Ui.Add(stats,Ui.Stack(4,cleanupSelectedBytesText,cleanupSelectedCountText),0);
  var actions=Ui.Stack(12,cleanupCleanButton,Ui.Row(8,Ui.Button("预演",()=>_=Guard(()=>RunCleanup(true))),Ui.Button("重新扫描",()=>_=Guard(StartScan))));actions.HorizontalAlignment=HorizontalAlignment.Right;Ui.Add(stats,actions,1);
  var filters=Ui.Row(8);foreach(var (label,kind) in new[]{("安全清理",SafetyLevel.Safe),("可选 / 暂不清理",SafetyLevel.Optional),("用户数据",SafetyLevel.UserData),("受保护",SafetyLevel.Protected)})filters.Children.Add(Ui.Button(label,()=>SwitchCleanupKind(kind),resultsKind==kind));

  var folderStack=Ui.Stack(10);
  if(resultsKind==SafetyLevel.Safe&&resultsPage==0)folderStack.Children.Add(BuildComponentStoreRow());
  foreach(var folder in folders)folderStack.Children.Add(BuildCleanupFolderCard(folder));
  if(folders.Count==0)folderStack.Children.Add(Ui.GlassCard(Ui.Stack(8,Ui.T("这个分类暂时没有项目。",16,true),Ui.T("重新扫描后会按最新文件状态重新整理。",12,false,Ui.Muted)),new Thickness(18)));
  var folderScroll=new ScrollViewer{Content=folderStack,Height=410,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
  var content=Ui.Stack(10);
  if(resultsKind==SafetyLevel.Optional)content.Children.Add(BuildRecycleBinRow());
  content.Children.Add(folderScroll);
  var pages=Ui.Columns(-1,-1);var pageNav=Ui.Row(8,Ui.Button("上一页",()=>{if(resultsPage>0)SwitchCleanupPage(resultsPage-1);}),Ui.T($"文件夹页 {resultsPage+1} / {totalPages}",12,false,Ui.Muted),Ui.Button("下一页",()=>{if(resultsPage+1<totalPages)SwitchCleanupPage(resultsPage+1);}));var selectActions=Ui.Row(8,Ui.Button("全选当前分类",()=>SetCurrentKindSelectionCached(true)),Ui.Button("取消当前分类",()=>SetCurrentKindSelectionCached(false)),Ui.Button("全部取消",()=>ClearSelectionsCached()),Ui.Button("恢复推荐",()=>RestoreRecommendedSelectionsCached()));selectActions.HorizontalAlignment=HorizontalAlignment.Right;Ui.Add(pages,pageNav,0);Ui.Add(pages,selectActions,1);
  var subtitle=resultsKind switch
  {
   SafetyLevel.Optional=>"默认不勾选。最近 7 天的浏览器/应用缓存、会马上重建的缓存索引、缩略图缓存和需要人工确认的项目都放这里；回收站仍固定在第一行。",
   SafetyLevel.UserData=>"照片、视频、文档等默认不勾选；即使出现在缓存目录也不会当垃圾自动删除。",
   SafetyLevel.Protected=>"系统、应用程序本体、配置/状态、恢复数据、便携程序和无法证明安全的文件只能查看，不能选择删除。",
   _=>"只推荐明确且稳定的旧缓存：浏览器/普通应用缓存需超过 7 天，着色器缓存需超过 30 天；最近常用缓存不会默认清理。"
  };
  pageHost.Content=Ui.Stack(20,Heading("SMART CLEAN",vm.LastScan.Canceled?"扫描已停止":"扫描完成",subtitle),Ui.Card(stats),filters,Ui.Card(Ui.Stack(10,content,pages),new Thickness(16)));
 }

 Border BuildRecycleBinRow()
 {
  var box=new CheckBox{IsChecked=recycleBinSelected,VerticalAlignment=VerticalAlignment.Center,IsEnabled=!recycleBinLoading&&recycleBinInfo.QueryComplete&&recycleBinInfo.Items>0};
  box.Click+=(_,_)=>{recycleBinSelected=box.IsChecked==true;UpdateSelectionSummaryVisual();};
  var row=Ui.Columns(40,-1,120);Ui.Add(row,box,0);
  var title=Ui.T("回收站",14,true);
  var driveSummary=recycleBinLoading?"正在后台查询所有本地磁盘回收站…":recycleBinInfo.DriveItems.Count==0
   ?"所有本地磁盘"
   :string.Join(" · ",recycleBinInfo.DriveItems.Select(x=>$"{x.Root.TrimEnd('\\')} {x.Items:N0} 项"));
  var recycleDetail=recycleBinLoading?driveSummary:!recycleBinInfo.QueryComplete
   ?$"{driveSummary} · 已确认 {recycleBinInfo.Items:N0} 项，但至少一个磁盘状态无法确认；当前禁止清空，避免把未知状态当成空"
   :$"{driveSummary} · 共 {recycleBinInfo.Items:N0} 项 · 勾选后清空当前用户所有本地磁盘回收站";
  var detail=Ui.T(recycleDetail,11,false,Ui.Muted);
  Ui.Add(row,Ui.Stack(4,title,detail),1);var size=Ui.T(Display.Bytes(recycleBinInfo.Bytes),13,true);size.HorizontalAlignment=HorizontalAlignment.Right;size.VerticalAlignment=VerticalAlignment.Center;Ui.Add(row,size,2);
  return Ui.GlassCard(row,new Thickness(16,12,16,12));
 }

 Border CleanupRiskChip(string text,SafetyLevel level)
 {
  var color=level switch{SafetyLevel.Safe=>Ui.Color("219653"),SafetyLevel.Optional=>Ui.Color("B7791F"),SafetyLevel.UserData=>Ui.Color("C05621"),_=>Ui.Color(Ui.Dark?"A5B0BF":"687386")};
  var label=Ui.T(text,10.5,true,new SolidColorBrush(color));label.TextWrapping=TextWrapping.NoWrap;label.HorizontalAlignment=HorizontalAlignment.Center;label.VerticalAlignment=VerticalAlignment.Center;label.TextAlignment=TextAlignment.Center;
  return new Border{Child=label,Padding=new Thickness(8,3,8,3),CornerRadius=new CornerRadius(10),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.Dark?(byte)34:(byte)24,color.R,color.G,color.B)),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,MinWidth=0};
 }
 Border CleanupInfoChip(string text,bool selected=false)
 {
  var color=selected?Ui.Color("219653"):Ui.Color(Ui.Dark?"AAB5C4":"67768A");
  var label=Ui.T(text,10.5,selected,new SolidColorBrush(color));label.TextWrapping=TextWrapping.NoWrap;label.HorizontalAlignment=HorizontalAlignment.Center;label.VerticalAlignment=VerticalAlignment.Center;label.TextAlignment=TextAlignment.Center;
  return new Border{Child=label,Padding=new Thickness(8,3,8,3),CornerRadius=new CornerRadius(10),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.Dark?(byte)30:(byte)20,color.R,color.G,color.B)),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,MinWidth=0};
 }
 Border SelectionMark(bool all,bool partial)
 {
  var green=Ui.Color("20A464");var neutral=Ui.Color(Ui.Dark?"738095":"AAB5C3");var edge=all?green:neutral;
  var frame=new Border{Width=22,Height=22,CornerRadius=new CornerRadius(5),BorderThickness=new Thickness(1.5),BorderBrush=new SolidColorBrush(edge),Background=all?new SolidColorBrush(green):new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
  if(all){var check=Ui.T("✓",13,true,Ui.B("FFFFFF"));check.HorizontalAlignment=HorizontalAlignment.Center;check.VerticalAlignment=VerticalAlignment.Center;frame.Child=check;}
  else if(partial)frame.Child=new Border{Width=9,Height=9,Margin=new Thickness(5),CornerRadius=new CornerRadius(2),Background=new SolidColorBrush(green),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
  return frame;
 }
 Button SelectionButton(bool all,bool partial,Action action,string tooltip)
 {
  var b=new Button{Content=SelectionMark(all,partial),Width=34,Height=34,Padding=new Thickness(0),CornerRadius=new CornerRadius(9),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)),BorderThickness=new Thickness(0),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
  var hover=Ui.NavigationHoverBrush();b.PointerEntered+=(_,_)=>b.Background=hover;b.PointerExited+=(_,_)=>b.Background=new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0));
  ToolTipService.SetToolTip(b,tooltip);b.Click+=(_,_)=>action();return b;
 }

 Border BuildCleanupFolderCard(CleanupFolderGroup folder)
 {
  var key=$"{resultsKind}|{folder.Path}";var expanded=cleanupExpandedFolders.Contains(key);var child=Ui.Stack(8);child.Visibility=expanded?Visibility.Visible:Visibility.Collapsed;
  var currentItems=folder.Items.Where(x=>x.Classification.Safety==resultsKind).ToList();var selectableItems=currentItems.Where(x=>x.Classification.Safety!=SafetyLevel.Protected).ToList();Button? folderBox=null;Button? openButton=null;
  (int selected,int total) FolderSelectionState(){lock(cleanupCacheLock)return(selectableItems.Count(x=>cleanupSelectedIds.Contains(x.Id)),selectableItems.Count);}
  void RefreshFolderState()
  {
   if(folderBox is null)return;var state=FolderSelectionState();folderBox.Content=SelectionMark(state.total>0&&state.selected==state.total,state.selected>0&&state.selected<state.total);
   ToolTipService.SetToolTip(folderBox,state.selected==state.total&&state.total>0?"取消这个文件夹中当前分类的全部选择":"选择这个文件夹中当前分类的全部项目");
  }
  void RefreshOpenItems()
  {
   RefreshFolderState();
   if(child.Visibility==Visibility.Visible)DispatcherQueue.TryEnqueue(()=>{var page=cleanupFolderChildPages.TryGetValue(key,out var n)?n:0;RenderCleanupFolderItems(folder,child,page,RefreshOpenItems);});
  }
  if(resultsKind!=SafetyLevel.Protected)
  {
   var state=FolderSelectionState();folderBox=SelectionButton(state.total>0&&state.selected==state.total,state.selected>0&&state.selected<state.total,()=>
   {
    var now=FolderSelectionState();var value=now.total>0&&now.selected<now.total;ApplySelectionsLocal(selectableItems,value);_=PersistSelectionsAsync(selectableItems,value);RefreshOpenItems();
   },"只选择/取消当前分类中的文件；其他分类不会联动");
  }

  var safe=folder.CountFor(SafetyLevel.Safe);var optional=folder.CountFor(SafetyLevel.Optional);var user=folder.CountFor(SafetyLevel.UserData);var protectedCount=folder.CountFor(SafetyLevel.Protected);
  int selectedInFolder;lock(cleanupCacheLock)selectedInFolder=currentItems.Count(x=>cleanupSelectedIds.Contains(x.Id));
  var title=Ui.T(folder.Name,14.5,true);var path=Ui.T(folder.Path,10.5,false,Ui.Muted);path.TextTrimming=TextTrimming.CharacterEllipsis;path.TextWrapping=TextWrapping.NoWrap;ToolTipService.SetToolTip(path,folder.Path);
  var chips=Ui.Row(6);
  chips.Children.Add(CleanupRiskChip($"{RiskLabel(resultsKind)} {currentItems.Count:N0}",resultsKind));
  if(resultsKind!=SafetyLevel.Protected&&selectedInFolder>0)chips.Children.Add(CleanupInfoChip($"已选 {selectedInFolder:N0}",true));
  var otherCount=folder.Items.Count-currentItems.Count;
  if(otherCount>0)chips.Children.Add(CleanupInfoChip($"同目录其他分类 {otherCount:N0}"));
  var meta=Ui.Stack(4,title,chips,path);
  var size=Ui.T(Display.Bytes(folder.BytesFor(resultsKind)),12.5,true);size.HorizontalAlignment=HorizontalAlignment.Right;size.VerticalAlignment=VerticalAlignment.Center;ToolTipService.SetToolTip(size,"当前分类在此文件夹中的容量");

  void Toggle()
  {
   var open=child.Visibility!=Visibility.Visible;child.Visibility=open?Visibility.Visible:Visibility.Collapsed;
   if(openButton is not null)openButton.Content=open?"收起文件  ▴":"查看文件  ▾";
   if(open){cleanupExpandedFolders.Add(key);var page=cleanupFolderChildPages.TryGetValue(key,out var n)?n:0;RenderCleanupFolderItems(folder,child,page,RefreshOpenItems);}else cleanupExpandedFolders.Remove(key);
  }
  openButton=Ui.Button(expanded?"收起文件  ▴":"查看文件  ▾",Toggle,true);openButton.Height=36;openButton.Padding=new Thickness(14,0,14,0);openButton.CornerRadius=new CornerRadius(10);

  var header=new Grid{ColumnSpacing=12};header.ColumnDefinitions.Add(new(){Width=new GridLength(38)});header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  Ui.Add(header,resultsKind==SafetyLevel.Protected?Ui.Icon("\uE72E",15):folderBox!,0);Ui.Add(header,meta,1);Ui.Add(header,size,2);Ui.Add(header,openButton,3);
  if(expanded){var page=cleanupFolderChildPages.TryGetValue(key,out var n)?n:0;RenderCleanupFolderItems(folder,child,page,RefreshOpenItems);}
  return Ui.GlassCard(Ui.Stack(8,header,child),new Thickness(12,10,12,10));
 }

 static string RiskLabel(SafetyLevel level)=>level switch{SafetyLevel.Safe=>"安全",SafetyLevel.Optional=>"可选",SafetyLevel.UserData=>"用户文件",_=>"受保护"};

 void RenderCleanupFolderItems(CleanupFolderGroup folder,StackPanel panel,int page,Action? onSelectionChanged=null)
 {
  panel.Children.Clear();var key=$"{resultsKind}|{folder.Path}";HashSet<long> selected;lock(cleanupCacheLock)selected=cleanupSelectedIds.ToHashSet();
  var visible=folder.Items.Where(x=>x.Classification.Safety==resultsKind).ToList();
  var ordered=visible.OrderByDescending(x=>selected.Contains(x.Id)).ThenByDescending(x=>x.File.Size).ThenBy(x=>x.File.Path,StringComparer.OrdinalIgnoreCase).ToList();
  var totalPages=Math.Max(1,(int)Math.Ceiling(ordered.Count/(double)CleanupChildPageSize));page=Math.Clamp(page,0,totalPages-1);cleanupFolderChildPages[key]=page;
  if(resultsKind==SafetyLevel.UserData&&visible.Count>0)panel.Children.Add(Ui.GlassCard(Ui.Stack(3,Ui.T("这里仅显示当前分类的用户数据",11.5,true,Ui.Warning),Ui.T("同目录中的安全、可选和受保护文件不会在这里重复显示。",10,false,Ui.Muted)),new Thickness(10,8,10,8)));
  foreach(var item in ordered.Skip(page*CleanupChildPageSize).Take(CleanupChildPageSize))panel.Children.Add(BuildCleanupItemRow(item,onSelectionChanged));
  if(totalPages>1)panel.Children.Add(Ui.Row(8,Ui.Button("上一组",()=>RenderCleanupFolderItems(folder,panel,page-1,onSelectionChanged)),Ui.T($"文件 {page*CleanupChildPageSize+1:N0}-{Math.Min((page+1)*CleanupChildPageSize,ordered.Count):N0} / {ordered.Count:N0}",11,false,Ui.Muted),Ui.Button("下一组",()=>RenderCleanupFolderItems(folder,panel,page+1,onSelectionChanged))));
 }

 Border BuildCleanupItemRow(ScanItem item,Action? onSelectionChanged=null)
 {
  bool checkedNow;lock(cleanupCacheLock)checkedNow=cleanupSelectedIds.Contains(item.Id);
  var row=new Grid{ColumnSpacing=12};row.ColumnDefinitions.Add(new(){Width=new GridLength(38)});row.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});row.ColumnDefinitions.Add(new(){Width=GridLength.Auto});row.ColumnDefinitions.Add(new(){Width=new GridLength(92)});row.Padding=new Thickness(4,5,4,5);
  if(resultsKind!=SafetyLevel.Protected&&item.Classification.Safety!=SafetyLevel.Protected)
  {
   Button? box=null;box=SelectionButton(checkedNow,false,()=>
   {
    bool value;lock(cleanupCacheLock)value=!cleanupSelectedIds.Contains(item.Id);ApplySelectionLocal(item,value);if(box is not null)box.Content=SelectionMark(value,false);onSelectionChanged?.Invoke();_=PersistSelectionAsync(item,value);
   },checkedNow?"取消清理这个文件":"勾选清理这个文件");Ui.Add(row,box,0);
  }
  else Ui.Add(row,Ui.Icon("\uE72E",15),0);
  var name=Ui.T(Path.GetFileName(item.File.Path),12,true);name.TextTrimming=TextTrimming.CharacterEllipsis;name.TextWrapping=TextWrapping.NoWrap;
  var detail=Ui.T($"{FriendlyFileKind(item.File.Path)} · {item.Classification.Reason}",10,false,item.Classification.Safety==SafetyLevel.UserData?Ui.Warning:Ui.Muted);detail.TextTrimming=TextTrimming.CharacterEllipsis;detail.TextWrapping=TextWrapping.NoWrap;ToolTipService.SetToolTip(detail,item.File.Path+"\n"+item.Classification.Reason);
  Ui.Add(row,Ui.Stack(2,name,detail),1);Ui.Add(row,CleanupRiskChip(RiskLabel(item.Classification.Safety),item.Classification.Safety),2);var size=Ui.T(Display.Bytes(item.File.Size),11.5,true);size.HorizontalAlignment=HorizontalAlignment.Right;size.VerticalAlignment=VerticalAlignment.Center;Ui.Add(row,size,3);
  var bg=checkedNow?new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.Dark?(byte)28:(byte)18,32,164,100)):new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0));
  return new Border{Child=row,CornerRadius=new CornerRadius(10),Background=bg,Padding=new Thickness(2,1,2,1)};
 }

 static string FriendlyFileKind(string path)
 {
  var ext=Path.GetExtension(path).ToLowerInvariant();
  if(new[]{".jpg",".jpeg",".png",".gif",".bmp",".webp",".heic",".raw"}.Contains(ext))return "图片";
  if(new[]{".mp4",".mkv",".mov",".avi",".wmv",".webm",".flv"}.Contains(ext))return "视频";
  if(new[]{".mp3",".wav",".flac",".aac",".m4a"}.Contains(ext))return "音频";
  if(new[]{".doc",".docx",".xls",".xlsx",".ppt",".pptx",".pdf",".txt",".rtf",".csv"}.Contains(ext))return "文档";
  if(new[]{".zip",".7z",".rar",".tar",".gz"}.Contains(ext))return "压缩包";
  if(new[]{".exe",".dll",".msi",".msix"}.Contains(ext))return "程序文件";
  return string.IsNullOrWhiteSpace(ext)?"缓存 / 数据文件":ext.TrimStart('.').ToUpperInvariant()+" 文件";
 }

 void ApplySelectionLocal(ScanItem item,bool selected)
 {
  if(item.Classification.Safety==SafetyLevel.Protected)return;
  lock(cleanupCacheLock)
  {
   if(selected){if(cleanupSelectedIds.Add(item.Id)){cleanupSelectedBytes+=item.File.Size;cleanupSelectedCount++;}}
   else if(cleanupSelectedIds.Remove(item.Id)){cleanupSelectedBytes-=item.File.Size;cleanupSelectedCount=Math.Max(0,cleanupSelectedCount-1);}
  }
  UpdateSelectionSummaryVisual();
 }
 void ApplySelectionsLocal(IEnumerable<ScanItem> items,bool selected)
 {
  lock(cleanupCacheLock)
   foreach(var item in items.Where(x=>x.Classification.Safety!=SafetyLevel.Protected))
   {
    if(selected){if(cleanupSelectedIds.Add(item.Id)){cleanupSelectedBytes+=item.File.Size;cleanupSelectedCount++;}}
    else if(cleanupSelectedIds.Remove(item.Id)){cleanupSelectedBytes-=item.File.Size;cleanupSelectedCount=Math.Max(0,cleanupSelectedCount-1);}
   }
  UpdateSelectionSummaryVisual();
 }
 void UpdateSelectionSummaryVisual()
 {
  long bytes;int count;lock(cleanupCacheLock){bytes=cleanupSelectedBytes+(recycleBinSelected?recycleBinInfo.Bytes:0);count=cleanupSelectedCount+(recycleBinSelected?1:0)+(componentSelected?1:0);}
  if(cleanupSelectedBytesText is not null)cleanupSelectedBytesText.Text=Display.Bytes(bytes);if(cleanupSelectedCountText is not null)cleanupSelectedCountText.Text=$"{count:N0} 项已选 · 组件清理释放量另计，可随时取消";if(cleanupCleanButton is not null)cleanupCleanButton.Content="开始清理   "+Display.Bytes(bytes);
 }
 async Task PersistSelectionAsync(ScanItem item,bool value)
 {
  await selectionWriteGate.WaitAsync();
  try{await Task.Run(()=>services.Database.Select(item.Id,value));}
  catch(Exception e)
  {
   services.Log.Write("Cleanup","Selection","Failed",item.File.Path,e.ToString());
   DispatcherQueue.TryEnqueue(()=>{ApplySelectionLocal(item,!value);SetStatus("选择状态保存失败，已恢复原状态。");if(currentPage=="clean")_=TransitionContentAsync(()=>ShowCleanup(),true,true);});
  }
  finally{selectionWriteGate.Release();}
 }
 async Task PersistSelectionsAsync(IReadOnlyCollection<ScanItem> items,bool value)
 {
  if(items.Count==0)return;await selectionWriteGate.WaitAsync();
  try{await Task.Run(()=>services.Database.SelectMany(items.Select(x=>x.Id),value));}
  catch(Exception e)
  {
   services.Log.Write("Cleanup","SelectionBatch","Failed",detail:e.ToString());
   DispatcherQueue.TryEnqueue(()=>{ApplySelectionsLocal(items,!value);SetStatus("批量选择状态保存失败，已恢复原状态。");if(currentPage=="clean")_=TransitionContentAsync(()=>ShowCleanup(),true,true);});
  }
  finally{selectionWriteGate.Release();}
 }
 async void SetCurrentKindSelectionCached(bool selected)
 {
  if(resultsKind==SafetyLevel.Protected)return;List<ScanItem> items;lock(cleanupCacheLock)items=cleanupGroups.TryGetValue(resultsKind,out var group)?group.ToList():new();
  if(resultsKind==SafetyLevel.Safe)componentSelected=selected&&ComponentEligible;
  ApplySelectionsLocal(items,selected);await PersistSelectionsAsync(items,selected);if(currentPage=="clean")await TransitionContentAsync(()=>ShowCleanup(),true,true);
 }
 async void ClearSelectionsCached()
 {
  componentSelected=false;
  await selectionWriteGate.WaitAsync();
  try{await Task.Run(()=>{lock(cleanupCacheLock){cleanupSelectedIds.Clear();cleanupSelectedBytes=0;cleanupSelectedCount=0;recycleBinSelected=false;}services.Database.ClearSelections();});}
  finally{selectionWriteGate.Release();}
  if(currentPage=="clean")await TransitionContentAsync(()=>ShowCleanup(),true,true);
 }
 async void RestoreRecommendedSelectionsCached()
 {
  componentSelected=ComponentEligible;
  await selectionWriteGate.WaitAsync();
  try
  {
   await Task.Run(()=>
   {
    lock(cleanupCacheLock)
    {
     cleanupSelectedIds.Clear();cleanupSelectedBytes=0;cleanupSelectedCount=0;recycleBinSelected=false;
     if(cleanupGroups.TryGetValue(SafetyLevel.Safe,out var safe))foreach(var item in safe)if(cleanupSelectedIds.Add(item.Id)){cleanupSelectedBytes+=item.File.Size;cleanupSelectedCount++;}
    }
    services.Database.ClearSelections();services.Database.SelectAllSafe(true);
   });
  }
  finally{selectionWriteGate.Release();}
  if(currentPage=="clean")await TransitionContentAsync(()=>ShowCleanup(),true,true);
 }
 List<ScanItem> SelectedItemsSnapshot()
 {
  lock(cleanupCacheLock){return cleanupSelectedIds.Where(cleanupById.ContainsKey).Select(id=>cleanupById[id]).ToList();}
 }

 async Task<CleanupPreflight> PreflightCleanupItemsAsync(IReadOnlyList<ScanItem> candidates)
 {
  return await Task.Run(() =>
  {
   var result=new CleanupPreflight();var freshMarkers=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);var remove=new List<string>();
   var updates=new List<(long Id,FileSnapshot File,Classification Kind,bool Selected)>(candidates.Count);
   foreach(var item in candidates)
   {
    var current=TrySnapshotNow(item.File.Path);
    if(current is null){remove.Add(item.File.Path);result.CacheChanges.Add((item.Id,null));result.RemovedMissing++;continue;}
    var kind=services.Policy.Classify(current,DateTime.UtcNow,fresh:true,sessionMarkers:freshMarkers);var changed=SnapshotChanged(item.File,current);
    if(item.Classification.RuleId is not null&&!string.Equals(item.Classification.RuleId,kind.RuleId,StringComparison.Ordinal)||kind.Safety==SafetyLevel.Protected||kind.Safety>item.Classification.Safety)
    {
     updates.Add((item.Id,current,kind,false));result.CacheChanges.Add((item.Id,new ScanItem(item.Id,current,kind,false)));result.Reclassified++;continue;
    }
    if(changed&&item.Classification.Safety==SafetyLevel.Safe&&services.Policy.IsVolatileRule(kind.RuleId))
    {
     var active=new Classification(SafetyLevel.Optional,"活动缓存","清理前复核发现文件仍在变化，默认移到可选项，避免把正在使用的缓存当成稳定垃圾",kind.RuleId);
     updates.Add((item.Id,current,active,false));result.CacheChanges.Add((item.Id,new ScanItem(item.Id,current,active,false)));result.Reclassified++;continue;
    }
    var prepared=new ScanItem(item.Id,current,kind,true);updates.Add((item.Id,current,kind,true));result.CacheChanges.Add((item.Id,prepared));result.Ready.Add(prepared);
   }
   services.Database.ApplyItemChanges(updates,remove);return result;
  });
 }
 void ApplyCleanupCacheChanges(IEnumerable<(long Id,ScanItem? Item)> changes)
 {
  lock(cleanupCacheLock)
  {
   foreach(var change in changes)
   {
    if(change.Item is null){cleanupById.Remove(change.Id);cleanupSelectedIds.Remove(change.Id);continue;}
    cleanupById[change.Id]=change.Item;
    if(change.Item.Selected&&change.Item.Classification.Safety!=SafetyLevel.Protected)cleanupSelectedIds.Add(change.Id);else cleanupSelectedIds.Remove(change.Id);
   }
   cleanupSelectedCount=cleanupSelectedIds.Count;cleanupSelectedBytes=cleanupSelectedIds.Where(cleanupById.ContainsKey).Sum(id=>cleanupById[id].File.Size);
  }
 }
 async Task RefreshCleanupViewIndexFromMemoryAsync()
 {
  int generation;List<ScanItem> items;
  lock(cleanupCacheLock){generation=cleanupCacheGeneration;items=cleanupById.Values.ToList();}
  var snapshot=await Task.Run(()=>BuildCleanupSnapshotFromUnique(items,CancellationToken.None));
  lock(cleanupCacheLock)
  {
   if(generation!=cleanupCacheGeneration)return;
   // Keep the same scan-generation cache alive. This only refreshes derived UI
   // groups after preflight/cleanup changes; it never rereads CleanC.db and never
   // starts a new Smart Clean cache generation.
   cleanupGroups=snapshot.Groups;cleanupFolderGroups=snapshot.FolderGroups;cleanupById=snapshot.ById;
   cleanupSelectedIds=snapshot.SelectedIds;cleanupSelectedBytes=snapshot.SelectedBytes;cleanupSelectedCount=snapshot.SelectedCount;cleanupCacheReady=true;
  }
 }

 List<(long Id,ScanItem? Item)> ReconcileCleanupBatch(Dictionary<string,ScanItem> byPath,IEnumerable<CleanupOutcome> outcomes,Dictionary<string,bool> freshMarkers)
 {
  var updates=new List<(long Id,FileSnapshot File,Classification Kind,bool Selected)>();var remove=new List<string>();var cacheChanges=new List<(long Id,ScanItem? Item)>();
  foreach(var outcome in outcomes)
  {
   if(outcome.Path.Equals("shell:RecycleBinFolder",StringComparison.OrdinalIgnoreCase)||!byPath.TryGetValue(outcome.Path,out var original))continue;
   var current=TrySnapshotNow(outcome.Path);
   if(current is null){remove.Add(outcome.Path);cacheChanges.Add((original.Id,null));continue;}
   var kind=services.Policy.Classify(current,DateTime.UtcNow,fresh:true,sessionMarkers:freshMarkers);
   if(outcome.Result is "Deleted" or "Gone")
   {
    if(kind.Safety==SafetyLevel.Safe)kind=new(SafetyLevel.Optional,"清理后立即重建","该文件在刚清理后又由系统或应用重新创建，说明它属于活跃缓存；本轮移到可选项，不再反复自动清理",kind.RuleId);
   }
   else if(outcome.Result=="Skipped"&&kind.Safety==SafetyLevel.Safe)
    kind=new(SafetyLevel.Optional,"当前正在使用","本次无法安全删除，说明文件仍被系统或应用使用；移到可选项，避免安全清理反复失败",kind.RuleId);
   updates.Add((original.Id,current,kind,false));cacheChanges.Add((original.Id,new ScanItem(original.Id,current,kind,false)));
  }
  services.Database.ApplyItemChanges(updates,remove);return cacheChanges;
 }
 async Task ReconcileCleanupStreamAsync(IReadOnlyList<ScanItem> attempted,ChannelReader<CleanupOutcome> reader)
 {
  var byPath=attempted.ToDictionary(x=>x.File.Path,StringComparer.OrdinalIgnoreCase);var freshMarkers=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
  while(await reader.WaitToReadAsync())
  {
   var batch=new List<CleanupOutcome>(512);while(reader.TryRead(out var item))batch.Add(item);if(batch.Count==0)continue;
   // Give browsers/applications a short window to immediately recreate volatile caches.
   // This delay overlaps the delete loop instead of being paid as a serial tail afterwards.
   await Task.Delay(450);while(reader.TryRead(out var moreItem))batch.Add(moreItem);
   var changes=await Task.Run(()=>ReconcileCleanupBatch(byPath,batch,freshMarkers));ApplyCleanupCacheChanges(changes);
  }
 }
 async Task ReconcileCleanupReportFallbackAsync(IReadOnlyList<ScanItem> attempted,CleanupReport report)
 {
  var byPath=attempted.ToDictionary(x=>x.File.Path,StringComparer.OrdinalIgnoreCase);var freshMarkers=new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
  var changes=await Task.Run(()=>ReconcileCleanupBatch(byPath,report.Items,freshMarkers));ApplyCleanupCacheChanges(changes);
 }

 static FileSnapshot? TrySnapshotNow(string path)
 {
  try
  {
   if(NativeFileSnapshot.TryRead(path,out var native))return native;
   var info=new FileInfo(path);info.Refresh();
   if(!info.Exists)return null;
   var attrs=info.Attributes;
   if((attrs&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)return null;
   return new(info.FullName,info.Length,info.LastWriteTimeUtc,info.CreationTimeUtc,attrs);
  }
  catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException){return null;}
 }

 static bool SnapshotChanged(FileSnapshot a,FileSnapshot b)=>
  (a.FileId!=0&&b.FileId!=0&&(a.FileId!=b.FileId||a.Volume!=b.Volume))||
  a.Size!=b.Size||a.LastWriteUtc!=b.LastWriteUtc||a.CreationUtc!=b.CreationUtc||a.Attributes!=b.Attributes;

 void ShowCleanupProgressPage()
 {
  if(cleanupProgressView is not null){pageHost.Content=cleanupProgressView;UpdateCleanupVisual();return;}
  cleanupStatusText=Ui.T(cleanupStatus,28,true);cleanupPercentText=Ui.T($"{cleanupPercentValue:0}%",13,true,Ui.Accent);cleanupDetailText=Ui.T(cleanupDetail,12,false,Ui.Muted);
  cleanupBar=new ProgressBar{Minimum=0,Maximum=100,Value=cleanupPercentValue,Height=8,HorizontalAlignment=HorizontalAlignment.Stretch};
  cleanupStopButton=Ui.Button(componentCleanupStage?"Windows 维护中，请等待":"停止",()=>{cleanupCancellation?.Cancel();cleanupStopButton!.IsEnabled=false;cleanupDetail="已请求停止，正在完成当前项目和结果同步…";UpdateCleanupVisual();});
  cleanupStopButton.IsEnabled=!componentCleanupStage;cleanupElapsedText=Ui.T("已用时 00:00",12,false,Ui.Muted);
  cleanupProgressView=Ui.Stack(24,Heading("CLEAN WITH CARE","安全清理","文件直接永久删除，不进入回收站；组件维护开始后请勿关机。"),Ui.GlassCard(Ui.Stack(16,cleanupStatusText,cleanupBar,Ui.Row(10,cleanupPercentText,cleanupDetailText),cleanupElapsedText,cleanupStopButton)));
  pageHost.Content=cleanupProgressView;
 }
 void UpdateCleanupVisual(){if(cleanupBar is not null){cleanupBar.IsIndeterminate=cleanupPercentValue<0;cleanupBar.Value=Math.Max(0,cleanupPercentValue);}if(cleanupPercentText is not null)cleanupPercentText.Text=cleanupPercentValue<0?"处理中":$"{cleanupPercentValue:0}%";if(cleanupDetailText is not null)cleanupDetailText.Text=cleanupDetail;if(cleanupStatusText is not null)cleanupStatusText.Text=cleanupStatus;}

 async Task RunCleanup(bool dryRun)
 {
  if(AnyTaskRunning){await Notice("请等待当前任务完成","扫描、清理或其他维护任务仍在进行。");return;}
  cleanupPreparing=true;cleanupResultView=null;
  try{await RunCleanupCore(dryRun);}
  finally
  {
   cleanupPreparing=false;
   if(currentPage=="clean"&&!closingPending)await TransitionContentAsync(RenderPage,false,true);
   TryFinishPendingClose();
  }
 }
 async Task RunCleanupCore(bool dryRun)
 {
  if(cleanupRunning||cleanupFinalizing){SetStatus("清理任务已经在后台运行。");return;}if(scanRunning){await Notice("扫描正在进行","请等待扫描与智能缓存都完成后再开始清理。系统修复可以与扫描同时进行。");return;}if(RepairBackgroundWorkRunning){await Notice("系统检查或修复正在进行","扫描可以与系统检查同时运行，但清理会写入磁盘。请等待系统任务结束后再开始清理。");return;}if(services.Drivers.IsInstalling){await Notice("驱动正在安装","请等待驱动安装完成后再执行文件清理。");return;}
  services.License.Context.Demand(FeatureCapability.Cleanup);
  if(!cleanupCacheReady)
  {
   var cacheTask=WaitForCurrentCleanupCacheAsync();
   if(!cacheTask.IsCompleted)
   {
    SetStatus("正在等待本次扫描同步创建的智能缓存完成…");
    try{await cacheTask;}catch(OperationCanceledException){}
   }
  }
  if(!cleanupCacheReady){await Notice("智能缓存尚未完成","本次扫描的智能缓存不可用或仍未完成。CleanC 不会重新从数据库构建缓存，请重新扫描后再清理。");return;}
  if(currentPage=="clean")await TransitionContentAsync(()=>ShowCleanup(),false,true);
  await Task.Yield();
  bool includeComponents=componentSelected&&ComponentEligible;
  (List<ScanItem> Items,long Bytes,int Optional,int User,bool Recycle,RecycleBinInfo RecycleInfo) selection;
  selection=await Task.Run(() =>
  {
   var list=SelectedItemsSnapshot();bool recycle;RecycleBinInfo rb;lock(cleanupCacheLock){recycle=recycleBinSelected;rb=recycleBinInfo;}
   return(Items:list,Bytes:list.Sum(x=>x.File.Size),Optional:list.Count(x=>x.Classification.Safety==SafetyLevel.Optional),User:list.Count(x=>x.Classification.Safety==SafetyLevel.UserData),Recycle:recycle,RecycleInfo:rb);
  });
  var preflight=await PreflightCleanupItemsAsync(selection.Items);ApplyCleanupCacheChanges(preflight.CacheChanges);
  var items=preflight.Ready;var targetCount=items.Count+(selection.Recycle?1:0)+(includeComponents?1:0);
  if(preflight.RemovedMissing>0||preflight.Reclassified>0)SetStatus($"清理前已复核：移除失效路径 {preflight.RemovedMissing:N0} 项 · 重新分级 {preflight.Reclassified:N0} 项");
  if(targetCount==0)
  {
   await RefreshCleanupViewIndexFromMemoryAsync();
   await Notice("当前没有可执行的清理项目","已在清理前重新核验勾选项。不存在的路径已经从结果移除，安全级别变化的文件已经转到对应分类。");
   if(currentPage=="clean")await TransitionContentAsync(()=>ShowCleanup(),false,true);return;
  }
  if(items.Count>100000){await RefreshCleanupViewIndexFromMemoryAsync();await Notice("项目数量较多","每次最多处理 100,000 个文件，请分批处理。");if(currentPage=="clean")ShowCleanup();return;}
  var totalBytes=items.Sum(x=>x.File.Size)+(selection.Recycle?selection.RecycleInfo.Bytes:0);var amount=Display.Bytes(totalBytes);var optionalCount=items.Count(x=>x.Classification.Safety==SafetyLevel.Optional);var userCount=items.Count(x=>x.Classification.Safety==SafetyLevel.UserData);
  var warning=userCount>0?$"\n\n其中包含 {userCount:N0} 个‘用户数据’项目，可能是照片、视频、文档或其他个人文件，删除后不可恢复。":optionalCount>0?$"\n\n其中包含 {optionalCount:N0} 个可选项目，是你手动加入清理队列的。":"";
  if(selection.Recycle)
  {
   var drives=selection.RecycleInfo.DriveItems.Count==0?"本地磁盘":string.Join("、",selection.RecycleInfo.DriveItems.Select(x=>x.Root.TrimEnd('\\')));
   warning+=$"\n\n已选择‘回收站’：将清空当前用户在 {drives} 上的回收站，共 {selection.RecycleInfo.Items:N0} 项（{Display.Bytes(selection.RecycleInfo.Bytes)}）。";
  }
  if(includeComponents)warning+="\n\n包含 Windows 旧组件：由系统复核后移除被替代的组件，不使用 ResetBase。释放量无法预估；此阶段不可强行中断，结果会单独列出。";
  if(!dryRun&&!await Confirm("即将清理 "+amount,$"将直接永久处理 {targetCount:N0} 个已选目标，不创建恢复副本。{warning}\n\n占用中、已变化或安全级别变高的文件会自动跳过。","开始清理"))
  {
   await RefreshCleanupViewIndexFromMemoryAsync();if(currentPage=="clean")ShowCleanup();return;
  }

  if(scanRunning||cleanupRunning||cleanupFinalizing||fileMoveRunning||RepairBackgroundWorkRunning||DriverBackgroundWorkRunning){await Notice("任务状态已变化","请等待其他任务结束后重试。");return;}
  cleanupRunning=true;cleanupFinalizing=false;cleanupCancellation=new();cleanupPercentValue=0;cleanupStatus=dryRun?"正在进行安全预演":"正在安全清理";cleanupDetail=$"0 / {targetCount:N0} 个目标";cleanupProgressView=null;
  try{await TransitionContentAsync(()=>ShowCleanupProgressPage(),false,false);}
  catch{cleanupRunning=false;cleanupCancellation?.Dispose();cleanupCancellation=null;cleanupProgressView=null;throw;}
  await Task.Yield();
  var cleanupWatch=System.Diagnostics.Stopwatch.StartNew();
  var elapsedTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
  elapsedTimer.Tick+=(_,_)=>{if(cleanupElapsedText is not null)cleanupElapsedText.Text=$"已用时 {cleanupWatch.Elapsed:hh\\:mm\\:ss}"+(componentCleanupStage?" · Windows 维护仍在运行，完成后会显示结果。":"");};elapsedTimer.Start();
  ChannelWriter<CleanupOutcome>? reconcileWriter=null;Task? reconcileTask=null;
  CleanC.Repair.ComponentCleanupResult? componentResult=null;string componentOutcome=includeComponents?"组件清理尚未执行。":"未选择组件清理。";
  try
  {
   if(!dryRun&&items.Count>0)
   {
    var channel=Channel.CreateUnbounded<CleanupOutcome>(new UnboundedChannelOptions{SingleReader=true,SingleWriter=true,AllowSynchronousContinuations=false});
    reconcileWriter=channel.Writer;reconcileTask=ReconcileCleanupStreamAsync(items,channel.Reader);
   }
   long before=await Task.Run(()=>new DriveInfo(vm.ScanRoot).AvailableFreeSpace);
   var filePercentMax=selection.Recycle?84d:90d;
   using var progress=new DispatcherProgress<CleanupProgress>(DispatcherQueue,p=>{cleanupPercentValue=p.Total<=0?0:Math.Clamp(p.Completed*filePercentMax/p.Total,0,filePercentMax);cleanupDetail=$"已处理 {p.Completed:N0} / {p.Total:N0} 个文件 · 已释放 {Display.Bytes(p.FreedBytes)} · 结果同步并行进行中";UpdateCleanupVisual();},e=>services.Log.Write("Cleanup","UiProgress","Failed",detail:e.ToString()));
   Action<CleanupOutcome>? outcomeSink=null;if(!dryRun&&reconcileWriter is not null){var activeReconcileWriter=reconcileWriter;outcomeSink=o=>{activeReconcileWriter.TryWrite(o);};}
   var report=items.Count>0?await services.Cleaner.ExecuteAsync(items,dryRun,progress,cleanupCancellation.Token,outcomeSink):new CleanupReport(DateTimeOffset.UtcNow,dryRun,0,0,0,false,Array.Empty<CleanupOutcome>());
   if(selection.Recycle&&!report.Canceled&&cleanupCancellation?.IsCancellationRequested!=true)
   {
    cleanupStatus=dryRun?"预演回收站":"正在清空回收站";cleanupPercentValue=Math.Max(cleanupPercentValue,90);cleanupDetail=$"回收站 {selection.RecycleInfo.Items:N0} 项 · {Display.Bytes(selection.RecycleInfo.Bytes)}";UpdateCleanupVisual();
    var outcomes=report.Items.ToList();var freed=report.FreedBytes;var deleted=report.Deleted;var skipped=report.Skipped;
    if(dryRun)outcomes.Add(new("shell:RecycleBinFolder","WouldDelete",selection.RecycleInfo.Bytes,$"用户已勾选所有本地磁盘回收站，共 {selection.RecycleInfo.Items:N0} 项"));
    else
    {
     var rb=await Task.Run(()=>services.RecycleBin.EmptyAll());freed+=rb.FreedBytes;
     ApplyRecycleBinCleanupResult(rb);
     if(rb.Success){deleted++;outcomes.Add(new("shell:RecycleBinFolder","Deleted",rb.FreedBytes,rb.Detail));}
     else{skipped++;outcomes.Add(new("shell:RecycleBinFolder","Skipped",rb.FreedBytes,rb.Detail));}
    }
    report=new CleanupReport(report.StartedAt,dryRun,freed,deleted,skipped,report.Canceled,outcomes);
   }

   reconcileWriter?.TryComplete();reconcileWriter=null;
   if(includeComponents)
   {
    if(report.Canceled||cleanupCancellation?.IsCancellationRequested==true)componentOutcome="已停止，未启动 Windows 组件清理。";
    else if(dryRun)componentOutcome="预演：将调用 Windows 官方组件清理；本次未执行，释放量不预估。";
    else {componentResult=await RunSelectedComponentCleanup();componentOutcome=componentResult.Message;}
   }

   if(!dryRun)
   {
    cleanupStatus=report.Canceled?"正在完成停止前的数据同步":"正在完成安全清理";cleanupPercentValue=Math.Max(cleanupPercentValue,95);cleanupDetail="文件处理已经结束，正在等待并行的数据库复核与智能缓存同步收尾…";UpdateCleanupVisual();
    try
    {
     if(reconcileTask is not null)await reconcileTask;
     cleanupPercentValue=98;cleanupDetail="正在合并最新智能缓存…";UpdateCleanupVisual();
     await RefreshCleanupViewIndexFromMemoryAsync();
    }
    catch(Exception e)
    {
     services.Log.Write("Cleanup","ParallelFinalize","Failed",detail:e.ToString());
     await ReconcileCleanupReportFallbackAsync(items,report);
     await RefreshCleanupViewIndexFromMemoryAsync();
    }
   }
   else if(preflight.CacheChanges.Count>0)await RefreshCleanupViewIndexFromMemoryAsync();

   long after=await Task.Run(()=>new DriveInfo(vm.ScanRoot).AvailableFreeSpace);
   var stopped=report.Canceled||cleanupCancellation?.IsCancellationRequested==true;
   cleanupPercentValue=100;cleanupStatus=dryRun?"预演完成":stopped?"清理已停止":componentResult is {RequiresRestart:true}?"文件清理完成 · 组件需重启":componentResult is {Completed:false}?"文件清理完成 · 组件未完成":"安全清理完成";cleanupDetail=$"已处理 {report.Deleted:N0} 项 · 跳过 {report.Skipped:N0} 项";UpdateCleanupVisual();
   var skippedHint=report.Skipped>0?report.Items.Where(x=>x.Result=="Skipped").GroupBy(x=>x.Detail).OrderByDescending(x=>x.Count()).Select(x=>x.Key).FirstOrDefault():null;
   SetStatus(cleanupStatus+" · "+componentOutcome);
   var summary=new {FinishedAt=DateTimeOffset.UtcNow,DryRun=dryRun,Status=cleanupStatus,FileBytes=report.FreedBytes,report.Deleted,report.Skipped,Component=componentOutcome,DiskBefore=before,DiskAfter=after};
   var reportDirectory=Path.Combine(services.Log.DirectoryPath,"Reports");
   try{Directory.CreateDirectory(reportDirectory);await File.WriteAllTextAsync(Path.Combine(reportDirectory,$"{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}-cleanup-summary.json"),System.Text.Json.JsonSerializer.Serialize(summary));}
   catch(Exception e){services.Log.Write("Cleanup","SummaryReport","Failed",detail:e.GetType().Name);}
   {
    var resultCard=Ui.Card(Ui.Stack(16,
     Ui.T(dryRun?Display.Bytes(report.Items.Where(x=>x.Result=="WouldDelete").Sum(x=>x.Bytes)):Display.Bytes(report.FreedBytes),48,true,Ui.Accent),
     Ui.T(dryRun?"符合清理条件 · 实际删除 0 项":"已处理目标的逻辑大小",14,false,Ui.Muted),
     Ui.T($"已处理 {report.Deleted:N0} 项 · 跳过 {report.Skipped:N0} 项",14),
     Ui.T(report.Skipped>0?$"未删除项目：{skippedHint}":"文件处理、数据库复核和智能缓存同步均已完成。",12,false,report.Skipped>0?Ui.Warning:Ui.Muted),
     Ui.T(componentOutcome,14,true,componentResult is {Completed:false}?Ui.Warning:Ui.Text),
     Ui.T($"C 盘可用空间：{Display.Bytes(before)} → {Display.Bytes(after)}（实测，可能受其他程序影响）",14),
     Ui.Row(12,Ui.Button("返回清理列表",()=>{cleanupResultView=null;_=TransitionContentAsync(()=>ShowCleanup(),false,true);}),Ui.Button("查看报告",()=>OpenFolder(Path.Combine(services.Log.DirectoryPath,"Reports"))))));
    cleanupResultView=Ui.Stack(24,Heading("ALL SET",cleanupStatus,dryRun?"没有删除任何文件。":"文件处理与后台数据收尾已并行完成。"),resultCard);
   }
   if(currentPage=="clean")await TransitionContentAsync(()=>pageHost.Content=cleanupResultView,false,false);
   else if(!closingPending)await Notice(cleanupStatus,componentOutcome+"\n普通文件处理 "+report.Deleted+" 项，跳过 "+report.Skipped+" 项。");
  }
  catch(Exception e)
  {
   services.Log.Write("Cleanup","Workflow","Failed",detail:e.ToString());
   cleanupStatus="清理未能全部完成";SetStatus(cleanupStatus+"："+e.Message);
   cleanupResultView=Ui.Stack(20,Heading("CLEANUP RESULT",cleanupStatus,"已处理的文件不会自动恢复；请查看报告，不把本次记为全部成功。"),
    Ui.Card(Ui.Stack(12,Ui.T(e.Message,14,false,Ui.Warning),Ui.T(componentOutcome,13),
     Ui.Row(10,Ui.Button("返回清理列表",()=>{cleanupResultView=null;_=TransitionContentAsync(()=>ShowCleanup(),false,true);}),
      Ui.Button("查看日志",()=>OpenFolder(services.Log.DirectoryPath))))));
  }
  finally
  {
   elapsedTimer.Stop();cleanupElapsedText=null;cleanupStopButton=null;
   reconcileWriter?.TryComplete();if(reconcileTask is not null&&!reconcileTask.IsCompleted){try{await reconcileTask;}catch(Exception e){services.Log.Write("Cleanup","FinalizeDrain","Failed",detail:e.GetType().Name);}}
   cleanupRunning=false;cleanupFinalizing=false;cleanupCancellation?.Dispose();cleanupCancellation=null;cleanupBar=null;cleanupPercentText=null;cleanupDetailText=null;cleanupStatusText=null;cleanupProgressView=null;TryFinishPendingClose();if(services.License.Context.State!=LicenseState.Active&&!RepairBackgroundWorkRunning)await TransitionContentAsync(RenderPage,false,false);
  }
 }


}
