using CleanC.Core;
using CleanC.Repair;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanC.App;
public sealed partial class MainWindow
{
 ComponentStoreAnalysis? componentAnalysis;
 string componentStatus="WinSxS 是系统组件存储，不是普通缓存。扫描会调用 DISM 单独分析，不会直接删除其中的文件。";
 int componentUiTasks;
 ContentControl? componentPanel;
 bool ComponentWorkRunning=>Volatile.Read(ref componentUiTasks)>0||services.ComponentStore.IsRunning;
 UIElement BuildComponentStoreCard()
 {
  componentPanel=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch};
  RefreshComponentPanel();return componentPanel;
 }
 void RefreshComponentPanel()
 {
  if(componentPanel is null)return;
  var body=Ui.Stack(8,Ui.T("Windows 组件存储 · WinSxS",16,true),Ui.T(componentStatus,12,false,Ui.Muted));
  if(componentAnalysis is {Available:true} a)
  {
   body.Children.Add(Ui.T($"实际大小 {a.ActualSize} · 与 Windows 共享 {a.SharedSize} · 可回收组件包 {a.ReclaimablePackages} 个",13,true));
   body.Children.Add(Ui.T("组件存储总大小、备份和缓存大小都不等于可释放空间，不计入上方普通缓存清理合计。",11,false,Ui.Muted));
  }
  if(ComponentWorkRunning)body.Children.Add(new ProgressBar{IsIndeterminate=true,Height=4});
  var analyze=Ui.Button("重新分析",()=>_=Guard(()=>RunComponentStore(false)));
  var clean=Ui.Button("Windows 官方组件清理",()=>_=Guard(()=>RunComponentStore(true)),true);
  analyze.IsEnabled=!ComponentWorkRunning;clean.IsEnabled=!ComponentWorkRunning&&componentAnalysis is {Available:true,CleanupRecommended:true,ReclaimablePackages:>0};
  body.Children.Add(Ui.Row(10,analyze,clean));
  componentPanel.Content=Ui.Card(body,new Thickness(18));
 }
 async Task AnalyzeComponentsAfterScan()
 {
  if(MaintenanceLock.IsHeld||DriverBackgroundWorkRunning){componentStatus="其他维护任务正在运行，组件存储尚未分析；完成后点击重新分析。";return;}
  Interlocked.Increment(ref componentUiTasks);
  try
  {
   componentStatus="正在由 Windows 分析组件存储…";RefreshComponentPanel();SetStatus(componentStatus);
   componentAnalysis=await services.ComponentStore.AnalyzeAsync();componentStatus=componentAnalysis.Message;
  }
  catch(Exception e){componentAnalysis=null;componentStatus="组件存储分析未完成："+e.Message;services.Log.Write("ComponentStore","ScanAnalysis","Failed",detail:e.Message);}
  finally{Interlocked.Decrement(ref componentUiTasks);RefreshComponentPanel();}
 }
 async Task RunComponentStore(bool clean)
 {
  if(AnyTaskRunning){await Notice("请等待当前任务完成","组件维护不能与文件清理、驱动安装或系统修复同时运行。");return;}
  services.License.Context.Demand(clean?FeatureCapability.Cleanup:FeatureCapability.Scan);
  if(clean&&!await Confirm("Windows 官方组件清理","仅由 Windows 删除已被替代的旧组件版本，不直接删除 WinSxS 文件，也不使用 /ResetBase。\n\n旧组件会立即移除（不等待系统自动维护的宽限期），不能恢复这些已清理的旧文件。请先完成更新并重启；开始后请勿关机。组件总大小不等于可释放空间。","开始组件清理"))return;
  if(AnyTaskRunning){await Notice("任务状态已变化","请等待当前任务完成后重试。");return;}
  Interlocked.Increment(ref componentUiTasks);
  componentStatus=clean?"Windows 正在维护组件存储，请勿关机；处理时间由 Windows 决定…":"正在由 Windows 分析组件存储…";RefreshComponentPanel();
  try
  {
   if(clean)
   {
    var result=await services.ComponentStore.CleanupAsync();componentAnalysis=result.After;componentStatus=result.Message;
   }
   else{componentAnalysis=await services.ComponentStore.AnalyzeAsync();componentStatus=componentAnalysis.Message;}
   SetStatus(componentStatus);
  }
  catch(Exception e){componentAnalysis=null;componentStatus="操作未完成："+e.Message;SetStatus(componentStatus);services.Log.Write("ComponentStore","Ui","Failed",detail:e.Message);}
  finally{Interlocked.Decrement(ref componentUiTasks);RefreshComponentPanel();TryFinishPendingClose();if(services.License.Context.State!=LicenseState.Active&&!AnyTaskRunning)await TransitionContentAsync(RenderPage,false,false);}
 }
}
