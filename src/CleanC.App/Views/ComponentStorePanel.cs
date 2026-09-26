using CleanC.Core;
using CleanC.Repair;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CleanC.App;
public sealed partial class MainWindow
{
 ComponentStoreAnalysis? componentAnalysis;
 string componentStatus="随文件扫描同步分析 Windows 旧组件。";
 int componentUiTasks;
 bool componentSelected,componentCleanupStage;
 Button? cleanupStopButton;
 TextBlock? cleanupElapsedText;
 ContentControl? componentPanel;
 bool ComponentWorkRunning=>Volatile.Read(ref componentUiTasks)>0||services.ComponentStore.IsRunning;
 bool ComponentEligible=>componentAnalysis is {Available:true,CleanupRecommended:true,ReclaimablePackages:>0};
 UIElement BuildComponentStoreRow()
 {
  componentPanel=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Stretch};
  RefreshComponentPanel();return componentPanel;
 }
 void RefreshComponentPanel()
 {
  if(componentPanel is null)return;
  var box=new CheckBox{IsChecked=componentSelected,IsEnabled=ComponentEligible&&!ComponentWorkRunning};
  box.Click+=(_,_)=>{componentSelected=box.IsChecked==true;UpdateSelectionSummaryVisual();};
  var row=Ui.Columns(40,-1);
  Ui.Add(row,box,0);
  Ui.Add(row,Ui.Stack(5,Ui.T("Windows 旧组件 · WinSxS",14,true),Ui.T(componentStatus,12,false,Ui.Muted),
   Ui.T("随“开始清理”由 Windows 官方工具处理，不直接删系统文件；释放量不预估。",11,false,Ui.Muted)),1);
  componentPanel.Content=Ui.GlassCard(row,new Thickness(16,12,16,12));
 }
 async Task AnalyzeComponentsDuringScan()
 {
  componentAnalysis=null;componentSelected=false;
  if(MaintenanceLock.IsHeld||DriverBackgroundWorkRunning){componentStatus="其他维护任务正在运行，本次跳过组件分析；普通文件扫描不受影响。";return;}
  Interlocked.Increment(ref componentUiTasks);
  try
  {
   componentStatus="Windows 组件分析与文件扫描同步进行中…";
   componentAnalysis=await services.ComponentStore.AnalyzeAsync();componentStatus=componentAnalysis.Message;
   componentSelected=ComponentEligible;
  }
  catch(Exception e){componentAnalysis=null;componentStatus="组件分析未完成："+e.Message;services.Log.Write("ComponentStore","ScanAnalysis","Failed",detail:e.GetType().Name);}
  finally{Interlocked.Decrement(ref componentUiTasks);RefreshComponentPanel();TryFinishPendingClose();}
 }
 async Task<ComponentCleanupResult> RunSelectedComponentCleanup()
 {
  componentCleanupStage=true;componentSelected=false;Interlocked.Increment(ref componentUiTasks);
  cleanupStatus="正在清理 Windows 旧组件";cleanupDetail="正在复核组件状态；此阶段不能安全强行中断，请勿关机。";cleanupPercentValue=-1;
  if(cleanupStopButton is not null){cleanupStopButton.IsEnabled=false;cleanupStopButton.Content="Windows 维护中，请等待";}
  UpdateCleanupVisual();
  using var progress=new DispatcherProgress<RepairProgress>(DispatcherQueue,p=>{
   cleanupStatus=p.Stage;cleanupPercentValue=p.Percent;
   cleanupDetail=p.Percent<0?"Windows 尚未提供百分比，正在处理；请勿关机。":$"当前 Windows 阶段 {p.Percent}% · 完成后还会验证结果，请勿关机。";
   SetStatus(cleanupStatus);UpdateCleanupVisual();
  },e=>services.Log.Write("ComponentStore","UiProgress","Failed",detail:e.GetType().Name));
  try
  {
   var result=await services.ComponentStore.CleanupAsync(progress);componentAnalysis=result.After;componentStatus=result.Message;return result;
  }
  catch(Exception e)
  {
   componentAnalysis=null;componentStatus="组件清理未完成："+e.Message;
   services.Log.Write("ComponentStore","Cleanup","Failed",detail:e.GetType().Name);
   return new(false,false,-1,componentStatus,null);
  }
  finally{componentCleanupStage=false;Interlocked.Decrement(ref componentUiTasks);RefreshComponentPanel();}
 }
}
