using System.Diagnostics;
using CleanC.Repair;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CleanC.App;

public sealed partial class MainWindow
{
 DriverScanResult? driverScanResult;double driverUiPercent;string driverUiStage="尚未扫描";bool driverUiInstalling;UIElement? driverViewCache,driverProgressView;
 ProgressBar? driverProgressBar;TextBlock? driverProgressText,driverStageText;
 readonly Dictionary<string,DriverInlineOperation> driverOperations=new(StringComparer.OrdinalIgnoreCase);
 readonly Dictionary<string,List<ContentControl>> driverActionHosts=new(StringComparer.OrdinalIgnoreCase);
 readonly HashSet<string> driverExpandedCategories=new(StringComparer.OrdinalIgnoreCase);
 readonly HashSet<DriverHealth> driverExpandedSummaries=[];
 readonly Queue<DriverQueuedInstall> driverInstallQueue=new();
 readonly HashSet<string> driverQueuedUpdateIds=new(StringComparer.OrdinalIgnoreCase);
 readonly object driverInstallQueueLock=new();
 readonly Dictionary<string,DriverDevice> driverRollbackCandidates=new(StringComparer.OrdinalIgnoreCase);
 readonly Dictionary<string,DriverBackupInfo> driverLocalBackups=new(StringComparer.OrdinalIgnoreCase);
 bool driverInstallQueueRunning;int driverUiWorkflowCount;
 bool DriverBackgroundWorkRunning=>services.Drivers.IsRunning||Volatile.Read(ref driverUiWorkflowCount)>0||DriverInstallQueueBusy();
 bool DriverInstallQueueBusy(){lock(driverInstallQueueLock)return driverInstallQueueRunning||driverInstallQueue.Count>0;}
 sealed class DriverInlineOperation
 {
  public string Stage="";public double Percent;public bool Active;public bool WaitingInstall;public bool ManualPending;public string? Error;
 }
 sealed record DriverQueuedInstall(DriverDevice Device,string UpdateId);

 void ShowDrivers()
 {
  if(services.Drivers.IsScanning){ShowDriverProgress();return;}
  if(driverViewCache is not null){pageHost.Content=driverViewCache;return;}

  var scan=Ui.Button(driverScanResult is null?"全面扫描":"重新全面扫描",()=>_=Guard(StartDriverScan),true);scan.Height=44;
  var source=Ui.GlassCard(Ui.Stack(8,Ui.T("官方驱动来源",12,true),Ui.T("先扫描本机全部 PnP 硬件，识别硬件厂家、当前驱动版本和硬件 ID；再在线匹配由硬件厂商提交、Windows Update 官方分发且适用于当前电脑的签名驱动。匹配到官方包后可自动下载并安装。",12,false,Ui.Muted),Ui.T("如果 Windows Update 没有提供可自动安装包，CleanC 会转到对应硬件厂商官网；厂商网页若需要机型识别、许可确认、UAC 或安装向导，会先提示用户确认。不会使用第三方驱动下载站。",11,false,Ui.Muted)),new Thickness(18));

  if(driverScanResult is null)
  {
   driverViewCache=Ui.Stack(24,Heading("DRIVER HEALTH","驱动修复","扫描硬件驱动、发现设备异常，并检查官方可用更新。"),source,Ui.Card(Ui.Stack(18,Ui.Icon("\uE950",34),Ui.T("还没有进行驱动扫描",22,true),Ui.T("点击“全面扫描”后，CleanC 会在后台读取硬件驱动并在线检查 Windows Update 驱动源。",13,false,Ui.Muted),scan),new Thickness(28)));
   pageHost.Content=driverViewCache;return;
  }

  var result=driverScanResult!;
  driverActionHosts.Clear();
  var summaryExpanders=new Dictionary<DriverHealth,Expander>();
  var stats=Ui.Columns(-1,-1,-1,-1);

  DriverMetric(0,"硬件驱动",Ui.T($"{result.Devices.Count:N0}",28,true,Ui.DriverAccent),"已识别驱动设备");
  DriverMetric(1,"驱动缺失",MetricValueButton($"{result.MissingCount:N0}",Ui.Danger,DriverHealth.Missing,result.MissingCount>0),"未安装或未识别驱动");
  var problemOnly=Math.Max(0,result.ProblemCount-result.MissingCount);
  DriverMetric(2,"驱动异常",MetricValueButton($"{problemOnly:N0}",Ui.Danger,DriverHealth.Problem,problemOnly>0),"设备管理器报告问题");

  var upgradeValue=Ui.Row(5,
   MetricValueButton($"{result.UpgradeableCount:N0}",Ui.Success,DriverHealth.UpdateAvailable,result.UpgradeableCount>0),
   Ui.T("/",28,true,Ui.Success),
   Ui.T($"{result.NormalCount:N0}",28,true,Ui.Success));
  DriverMetric(3,"可升级 / 正常",upgradeValue,"官方更新 / 当前正常");

  void DriverMetric(int col,string title,UIElement value,string caption)
  {
   var card=Ui.Card(Ui.Stack(8,Ui.T(title,11,false,Ui.Muted),value,Ui.T(caption,10.5,false,Ui.Muted)),new Thickness(18));
   Ui.Add(stats,card,col);
  }

  FrameworkElement MetricValueButton(string text,Brush brush,DriverHealth target,bool enabled)
  {
   if(!enabled)return Ui.T(text,28,true,brush);
   var valueText=Ui.T(text,28,true,brush);valueText.TextWrapping=TextWrapping.NoWrap;
   var button=new Button
   {
    Content=valueText,Padding=new Thickness(2,0,2,0),MinWidth=0,MinHeight=0,
    Height=38,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,
    Background=new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)),
    BorderBrush=new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0)),
    BorderThickness=new Thickness(0),CornerRadius=new CornerRadius(7)
   };
   var normal=new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0));
   var hover=new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.Dark?(byte)26:(byte)18,56,122,232));
   Ui.ApplyLiquidButton(button,LiquidButtonTone.Ghost,true);
   button.PointerEntered+=(_,_)=>button.Background=hover;
   button.PointerExited+=(_,_)=>button.Background=normal;
   button.Click+=(_,_)=>JumpToDriverSummary(target);
   ToolTipService.SetToolTip(button,$"查看{DriverSummaryTitle(target)}");
   return button;
  }

  void JumpToDriverSummary(DriverHealth target)
  {
   if(!summaryExpanders.TryGetValue(target,out var expander))return;
   driverExpandedSummaries.Add(target);expander.IsExpanded=true;
   DispatcherQueue.TryEnqueue(()=>expander.StartBringIntoView());
  }

  static string DriverSummaryTitle(DriverHealth state)=>state switch
  {
   DriverHealth.Missing=>"驱动缺失",
   DriverHealth.Problem=>"驱动异常",
   DriverHealth.UpdateAvailable=>"可升级驱动",
   _=>"驱动详情"
  };

  var groups=Ui.Stack(12);
  foreach(var group in result.Devices.GroupBy(x=>DriverCategory(x.DeviceClass)).OrderBy(g=>DriverCategoryOrder(g.Key)).ThenBy(g=>g.Key,StringComparer.OrdinalIgnoreCase))
  {
   var items=Ui.Stack(6);
   foreach(var device in group.OrderBy(x=>x.Health==DriverHealth.Missing?0:x.Health==DriverHealth.Problem?1:x.Health==DriverHealth.UpdateAvailable?2:3).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase))
    items.Children.Add(BuildDriverRow(device));
   var actionable=group.Count(x=>x.Health!=DriverHealth.Normal);
   var header=Ui.Row(10,Ui.Icon(DriverCategoryGlyph(group.Key),17),Ui.T(group.Key,14,true),Ui.T($"{group.Count():N0} 项",11,false,Ui.Muted));
   if(actionable>0)header.Children.Add(DriverStatusChip($"需处理 {actionable:N0}",DriverHealth.UpdateAvailable));
   var expander=new Expander{Header=header,Content=items,IsExpanded=driverExpandedCategories.Contains(group.Key),HorizontalAlignment=HorizontalAlignment.Stretch};
   expander.Expanding+=(_,_)=>driverExpandedCategories.Add(group.Key);
   expander.Collapsed+=(_,_)=>driverExpandedCategories.Remove(group.Key);
   groups.Children.Add(expander);
  }

  var summaries=Ui.Stack(10);
  AddDriverSummary(DriverHealth.Missing,"驱动缺失","未安装、未识别或设备管理器代码 28 的设备。",Ui.Danger);
  AddDriverSummary(DriverHealth.Problem,"驱动异常","设备管理器已经报告驱动或设备状态异常。",Ui.Danger);
  AddDriverSummary(DriverHealth.UpdateAvailable,"可升级驱动","官方驱动源检测到适用于当前硬件的更新。",Ui.Warning);

  void AddDriverSummary(DriverHealth state,string title,string caption,Brush color)
  {
   var devices=result.Devices.Where(x=>x.Health==state).OrderByDescending(x=>x.Update?.DriverDate).ThenBy(x=>DriverCategoryOrder(DriverCategory(x.DeviceClass))).ThenBy(x=>x.Name,StringComparer.OrdinalIgnoreCase).ToList();
   if(devices.Count==0)return;
   var items=Ui.Stack(6);
   foreach(var device in devices)items.Children.Add(BuildDriverRow(device));
   var count=Ui.T($"{devices.Count:N0} 项",11,true,color);
   var header=new Grid{ColumnSpacing=10,VerticalAlignment=VerticalAlignment.Center};
   header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});header.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});header.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
   var left=Ui.Row(10,Ui.Icon(DriverSummaryGlyph(state),17,color),Ui.T(title,14,true),count,Ui.T(caption,10.5,false,Ui.Muted));Ui.Add(header,left,0);
   var bulk=DriverBulkActionButton(state,devices);Ui.Add(header,bulk,2);
   var expander=new Expander{Header=header,Content=items,IsExpanded=driverExpandedSummaries.Contains(state),HorizontalAlignment=HorizontalAlignment.Stretch};
   expander.Expanding+=(_,_)=>driverExpandedSummaries.Add(state);
   expander.Collapsed+=(_,_)=>driverExpandedSummaries.Remove(state);
   summaryExpanders[state]=expander;
   summaries.Children.Add(expander);
  }

  static string DriverSummaryGlyph(DriverHealth state)=>state switch
  {
   DriverHealth.Missing=>"\uE783",
   DriverHealth.Problem=>"\uE7BA",
   DriverHealth.UpdateAvailable=>"\uE895",
   _=>"\uE950"
  };

  var top=Ui.Row(12,scan,Ui.T($"上次扫描：{result.EndedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}",11,false,Ui.Muted));
  var root=Ui.Stack(24,
   Heading("DRIVER HEALTH","驱动修复","保留按硬件类型分类，同时在页面底部集中显示缺失、异常和可升级驱动。"),
   source,top,stats,
   Ui.Stack(10,Ui.T("硬件分类",16,true),Ui.T("扫描完成后默认全部收起，需要时再展开对应硬件类别。",11,false,Ui.Muted),groups));

  if(summaries.Children.Count>0)
  {
   root.Children.Add(Ui.Stack(10,
    Ui.T("需要处理",16,true),
    Ui.T("这里是问题和更新驱动的汇总视图；同一驱动仍保留在上面的原硬件分类中。点击顶部对应数字会自动跳到这里并展开。",11,false,Ui.Muted),
    summaries));
  }

  driverViewCache=root;
  pageHost.Content=driverViewCache;
 }
 Border BuildDriverRow(DriverDevice device)
 {
  var grid=new Grid{ColumnSpacing=12,Padding=new Thickness(12,11,12,11)};
  grid.ColumnDefinitions.Add(new(){Width=new GridLength(42)});
  grid.ColumnDefinitions.Add(new(){Width=new GridLength(1,GridUnitType.Star)});
  grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  grid.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
  var icon=new Border{Width=34,Height=34,CornerRadius=new CornerRadius(10),Background=new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.Dark?(byte)28:(byte)22,104,157,255)),Child=Ui.Icon(DriverCategoryGlyph(DriverCategory(device.DeviceClass)),17,Ui.DriverAccent)};
  Ui.Add(grid,icon,0);

  var provider=string.Join(" · ",new[]{device.Provider,device.Manufacturer}.Where(x=>!string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
  var current=string.IsNullOrWhiteSpace(device.DriverVersion)?"当前版本：未安装 / 未识别":$"当前版本：{device.DriverVersion}";
  if(device.DriverDate is not null)current+=$" · {device.DriverDate.Value.LocalDateTime:yyyy-MM-dd}";
  var next=device.Update is null
   ?(device.Health is DriverHealth.Missing or DriverHealth.Problem
      ?$"设备状态：{device.ProblemText} · 未发现可自动安装的 Windows Update 官方包"
      :"Windows Update 官方源未发现适用更新")
   :$"官方可用：{(string.IsNullOrWhiteSpace(device.Update.Version)?device.Update.Title:device.Update.Version)}{(device.Update.DriverDate is null?"":$" · {device.Update.DriverDate.Value.LocalDateTime:yyyy-MM-dd}")}";
  var nextBrush=device.Health switch{DriverHealth.Missing or DriverHealth.Problem=>Ui.Danger,DriverHealth.UpdateAvailable=>Ui.Warning,_=>Ui.Muted};
  var meta=Ui.Stack(3,Ui.T(device.Name,12.5,true),Ui.T(string.IsNullOrWhiteSpace(provider)?device.DeviceClass:provider,10.5,false,Ui.Muted),Ui.T(current,10.5,false,Ui.Muted),Ui.T(next,10.5,false,nextBrush));
  Ui.Add(grid,meta,1);

  var statusColor=device.Health switch{DriverHealth.Missing or DriverHealth.Problem=>Ui.Danger,DriverHealth.UpdateAvailable=>Ui.Warning,_=>Ui.Muted};
  var status=Ui.T(device.StatusText,11.5,true,statusColor);status.VerticalAlignment=VerticalAlignment.Center;status.HorizontalAlignment=HorizontalAlignment.Center;status.TextAlignment=TextAlignment.Center;status.MinWidth=86;status.Margin=new Thickness(6,0,6,0);
  Ui.Add(grid,status,2);

  var actionHost=new ContentControl{HorizontalContentAlignment=HorizontalAlignment.Right,VerticalContentAlignment=VerticalAlignment.Center,MinWidth=154,Margin=new Thickness(4,0,0,0)};
  actionHost.Content=BuildDriverAction(device);
  if(!driverActionHosts.TryGetValue(device.DeviceId,out var hosts)){hosts=[];driverActionHosts[device.DeviceId]=hosts;}hosts.Add(actionHost);
  Ui.Add(grid,actionHost,3);

  var bg=device.Health switch
  {
   DriverHealth.Missing=>new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.Dark?(byte)29:(byte)17,215,79,79)),
   DriverHealth.Problem=>new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.Dark?(byte)24:(byte)14,215,79,79)),
   DriverHealth.UpdateAvailable=>new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.Dark?(byte)22:(byte)14,198,132,35)),
   _=>new SolidColorBrush(Windows.UI.Color.FromArgb(0,0,0,0))
  };
  return new Border{Child=grid,Background=bg,CornerRadius=new CornerRadius(12)};
 }

 FrameworkElement BuildDriverAction(DriverDevice device)
 {
  if(driverOperations.TryGetValue(device.DeviceId,out var op))
  {
   if(op.Active||op.WaitingInstall)return DriverInlineProgress(op);
   if(op.ManualPending)return DriverActionText("检测此驱动",()=>_=Guard(()=>VerifySingleDriver(device)),true);
   if(!string.IsNullOrWhiteSpace(op.Error))return DriverActionText("重试",()=>_=Guard(()=>StartDriverOperation(device)),true);
  }
  if(device.Health==DriverHealth.Normal)
  {
   var normal=DriverActionText("驱动正常",()=>{},false);normal.IsEnabled=false;normal.Opacity=.56;return normal;
  }
  if((driverLocalBackups.ContainsKey(device.DeviceId)||driverRollbackCandidates.ContainsKey(device.DeviceId))&&device.Health is DriverHealth.Missing or DriverHealth.Problem)
  {
   var row=new StackPanel{Orientation=Orientation.Horizontal,Spacing=8,HorizontalAlignment=HorizontalAlignment.Right};
   row.Children.Add(DriverActionText(device.ActionText,()=>_=Guard(()=>StartDriverOperation(device)),true,112));
   row.Children.Add(DriverActionText("回退",()=>_=Guard(()=>RollbackDriver(device)),true,82,LiquidButtonTone.Neutral));
   return row;
  }
  return DriverActionText(device.ActionText,()=>_=Guard(()=>StartDriverOperation(device)),true);
 }

 FrameworkElement DriverInlineProgress(DriverInlineOperation op)
 {
  var percent=Math.Clamp(op.Percent,0,100);
  const double width=152d,inner=146d;
  var track=new Grid{Width=width,Height=34,HorizontalAlignment=HorizontalAlignment.Right};
  track.Children.Add(new Border
  {
   Width=width,Height=34,CornerRadius=new CornerRadius(17),
   Background=Ui.NavigationGlassBrush(),
   BorderBrush=new LinearGradientBrush
   {
    StartPoint=new Windows.Foundation.Point(0,0),EndPoint=new Windows.Foundation.Point(1,1),
    GradientStops=
    {
     new GradientStop{Color=Windows.UI.Color.FromArgb(160,255,255,255),Offset=0},
     new GradientStop{Color=Windows.UI.Color.FromArgb(82,141,224,176),Offset=.52},
     new GradientStop{Color=Windows.UI.Color.FromArgb(112,255,255,255),Offset=1}
    }
   },
   BorderThickness=new Thickness(1)
  });
  var fillWidth=Math.Max(percent>0?7:0,inner*percent/100d);
  track.Children.Add(new Border
  {
   Width=fillWidth,Height=28,Margin=new Thickness(3),HorizontalAlignment=HorizontalAlignment.Left,
   CornerRadius=new CornerRadius(14),
   Background=new SolidColorBrush(Windows.UI.Color.FromArgb(op.WaitingInstall?(byte)155:(byte)215,32,164,100))
  });
  var labelText=op.WaitingInstall?"等待安装":string.IsNullOrWhiteSpace(op.Stage)?"处理中":op.Stage;
  var label=Ui.T($"{labelText}  {percent:0}%",10.5,true,Ui.Text);
  label.HorizontalAlignment=HorizontalAlignment.Center;label.VerticalAlignment=VerticalAlignment.Center;label.TextAlignment=TextAlignment.Center;label.TextWrapping=TextWrapping.NoWrap;
  track.Children.Add(label);
  return track;
 }
 void RefreshDriverActionHosts(string deviceId)
 {
  if(!driverActionHosts.TryGetValue(deviceId,out var hosts))return;
  var device=driverScanResult?.Devices.FirstOrDefault(x=>x.DeviceId.Equals(deviceId,StringComparison.OrdinalIgnoreCase));
  if(device is null)return;
  foreach(var host in hosts)host.Content=BuildDriverAction(device);
 }

 Button DriverActionText(string text,Action action,bool active,double width=152,LiquidButtonTone? tone=null)
 {
  var button=new Button
  {
   Content=text,Width=width,Height=36,MinWidth=width,Padding=new Thickness(12,0,12,0),
   HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center
  };
  Ui.ApplyLiquidButton(button,active?(tone??LiquidButtonTone.Success):LiquidButtonTone.Neutral,true);
  if(active)button.Click+=(_,_)=>action();
  else
  {
   button.IsEnabled=false;
   button.Foreground=Ui.Muted;
   button.Opacity=.74;
  }
  return button;
 }

 Button DriverBulkActionButton(DriverHealth state,IReadOnlyList<DriverDevice> devices)
 {
  var text=state switch{DriverHealth.Missing=>"一键安装",DriverHealth.Problem=>"一键修复",DriverHealth.UpdateAvailable=>"一键升级",_=>"批量处理"};
  var button=DriverActionText(text,()=>_=Guard(()=>StartBulkDriverAction(state,devices)),true,108,LiquidButtonTone.Success);
  button.Height=32;button.FontSize=11;return button;
 }

 async Task StartBulkDriverAction(DriverHealth state,IReadOnlyList<DriverDevice> devices)
 {
  bool HasCurrentRollback(DriverDevice x)=>driverRollbackCandidates.ContainsKey(x.DeviceId)||(driverLocalBackups.TryGetValue(x.DeviceId,out var b)&&services.Drivers.IsBackupProtected(b));
  var rollback=state==DriverHealth.Problem?devices.Count(HasCurrentRollback):0;
  var automatic=devices.Count(x=>x.Update is not null&&!HasCurrentRollback(x));
  var manual=Math.Max(0,devices.Count-rollback-automatic);
  if(rollback==0&&automatic==0)
  {
   await Notice("没有可一键处理的官方包",$"当前 {devices.Count:N0} 个设备都需要单独进入厂商官网或手动安装。请展开卡片逐项处理。");return;
  }
  var action=state switch{DriverHealth.Missing=>"安装",DriverHealth.Problem=>"修复",DriverHealth.UpdateAvailable=>"升级",_=>"处理"};
  var parts=new List<string>();if(automatic>0)parts.Add($"{automatic:N0} 个将下载官方适配驱动并排队安装");if(rollback>0)parts.Add($"{rollback:N0} 个更新后异常的设备将优先回退到上一版驱动");if(manual>0)parts.Add($"{manual:N0} 个没有自动包，保留手动处理");
  if(!await Confirm($"一键{action}",$"将处理此卡片中的驱动：\\n\\n{string.Join("\\n",parts.Select(x=>"• "+x))}\\n\\n下载可以并行；实际安装和回退会串行执行，避免多个驱动同时修改系统。",$"一键{action}"))return;
  foreach(var device in devices)
  {
   if(state==DriverHealth.Problem&&HasCurrentRollback(device)){_ = Guard(()=>RollbackDriver(device,true));continue;}
   if(device.Update is not null)_ = Guard(()=>StartDriverOperation(device,true));
  }
 }

 async Task StartDriverOperation(DriverDevice device,bool skipConfirm=false)
 {
  if(device.Health==DriverHealth.Normal)return;
  if(scanRunning||cleanupRunning||RepairBackgroundWorkRunning)
  {
   await Notice("当前不适合安装驱动","请等待 C 盘扫描、清理或系统修复任务结束后再安装驱动。多个驱动下载可以同时进行，实际安装会自动排队。");
   return;
  }
  if(driverOperations.TryGetValue(device.DeviceId,out var existing)&&(existing.Active||existing.WaitingInstall))return;

  if(device.Update is null)
  {
   await OpenManualDriverSource(device);
   return;
  }

  var target=string.IsNullOrWhiteSpace(device.Update.Version)?device.Update.Title:device.Update.Version;
  var isHealthyUpgrade=device.Health==DriverHealth.UpdateAvailable&&device.ProblemCode==0&&!device.IsMissingDriver;
  var action=device.Health==DriverHealth.Missing?"安装":device.Health==DriverHealth.Problem?"修复安装":"升级";
  if(!skipConfirm&&!await Confirm($"{action}驱动",
   $"设备：{device.Name}\n当前版本：{(string.IsNullOrWhiteSpace(device.DriverVersion)?"未安装 / 未识别":device.DriverVersion)}\n官方更新：{target}\n\n{(isHealthyUpgrade?"CleanC 会先备份当前正常驱动；备份成功后再下载并安装新版。":"此设备当前属于驱动缺失或驱动异常，不执行原驱动备份。")}\n\n多个驱动可以并行下载，安装会按下载完成顺序自动排队。",
   action))return;

  Interlocked.Increment(ref driverUiWorkflowCount);
  try
  {
   var op=new DriverInlineOperation{Active=true,Stage=isHealthyUpgrade?"准备备份":"准备下载",Percent=2};
   driverOperations[device.DeviceId]=op;RefreshDriverActionHosts(device.DeviceId);

   if(isHealthyUpgrade)
  {
   using var backupProgress=new DispatcherProgress<DriverProgress>(DispatcherQueue,p=>{
    op.Stage=p.Stage;op.Percent=Math.Clamp(p.Percent*.28,2,28);RefreshDriverActionHosts(device.DeviceId);
   },e=>services.Log.Write("Drivers","BackupUiProgress","Failed",device.DeviceId,e.ToString()));

   DriverBackupResult backupResult;
   try{backupResult=await services.Drivers.BackupCurrentDriverAsync(device,backupProgress);}
   catch(Exception e){backupResult=new(false,null,e.Message,0);}

   if(backupResult.Success&&backupResult.Backup is not null)
   {
    driverLocalBackups[device.DeviceId]=backupResult.Backup;
    driverRollbackCandidates[device.DeviceId]=device;
    op.Stage="备份完成";op.Percent=29;RefreshDriverActionHosts(device.DeviceId);
   }
   else
   {
    op.Active=false;op.Stage="备份失败";op.Percent=0;op.Error=backupResult.Message;RefreshDriverActionHosts(device.DeviceId);
    var continueWithoutBackup=await Confirm("驱动备份失败",
     $"设备：{device.Name}\n当前版本：{device.DriverVersion}\n\n{backupResult.Message}\n\n如果继续升级，CleanC 将无法保证新驱动出现问题后一定恢复到当前版本；届时只能尝试 Windows 自带回退或重新安装其他驱动。\n\n是否仍然继续升级？",
     "继续升级");
    if(!continueWithoutBackup)return;

    driverRollbackCandidates[device.DeviceId]=device;
    op.Active=true;op.Error=null;op.Stage="继续升级";op.Percent=2;RefreshDriverActionHosts(device.DeviceId);
   }
  }

  using var progress=new DispatcherProgress<DriverProgress>(DispatcherQueue,p=>{
   op.Stage=p.Stage.Replace("官方驱动包","驱动").Replace("正在确认","确认");
   var floor=isHealthyUpgrade&&driverLocalBackups.ContainsKey(device.DeviceId)?30d:2d;
   op.Percent=Math.Clamp(floor+(100-floor)*p.Percent/100d,floor,100);
   RefreshDriverActionHosts(device.DeviceId);
  },e=>services.Log.Write("Drivers","DownloadUiProgress","Failed",device.DeviceId,e.ToString()));

  try
  {
   var result=await services.Drivers.DownloadAsync(device.Update.UpdateId,progress);
   if(!result.Success)
   {
    op.Active=false;op.Error=result.Message;op.Stage="下载失败";op.Percent=0;RefreshDriverActionHosts(device.DeviceId);
    await Notice("驱动下载失败",result.Message);return;
   }
   op.Active=false;op.WaitingInstall=true;op.Stage="等待安装";op.Percent=100;RefreshDriverActionHosts(device.DeviceId);
   var queued=false;
   lock(driverInstallQueueLock)
   {
    if(driverQueuedUpdateIds.Add(device.Update.UpdateId)){driverInstallQueue.Enqueue(new(device,device.Update.UpdateId));queued=true;}
   }
   if(queued)_=ProcessDriverInstallQueue();
   else
   {
    op.WaitingInstall=false;op.Stage="同一官方更新已排队";op.Percent=100;
    SetStatus($"{device.Name} 使用的同一 Windows Update 驱动包已经排队，本次不重复安装。完成后请重新扫描驱动状态。");
    RefreshDriverActionHosts(device.DeviceId);
   }
  }
   catch(Exception e)
   {
    op.Active=false;op.Error=e.Message;op.Stage="下载失败";op.Percent=0;RefreshDriverActionHosts(device.DeviceId);
    await Notice("驱动下载失败",e.Message);
   }
  }
  finally
  {
   Interlocked.Decrement(ref driverUiWorkflowCount);
   TryFinishPendingClose();
  }
 }


 async Task ProcessDriverInstallQueue()
 {
  lock(driverInstallQueueLock)
  {
   if(driverInstallQueueRunning)return;
   driverInstallQueueRunning=true;
  }
  try
  {
   while(true)
   {
    DriverQueuedInstall? work=null;
    lock(driverInstallQueueLock){if(driverInstallQueue.Count>0)work=driverInstallQueue.Dequeue();}
    if(work is null)break;

    var device=CurrentDriver(work.Device.DeviceId)??work.Device;
    DriverInlineOperation op;
    if(!driverOperations.TryGetValue(device.DeviceId,out var existingOp)||existingOp is null)
    {
     op=new DriverInlineOperation();
     driverOperations[device.DeviceId]=op;
    }
    else op=existingOp;
    op.WaitingInstall=false;op.Active=true;op.Stage="正在安装";op.Percent=5;RefreshDriverActionHosts(device.DeviceId);

    var wasHealthyUpgrade=device.Health==DriverHealth.UpdateAvailable&&device.ProblemCode==0&&!device.IsMissingDriver;
    driverLocalBackups.TryGetValue(device.DeviceId,out var backup);

    try
    {
     using var progress=new DispatcherProgress<DriverProgress>(DispatcherQueue,p=>{
      op.Stage=p.Stage.Replace("正在","");op.Percent=Math.Clamp(p.Percent,0,100);RefreshDriverActionHosts(device.DeviceId);
     },e=>services.Log.Write("Drivers","InstallUiProgress","Failed",device.DeviceId,e.ToString()));

     var result=await services.Drivers.InstallDownloadedAsync(work.UpdateId,device.DeviceId,device.Name,progress);
     op.Active=false;op.Stage=result.Success?"安装完成":"安装失败";op.Percent=result.Success?100:0;

     DriverDevice? verified=null;
     if(result.Success||wasHealthyUpgrade)
     {
      await VerifySingleDriver(device,false);
      verified=CurrentDriver(device.DeviceId);
     }

     if(wasHealthyUpgrade&&verified is not null&&verified.Health is DriverHealth.Missing or DriverHealth.Problem)
     {
      if(result.RequiresRestart)
      {
       if(backup is not null)services.Drivers.MarkBackupPendingRestart(backup,"新驱动需要重启确认，当前状态尚未最终确定");
       SetStatus($"{device.Name} 更新后需要重启，当前检测为 {verified.StatusText}。{(backup is null?"本次没有 CleanC 本地备份，可尝试 Windows 回退。":$"更新前版本 {backup.DriverVersion} 已备份，可随时回退。")}");
       await Notice("驱动需要重启确认",backup is null
        ?$"{device.Name} 更新后 Windows 要求重启。\n\n本次升级是在备份失败后由用户确认继续的。建议先重启；若重启后仍异常，可尝试 Windows 自带回退或修复安装。"
        :$"{device.Name} 更新后 Windows 要求重启。\n\nCleanC 已保存更新前版本 {backup.DriverVersion}。建议先重启；若重启后仍异常，可直接使用“回退”。");
      }
      else if(backup is not null)
      {
       services.Drivers.ProtectBackup(backup,"新驱动安装后检测异常，正在等待/执行回退");
       driverOperations[device.DeviceId]=op;
       op.Active=true;op.Error=null;op.Stage="自动恢复原驱动";op.Percent=8;RefreshDriverActionHosts(device.DeviceId);
       using var restoreProgress=new DispatcherProgress<DriverProgress>(DispatcherQueue,p=>{
        op.Stage=p.Stage;op.Percent=Math.Clamp(p.Percent,0,100);RefreshDriverActionHosts(device.DeviceId);
       },e=>services.Log.Write("Drivers","AutoRestoreUiProgress","Failed",device.DeviceId,e.ToString()));

       var restored=await services.Drivers.RestoreBackupAsync(backup,restoreProgress);
       op.Active=false;op.Percent=restored.Success?100:0;op.Stage=restored.Success?"已恢复原驱动":"自动恢复失败";
       if(restored.Success)
       {
        await VerifySingleDriver(verified,false);
        var afterRestore=CurrentDriver(device.DeviceId);
        if(afterRestore is not null&&afterRestore.Health==DriverHealth.Normal)
        {
         services.Drivers.ReleaseBackupProtection(backup,"回退成功且驱动检测正常");
         driverRollbackCandidates.Remove(device.DeviceId);
         SetStatus($"{device.Name} 更新后异常，已自动恢复到 {backup.DriverVersion}。");
         await Notice("已自动恢复原驱动",$"新驱动导致设备异常，CleanC 已使用升级前备份恢复到版本 {backup.DriverVersion}。\n\n{restored.Message}");
        }
        else
        {
         services.Drivers.ProtectBackup(backup,"已尝试回退但设备仍异常，继续保留备份");
         op.Error="原驱动已恢复，但设备仍未恢复正常。";driverOperations[device.DeviceId]=op;RefreshDriverPagePreservePosition();
         await Notice("恢复后仍有异常",restored.Message+"\n\n设备仍未恢复正常，已保留备份和“回退”入口，建议重启后再次检测。");
        }
       }
       else
       {
        services.Drivers.ProtectBackup(backup,"回退失败，必须保留原驱动备份继续处理");
        op.Error=restored.Message;driverOperations[device.DeviceId]=op;RefreshDriverPagePreservePosition();
        await Notice("自动恢复失败",restored.Message+"\n\n更新前驱动备份仍然保留，可再次点击“回退”。");
       }
      }
      else
      {
       SetStatus($"{device.Name} 更新后异常 · 本次没有 CleanC 本地备份 · 可尝试 Windows 回退");
       RefreshDriverPagePreservePosition();
       await Notice("新驱动检测到异常",
        $"{device.Name} 更新后检测为 {verified.StatusText}。\n\n本次升级前备份失败且用户选择继续，因此 CleanC 无法保证恢复原版本。已保留“回退”入口，用于尝试 Windows 自带回退机制。");
      }
     }
     else if(wasHealthyUpgrade&&verified is not null&&verified.Health==DriverHealth.Normal)
     {
      if(backup is not null)
      {
       if(result.Success&&result.RequiresRestart)
        services.Drivers.MarkBackupPendingRestart(backup,"新驱动安装成功但 Windows 要求重启；重启后重新扫描且设备正常才解除保护");
       else services.Drivers.ReleaseBackupProtection(backup,result.Success
        ?"新驱动安装成功且无需重启，重新检测正常"
        :"新驱动安装未成功，但原驱动仍正常，不再需要故障回退保护");
      }
      if(!(result.Success&&result.RequiresRestart))driverRollbackCandidates.Remove(device.DeviceId);
      SetStatus(backup is null
       ?$"{device.Name} {(result.Success?"升级完成":"升级未完成，但原驱动仍正常")} · 本次没有 CleanC 本地备份。"
       :result.Success&&result.RequiresRestart
        ?$"{device.Name} 安装完成并需要重启 · 更新前备份继续受保护，重启后复检正常才会转入安全清理。"
        :$"{device.Name} {(result.Success?"升级完成":"升级未完成，但原驱动仍正常")} · 备份已转入安全清理，可由用户选择删除。");
     }
     else if(result.Success&&result.RequiresRestart)SetStatus($"{device.Name} 安装完成，需要重启后完全生效。");
     else if(!result.Success)
     {
      if(!wasHealthyUpgrade||verified is null){op.Error=result.Message;driverOperations[device.DeviceId]=op;RefreshDriverActionHosts(device.DeviceId);}
      await Notice("驱动安装失败",result.Message);
     }
    }
    catch(Exception e)
    {
     op.Active=false;op.Error=e.Message;op.Stage="安装失败";op.Percent=0;RefreshDriverActionHosts(device.DeviceId);
     await Notice("驱动安装失败",e.Message);
    }
    finally{lock(driverInstallQueueLock)driverQueuedUpdateIds.Remove(work.UpdateId);}
   }
  }
  finally
  {
   lock(driverInstallQueueLock)driverInstallQueueRunning=false;
   TryFinishPendingClose();
  }
 }


 async Task RollbackDriver(DriverDevice current,bool skipConfirm=false)
 {
  driverLocalBackups.TryGetValue(current.DeviceId,out var backup);
  driverRollbackCandidates.TryGetValue(current.DeviceId,out var previous);
  backup??=services.Drivers.FindLatestBackup(current.DeviceId);
  if(backup is not null)driverLocalBackups[current.DeviceId]=backup;

  if(backup is null&&previous is null)
  {
   await Notice("无法回退","CleanC 没有找到这个设备的更新前驱动备份，也没有本次更新前状态记录。");return;
  }

  var previousVersion=backup?.DriverVersion??previous?.DriverVersion??"未知";
  var mode=backup is not null?"CleanC 升级前保存的完整驱动包":"Windows 系统保存的回退版本";
  if(!skipConfirm&&!await Confirm("回退驱动",$"设备：{current.Name}\n当前版本：{current.DriverVersion}\n更新前版本：{previousVersion}\n恢复来源：{mode}\n\nCleanC 会优先使用升级前的本地完整备份恢复原版本；只有没有本地备份时才使用 Windows 自带回退机制。","回退"))return;

  Interlocked.Increment(ref driverUiWorkflowCount);
  try
  {
   var op=driverOperations.TryGetValue(current.DeviceId,out var existing)?existing:new DriverInlineOperation();driverOperations[current.DeviceId]=op;
  op.Active=true;op.WaitingInstall=false;op.Error=null;op.Stage="准备回退";op.Percent=5;RefreshDriverActionHosts(current.DeviceId);
  using var progress=new DispatcherProgress<DriverProgress>(DispatcherQueue,p=>{op.Stage=p.Stage;op.Percent=Math.Clamp(p.Percent,0,100);RefreshDriverActionHosts(current.DeviceId);},e=>services.Log.Write("Drivers","RollbackUiProgress","Failed",current.DeviceId,e.ToString()));

  try
  {
   var result=backup is not null
    ?await services.Drivers.RestoreBackupAsync(backup,progress)
    :await services.Drivers.RollbackAsync(current.DeviceId,current.Name,progress);

   op.Active=false;op.Percent=result.Success?100:0;op.Stage=result.Success?"回退完成":"回退失败";
   if(!result.Success){op.Error=result.Message;RefreshDriverActionHosts(current.DeviceId);await Notice("驱动回退失败",result.Message);return;}

   await VerifySingleDriver(current,false);
   var verified=CurrentDriver(current.DeviceId);
   if(verified is not null&&verified.Health==DriverHealth.Normal)
   {
    if(backup is not null)services.Drivers.ReleaseBackupProtection(backup,"手动回退成功且驱动检测正常");
    driverRollbackCandidates.Remove(current.DeviceId);
   }
   else if(backup is not null)services.Drivers.ProtectBackup(backup,"回退后仍未检测正常，继续保留备份");
   RefreshDriverPagePreservePosition();
   await Notice("驱动已回退",result.Message+(verified is null?"":$"\n当前状态：{verified.StatusText}"));
  }
   catch(Exception e){op.Active=false;op.Error=e.Message;op.Stage="回退失败";op.Percent=0;RefreshDriverActionHosts(current.DeviceId);await Notice("驱动回退失败",e.Message);}
  }
  finally
  {
   Interlocked.Decrement(ref driverUiWorkflowCount);
   TryFinishPendingClose();
  }
 }

 DriverDevice? CurrentDriver(string deviceId)=>driverScanResult?.Devices.FirstOrDefault(x=>x.DeviceId.Equals(deviceId,StringComparison.OrdinalIgnoreCase));

 async Task VerifySingleDriver(DriverDevice device,bool showResult=true)
 {
  Interlocked.Increment(ref driverUiWorkflowCount);
  try
  {
   if(!driverOperations.TryGetValue(device.DeviceId,out var op)){op=new();driverOperations[device.DeviceId]=op;}
  op.Active=true;op.WaitingInstall=false;op.ManualPending=false;op.Error=null;op.Stage="检测驱动";op.Percent=35;RefreshDriverActionHosts(device.DeviceId);
  try
  {
   var updated=await services.Drivers.VerifyDeviceAsync(device);
   if(updated is null){op.Active=false;op.Error="没有重新枚举到该设备。";RefreshDriverActionHosts(device.DeviceId);return;}
   op.Percent=100;op.Stage="检测完成";op.Active=false;op.Error=null;
   ReplaceDriverResult(updated);
   driverOperations.Remove(device.DeviceId);
   RefreshDriverPagePreservePosition();
   if(showResult)SetStatus(updated.Health==DriverHealth.Normal?$"{updated.Name} · 驱动正常":$"{updated.Name} · {updated.StatusText}");
  }
   catch(Exception e)
   {
    op.Active=false;op.Error=e.Message;op.Stage="检测失败";op.Percent=0;RefreshDriverActionHosts(device.DeviceId);
    if(showResult)await Notice("检测失败",e.Message);
   }
  }
  finally
  {
   Interlocked.Decrement(ref driverUiWorkflowCount);
   TryFinishPendingClose();
  }
 }

 void ReplaceDriverResult(DriverDevice updated)
 {
  if(driverScanResult is null)return;
  var devices=driverScanResult.Devices.Select(x=>x.DeviceId.Equals(updated.DeviceId,StringComparison.OrdinalIgnoreCase)?updated:x).ToList();
  var updates=devices.Count(x=>x.Update is not null);
  var problems=devices.Count(x=>x.Health is DriverHealth.Missing or DriverHealth.Problem);
  driverScanResult=new(driverScanResult.StartedAt,DateTimeOffset.UtcNow,devices,updates,problems,driverScanResult.UnmatchedUpdateCount);
  driverViewCache=null;
 }

 void RefreshDriverPagePreservePosition()
 {
  if(currentPage!="driver")return;
  driverViewCache=null;_=TransitionContentAsync(()=>ShowDrivers(),true,true);
 }

 async Task OpenManualDriverSource(DriverDevice device)
 {
  if(!await Confirm(device.IsMissingDriver?"查找并安装驱动":"查找驱动修复",
   $"设备：{device.Name}\n厂家：{(string.IsNullOrWhiteSpace(device.Manufacturer)?"未识别":device.Manufacturer)}\n硬件 ID：{device.HardwareId}\n\nWindows Update 当前没有给出可自动安装的适配包。CleanC 将继续按“硬件厂商官网 → 互联网搜索”的顺序查找。网页下载的安装程序需要你确认并手动运行；CleanC 不会静默执行来源不明确的 EXE。",
   "继续查找"))return;

  var op=new DriverInlineOperation{Active=true,Stage="查找官网",Percent=45};driverOperations[device.DeviceId]=op;RefreshDriverActionHosts(device.DeviceId);
  await Task.Delay(160);
  var url=ManualDriverUrl(device,out var source);
  op.Active=false;op.ManualPending=true;op.Stage=source;op.Percent=100;RefreshDriverActionHosts(device.DeviceId);
  OpenWeb(url);
  SetStatus($"{device.Name} · 已打开{source}，安装完成后点击“检测此驱动”。");
 }

 static string ManualDriverUrl(DriverDevice device,out string source)
 {
  if(!string.IsNullOrWhiteSpace(device.OfficialSupportUrl))
  {
   source="厂商官网";
   return device.OfficialSupportUrl!;
  }
  source="互联网搜索";
  var q=Uri.EscapeDataString($"\"{device.HardwareId}\" \"{device.Name}\" {device.Manufacturer} driver");
  return $"https://www.bing.com/search?q={q}";
 }

 Border DriverStatusChip(string text,DriverHealth state)
 {
  var color=state switch
  {
   DriverHealth.Normal=>Ui.Color(Ui.Dark?"8E9AA9":"7B8794"),
   DriverHealth.UpdateAvailable=>Ui.Color("C68423"),
   DriverHealth.Missing or DriverHealth.Problem=>Ui.Color("D74F4F"),
   _=>Ui.Color(Ui.Dark?"8E9AA9":"7B8794")
  };
  return new Border
  {
   Child=Ui.T(text,10.5,true,new SolidColorBrush(color)),
   Padding=new Thickness(9,4,9,4),
   CornerRadius=new CornerRadius(11),
   Background=new SolidColorBrush(Windows.UI.Color.FromArgb(Ui.Dark?(byte)34:(byte)22,color.R,color.G,color.B)),
   VerticalAlignment=VerticalAlignment.Center
  };
 }

 void ShowDriverProgress()
 {
  if(driverProgressView is null)
  {
   driverStageText=Ui.T(driverUiStage,22,true);driverProgressText=Ui.T($"{driverUiPercent:0}%",13,true,Ui.DriverAccent);driverProgressBar=new ProgressBar{Minimum=0,Maximum=100,Value=driverUiPercent,Height=8,Foreground=Ui.DriverAccent,HorizontalAlignment=HorizontalAlignment.Stretch};
   driverProgressView=Ui.Stack(24,Heading("DRIVER HEALTH",driverUiInstalling?"正在安装驱动":"正在全面扫描驱动","任务在后台执行，界面和授权倒计时仍可正常使用。"),Ui.GlassCard(Ui.Stack(14,driverStageText,driverProgressBar,driverProgressText),new Thickness(22)));
  }
  UpdateDriverProgress();pageHost.Content=driverProgressView;
 }
 void UpdateDriverProgress(){if(driverProgressBar is not null)driverProgressBar.Value=driverUiPercent;if(driverProgressText is not null)driverProgressText.Text=$"{driverUiPercent:0}%";if(driverStageText is not null)driverStageText.Text=driverUiStage;}

 async Task StartDriverScan()
 {
  if(DriverBackgroundWorkRunning){SetStatus("驱动任务已经在后台运行。");return;}
  Interlocked.Increment(ref driverUiWorkflowCount);
  try
  {
   driverUiInstalling=false;driverUiPercent=0;driverUiStage="正在准备驱动扫描";driverProgressView=null;driverViewCache=null;await TransitionContentAsync(()=>ShowDriverProgress(),false,true);await Task.Yield();
   using var progress=new DispatcherProgress<DriverProgress>(DispatcherQueue,p=>{driverUiPercent=p.Percent;driverUiStage=p.Stage;UpdateDriverProgress();},e=>services.Log.Write("Drivers","UiProgress","Failed",detail:e.ToString()));
   driverScanResult=await services.Drivers.ScanAsync(progress);driverUiPercent=100;driverUiStage="扫描完成";driverExpandedCategories.Clear();driverExpandedSummaries.Clear();driverOperations.Clear();driverRollbackCandidates.Clear();driverLocalBackups.Clear();foreach(var d in driverScanResult.Devices){var b=services.Drivers.FindLatestBackup(d.DeviceId);if(b is not null){driverLocalBackups[d.DeviceId]=b;if(d.Health is DriverHealth.Missing or DriverHealth.Problem)services.Drivers.ProtectBackup(b,"全面扫描发现该设备仍异常，保留用于回退");else services.Drivers.TryReleasePendingRestartProtection(b,true,"Windows 已重启且全面扫描确认设备正常");}}SetStatus($"驱动扫描完成 · {driverScanResult.Devices.Count:N0} 个设备 · {driverScanResult.UpgradeableCount:N0} 个可升级");
  }
  finally
  {
   Interlocked.Decrement(ref driverUiWorkflowCount);driverProgressBar=null;driverProgressText=null;driverStageText=null;driverProgressView=null;driverViewCache=null;if(currentPage=="driver")await TransitionContentAsync(()=>ShowDrivers(),false,true);TryFinishPendingClose();
  }
 }



 static string DriverCategory(string cls)=>cls.ToUpperInvariant() switch{"DISPLAY"=>"显卡","NET"=>"网络适配器","BLUETOOTH"=>"蓝牙","MEDIA"=>"声音、视频和游戏控制器","HDC" or "SCSIADAPTER" or "STORAGE"=>"存储控制器","USB"=>"USB 控制器","HIDCLASS" or "KEYBOARD" or "MOUSE"=>"输入设备","CAMERA" or "IMAGE"=>"摄像头 / 图像设备","MONITOR"=>"显示器","BATTERY"=>"电池与电源","PROCESSOR"=>"处理器","SYSTEM"=>"系统设备","PORTS"=>"端口","BIOMETRIC"=>"生物识别",_=>string.IsNullOrWhiteSpace(cls)?"其他硬件":cls};
 static int DriverCategoryOrder(string category)=>category switch{"显卡"=>0,"网络适配器"=>1,"蓝牙"=>2,"声音、视频和游戏控制器"=>3,"存储控制器"=>4,"USB 控制器"=>5,"摄像头 / 图像设备"=>6,"输入设备"=>7,"系统设备"=>8,_=>20};
 static string DriverCategoryGlyph(string category)=>category switch{"显卡"=>"\uE7F8","网络适配器"=>"\uE968","蓝牙"=>"\uE702","声音、视频和游戏控制器"=>"\uE995","存储控制器"=>"\uEDA2","摄像头 / 图像设备"=>"\uE714","输入设备"=>"\uE765","电池与电源"=>"\uE850",_=>"\uE950"};
 static void OpenWeb(string url){try{Process.Start(new ProcessStartInfo{FileName=url,UseShellExecute=true});}catch{}}
}
