using CleanC.Core;
using CleanC.Repair;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
namespace CleanC.App;
public sealed partial class MainWindow
{
 RepairAction repairUiAction=RepairAction.FullRepair;double repairUiPercent;string repairUiStatus="准备就绪",repairUiResult="";
 int repairUiWorkflowCount;
 bool RepairBackgroundWorkRunning=>services.Repair.IsRunning||Volatile.Read(ref repairUiWorkflowCount)>0;
 ProgressBar? repairBar;TextBlock? repairPercentText,repairStatusText;TextBox? repairResultBox;Button? repairRunButton;ComboBox? repairSelector;UIElement? repairViewCache;

 void ShowRepair()
 {
  if(repairViewCache is not null){pageHost.Content=repairViewCache;UpdateRepairVisual(RepairBackgroundWorkRunning);return;}
  repairSelector=new ComboBox{HorizontalAlignment=HorizontalAlignment.Stretch,Height=44,CornerRadius=new CornerRadius(12)};
  var actions=Enum.GetValues<RepairAction>();
  foreach(var action in actions)repairSelector.Items.Add(new ComboBoxItem{Content=RepairCommands.Get(action).Title,Tag=action});
  repairSelector.SelectedIndex=Array.IndexOf(actions,repairUiAction);
  var description=Ui.T("所有检查和修复均调用 Microsoft Windows 内置组件。只有独立复检明确通过才显示“已验证修复成功”；未知输出、缺少修复源或需要重启时绝不会误报为正常。完整修复按微软建议执行 DISM → SFC，并在最后检查 C 盘文件系统。",13,false,Ui.Muted);
  repairResultBox=new TextBox{AcceptsReturn=true,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Height=120,FontSize=14,CornerRadius=new CornerRadius(16),PlaceholderText="这里仅显示检查或修复结果。",Text=repairUiResult};
  repairStatusText=Ui.T(repairUiStatus,13,true);repairPercentText=Ui.T($"{repairUiPercent:0}%",13,true,Ui.Accent);
  repairBar=new ProgressBar{Minimum=0,Maximum=100,Value=repairUiPercent,Height=8,HorizontalAlignment=HorizontalAlignment.Stretch};
  var running=RepairBackgroundWorkRunning;repairBar.Visibility=running?Visibility.Visible:repairUiPercent>0?Visibility.Visible:Visibility.Collapsed;repairPercentText.Visibility=repairBar.Visibility;
  repairRunButton=Ui.Button(repairUiAction==RepairAction.FullRepair?"一键修复并复检":RepairCommands.Get(repairUiAction).ChangesSystem?"开始修复":"开始检查",()=>{},true);
  repairSelector.IsEnabled=!running;repairRunButton.IsEnabled=!running;
  repairSelector.SelectionChanged+=(_,_)=>{
   if(repairSelector.SelectedItem is not ComboBoxItem item||item.Tag is not RepairAction action)return;
   repairUiAction=action;if(!RepairBackgroundWorkRunning&&repairRunButton is not null)repairRunButton.Content=action==RepairAction.FullRepair?"一键修复并复检":RepairCommands.Get(action).ChangesSystem?"开始修复":"开始检查";
  };
  repairRunButton.Click+=async(_,_)=>await Guard(RunRepairFromUi);
  var info=Ui.Columns(-1,-1,-1);Ui.Add(info,Ui.Card(Ui.Stack(12,Ui.Icon("\uE7F4",24),Ui.T("Windows 映像",17,true),Ui.T("Microsoft DISM + ImageHealthState",12,false,Ui.Muted)),new Thickness(20)),0);Ui.Add(info,Ui.Card(Ui.Stack(12,Ui.Icon("\uE73E",24),Ui.T("系统文件",17,true),Ui.T("Microsoft SFC + 二次验证",12,false,Ui.Muted)),new Thickness(20)),1);Ui.Add(info,Ui.Card(Ui.Stack(12,Ui.Icon("\uEDA2",24),Ui.T("磁盘文件系统",17,true),Ui.T("Microsoft CHKDSK 官方退出码",12,false,Ui.Muted)),new Thickness(20)),2);
  var advanced=new Expander{Header="高级选项 · 单独检查或修复",Content=repairSelector,HorizontalAlignment=HorizontalAlignment.Stretch};
  repairViewCache=Ui.Stack(24,Heading("WINDOWS HEALTH","系统修复","一键检查、修复并复检；遇到无法自动处理的问题会明确说明下一步。"),info,Ui.GlassCard(Ui.Stack(16,description,repairRunButton,repairStatusText,repairBar,repairPercentText,repairResultBox,advanced)));
  pageHost.Content=repairViewCache;
 }

 async Task RunRepairFromUi()
 {
  if(RepairBackgroundWorkRunning){SetStatus("系统检查或修复已经在后台运行。");return;}
  if(cleanupRunning){await Notice("清理正在进行","为了避免磁盘写入互相影响，请等待清理结束后再进行系统修复。C 盘扫描可以与系统检查同时运行。");return;}
  if(services.Drivers.IsInstalling){await Notice("驱动正在安装","请等待驱动安装结束后再运行系统修复。");return;}
  var action=repairUiAction;var command=RepairCommands.Get(action);
  if(!RepairService.IsAdministrator){await Notice("需要管理员权限","请关闭 CleanC，然后右键程序选择“以管理员身份运行”。正式安装版启动时会自动请求权限。");return;}
  if(command.ChangesSystem&&!await Confirm(command.Title,"将使用 Windows 官方组件执行系统修复。修复结束后 CleanC 会自动再次检查结果。","开始修复"))return;

  Interlocked.Increment(ref repairUiWorkflowCount);
  try
  {
   repairUiPercent=0;repairUiStatus=command.ChangesSystem?"正在修复…":"正在检查…";repairUiResult="";UpdateRepairVisual(true);
   await Task.Yield();
   using var progress=new DispatcherProgress<RepairProgress>(DispatcherQueue,p=>{repairUiPercent=p.Percent;repairUiStatus=p.Stage;UpdateRepairVisual(true);},e=>services.Log.Write("Repair","UiProgress","Failed",detail:e.ToString()));
   var result=await services.Repair.RunAsync(action,progress);repairUiPercent=100;repairUiStatus=result.RequiresRestart?"需要重启后复检":result.Success?(command.ChangesSystem?"已验证修复成功":"已验证正常"):"仍需处理";repairUiResult=result.Summary;UpdateRepairVisual(false);SetStatus(result.Summary);
  }
  finally
  {
   Interlocked.Decrement(ref repairUiWorkflowCount);
   if(repairRunButton is not null)repairRunButton.IsEnabled=true;if(repairSelector is not null)repairSelector.IsEnabled=true;TryFinishPendingClose();if(services.License.Context.State!=LicenseState.Active&&!AnyTaskRunning)await TransitionContentAsync(RenderPage,false,false);
  }
 }
 void UpdateRepairVisual(bool running)
 {
  if(repairBar is not null){repairBar.Value=repairUiPercent;repairBar.Visibility=Visibility.Visible;}
  if(repairPercentText is not null){repairPercentText.Text=$"{repairUiPercent:0}%";repairPercentText.Visibility=Visibility.Visible;}
  if(repairStatusText is not null)repairStatusText.Text=repairUiStatus;if(repairResultBox is not null)repairResultBox.Text=repairUiResult;
  if(repairRunButton is not null)repairRunButton.IsEnabled=!running;if(repairSelector is not null)repairSelector.IsEnabled=!running;
 }
}
