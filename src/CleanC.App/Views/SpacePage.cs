using CleanC.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanC.App;
public sealed partial class MainWindow
{
 readonly object spaceCacheLock=new();readonly Dictionary<string,List<DirectoryStat>> spaceCache=new(StringComparer.OrdinalIgnoreCase);readonly Dictionary<string,UIElement> spaceViewCache=new(StringComparer.OrdinalIgnoreCase);UIElement? spaceScanningView;string spaceCurrentPath="";
 void ClearSpaceCache(){lock(spaceCacheLock){spaceCache.Clear();spaceViewCache.Clear();spaceScanningView=null;}}
 async Task WarmSpaceRootAsync()
 {
  Interlocked.Increment(ref transientDatabaseReaders);
  try{List<DirectoryStat>? cached;lock(spaceCacheLock)spaceCache.TryGetValue(vm.ScanRoot,out cached);if(cached is not null)return;var children=await Task.Run(()=>services.Database.Children(vm.ScanRoot));lock(spaceCacheLock)spaceCache[vm.ScanRoot]=children;}catch(Exception e){services.Log.Write("Space","WarmCache","Failed",vm.ScanRoot,e.GetType().Name);}
  finally{Interlocked.Decrement(ref transientDatabaseReaders);TryFinishPendingClose();}
 }
 async Task NavigateSpaceAsync(string path)
 {
  if(string.Equals(spaceCurrentPath,path,StringComparison.OrdinalIgnoreCase)&&currentPage=="space")return;
  if(currentPage!="space"){ShowSpace(path);return;}
  await TransitionContentAsync(()=>ShowSpace(path),true,true);
 }
 void ShowSpace(string path)
 {
  services.License.Context.Demand(FeatureCapability.AdvancedAnalysis);spaceCurrentPath=path;
  if(scanRunning)
  {
   if(spaceScanningView is null)
   {
    var scanButton=Ui.Button("正在扫描…",()=>{},true);scanButton.IsEnabled=false;
    spaceScanningView=Ui.Stack(24,Heading("SPACE EXPLORER","空间分析","当前 C 盘扫描正在后台进行，完成后这里会自动展示最新分析结果。"),Ui.GlassCard(Ui.Stack(18,Ui.T("正在等待最新扫描完成",22,true),Ui.T("你可以继续切换页面或同时运行系统检查。",13,false,Ui.Muted),scanButton)));
   }
   pageHost.Content=spaceScanningView;return;
  }
  if(vm.LastScan is null){pageHost.Content=Ui.Stack(24,Heading("SPACE EXPLORER","空间分析","用矩形面积呈现空间占用，颜色仅用于区分目录。"),Ui.Card(Ui.Stack(20,Ui.T("扫描后，查看磁盘空间地图。",22,true),Ui.Button("开始扫描",()=>_=Guard(StartScan),true))));return;}
  lock(spaceCacheLock){if(spaceViewCache.TryGetValue(path,out var cached)){pageHost.Content=cached;return;}}
  List<DirectoryStat>? children;lock(spaceCacheLock)spaceCache.TryGetValue(path,out children);
  if(children is null)
  {
   pageHost.Content=Ui.Stack(24,Heading("SPACE EXPLORER","空间分析","页面已经打开，目录数据正在后台加载。"),Ui.GlassCard(Ui.Stack(12,Ui.T(path,13,true),Ui.T("正在读取缓存数据…",13,false,Ui.Muted))));
   _=LoadSpaceAsync(path);return;
  }
  RenderSpace(path,children);
 }
 async Task LoadSpaceAsync(string path)
 {
  Interlocked.Increment(ref transientDatabaseReaders);
  try
  {
   var children=await Task.Run(()=>services.Database.Children(path));lock(spaceCacheLock)spaceCache[path]=children;
   if(currentPage=="space"&&string.Equals(spaceCurrentPath,path,StringComparison.OrdinalIgnoreCase))await TransitionContentAsync(()=>RenderSpace(path,children),true,true);
  }
  catch(Exception e){services.Log.Write("Space","Load","Failed",path,e.ToString());if(currentPage=="space")SetStatus("空间分析加载失败："+e.Message);}
  finally{Interlocked.Decrement(ref transientDatabaseReaders);TryFinishPendingClose();}
 }
 void RenderSpace(string path,List<DirectoryStat> children)
 {
  lock(spaceCacheLock){if(spaceViewCache.TryGetValue(path,out var cached)){pageHost.Content=cached;return;}}
  var currentProtection=services.Policy.ClassifyPath(path,true);var currentLocked=currentProtection.Safety==SafetyLevel.Protected;
  var list=new ListView{Height=260,SelectionMode=ListViewSelectionMode.None};
  foreach(var child in children)
  {
   var protection=services.Policy.ClassifyPath(child.Path,child.IsDirectory);var locked=protection.Safety==SafetyLevel.Protected;
   var grid=Ui.Columns(32,-1,100,104);grid.Padding=new Thickness(0,8,0,8);Ui.Add(grid,Ui.Icon(locked?"\uE72E":child.IsDirectory?"\uE8B7":"\uE8A5",18),0);
   var title=locked?"🔒 "+child.Name:child.Name;var name=Ui.T(title,13,true);name.TextTrimming=TextTrimming.CharacterEllipsis;name.TextWrapping=TextWrapping.NoWrap;ToolTipService.SetToolTip(name,locked?child.Path+"\n受保护："+protection.Reason:child.Path);Ui.Add(grid,name,1);Ui.Add(grid,Ui.T(Display.Bytes(child.Bytes),13),2);
   var actionText=child.IsDirectory?"进入":locked?"只读查看":"查看文件";
   Ui.Add(grid,Ui.Button(actionText,()=>{if(child.IsDirectory)_=NavigateSpaceAsync(child.Path);else _=Guard(()=>FileDetails(child.Path));}),3);list.Items.Add(grid);
  }
  var parent=Path.GetDirectoryName(path.TrimEnd('\\'));
  var toolbar=Ui.Row(12,Ui.Button("↑ 上一级",()=>{if(parent is not null&&SafetyPolicy.Within(parent,vm.ScanRoot))_=NavigateSpaceAsync(parent);}),Ui.Button("磁盘根目录",()=>_=NavigateSpaceAsync(vm.ScanRoot)),Ui.Button("打开文件夹",()=>OpenFolder(path)));
  var map=new TreemapView(children,v=>{if(v.IsDirectory)_=NavigateSpaceAsync(v.Path);else SetStatus(v.Path+" · "+Display.Bytes(v.Bytes));});
  var body=new List<UIElement>{Heading("SPACE EXPLORER","空间分析","点击矩形进入目录。大小表示占用，不代表应该删除。"),toolbar,Ui.T(path,13,true)};
  if(currentLocked)body.Add(Ui.GlassCard(Ui.Stack(8,Ui.Row(10,Ui.Icon("\uE72E",17),Ui.T("系统重要目录 · 仅供查看",14,true)),Ui.T("此目录包含 Windows、恢复环境、应用状态、凭据或其他重要数据。可以继续进入目录查看占用和路径，但受保护内容不会提供任何删除选项。",12,false,Ui.Muted))));
  body.Add(Ui.Card(map,new Thickness(16)));
  body.Add(Ui.Card(Ui.Stack(8,Ui.T("目录与文件 · 按逻辑大小排序",14,true),Ui.T("显示当前目录最大的 250 项；图中显示其中最大的 40 项。🔒 表示受保护内容，仅允许查看。",11,false,Ui.Muted),list),new Thickness(16)));
  var view=Ui.Stack(20,body.ToArray());
  lock(spaceCacheLock)spaceViewCache[path]=view;pageHost.Content=view;
 }
 async Task FileDetails(string path)
 {
  FileSnapshot snapshot;Classification classification;
  using(var pin=new PinnedFile(path)){snapshot=pin.Snapshot;classification=services.Policy.Classify(snapshot,DateTime.UtcNow,true);}
  var locked=classification.Safety==SafetyLevel.Protected;
  if(locked)
  {
   var protectedContent=Ui.Stack(12,
    Ui.Row(10,Ui.Icon("\uE72E",20),Ui.T("系统重要文件请勿清除",18,true)),
    Ui.T($"文件：{Path.GetFileName(path)}\n大小：{Display.Bytes(snapshot.Size)}\n修改时间：{snapshot.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n路径：{path}",13),
    Ui.GlassCard(Ui.Stack(6,Ui.T("受保护 · "+classification.Category,13,true),Ui.T(classification.Reason,12,false,Ui.Muted))));
   var readOnly=new ContentDialog{XamlRoot=shell.XamlRoot,Title="系统重要文件",Content=protectedContent,CloseButtonText="关闭",DefaultButton=ContentDialogButton.Close};
   await ShowDialogAsync(readOnly);return;
  }
  var content=Ui.Stack(12,Ui.T(Path.GetFileName(path),20,true),Ui.T($"大小：{Display.Bytes(snapshot.Size)}\n类型：{Path.GetExtension(path)}\n修改时间：{snapshot.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}\n路径：{path}",13),Ui.Button("打开所在位置",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName="explorer.exe",Arguments="/select,\""+path+"\"",UseShellExecute=true})));
  var d=new ContentDialog{XamlRoot=shell.XamlRoot,Title="文件详情",Content=content,PrimaryButtonText="选择清理…",CloseButtonText="保留",DefaultButton=ContentDialogButton.Close};
  if(await ShowDialogAsync(d)!=ContentDialogResult.Primary)return;
  var action=new CleanC.Cleaner.UserFileService(services.License.Context,services.Policy);snapshot=action.Preview(path);
  var confirm=new ContentDialog{XamlRoot=shell.XamlRoot,Title="确认永久删除",Content=Ui.T($"{path}\n\n{Display.Bytes(snapshot.Size)}\n\n此操作会直接永久删除文件，不进入回收站，也不会创建 CleanC 恢复副本。删除后无法通过 CleanC 或回收站恢复。",13),PrimaryButtonText="永久删除",CloseButtonText="取消",DefaultButton=ContentDialogButton.Close};
  if(await ShowDialogAsync(confirm)!=ContentDialogResult.Primary)return;
  if(fileMoveRunning){SetStatus("已有文件处理任务正在后台执行。");return;}
  fileMoveRunning=true;fileMoveCancellation=new();SetStatus("正在后台永久删除所选文件…");
  try{await Task.Run(()=>action.DeletePermanent(snapshot,fileMoveCancellation.Token));ClearSpaceCache();InvalidateOverviewCache();await Notice("文件已永久删除","文件未进入回收站，也没有创建恢复副本。请重新扫描以更新空间统计。");}
  finally{fileMoveRunning=false;fileMoveCancellation.Dispose();fileMoveCancellation=null;TryFinishPendingClose();}
 }

}
