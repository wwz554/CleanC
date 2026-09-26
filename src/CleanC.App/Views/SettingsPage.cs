using CleanC.Core;
using CleanC.Cleaner;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
namespace CleanC.App;
public sealed partial class MainWindow
{
 IReadOnlyList<RecoveryItem>? recoveryCache;bool recoveryCacheLoading;UIElement? settingsViewCache;TextBlock? settingsCountdownValue;
 async Task EnsureRecoveryCacheAsync()
 {
  if(recoveryCacheLoading)return;recoveryCacheLoading=true;
  try{recoveryCache=await Task.Run(()=>services.Recovery.List());settingsViewCache=null;if(currentPage=="settings")await TransitionContentAsync(()=>ShowSettings(),true,true);}
  catch(Exception e){services.Log.Write("Settings","RecoveryCache","Failed",detail:e.ToString());}
  finally{recoveryCacheLoading=false;}
 }
 void ShowSettings()
 {
  if(settingsViewCache is not null){if(settingsCountdownValue is not null)settingsCountdownValue.Text=services.License.Context.Countdown;pageHost.Content=settingsViewCache;return;}
  var lease=services.License.Context.Lease!;
  var theme=new ComboBox{Width=200,Height=44};theme.Items.Add("跟随系统");theme.Items.Add("浅色");theme.Items.Add("深色");theme.SelectedIndex=services.Preferences.Theme switch{"light"=>1,"dark"=>2,_=>0};
  theme.SelectionChanged+=(_,_)=>{services.Preferences.Theme=theme.SelectedIndex switch{1=>"light",2=>"dark",_=>"system"};_=Task.Run(()=>services.Preferences.Save());settingsViewCache=null;repairViewCache=null;scanProgressView=null;cleanupProgressView=null;InvalidateOverviewCache();ClearSpaceCache();lock(cleanupCacheLock){cleanupExpandedFolders.Clear();cleanupFolderChildPages.Clear();}BuildShell();};
  var quiet=new ToggleSwitch{Header="温和扫描",IsOn=services.Preferences.QuietScan,OnContent="降低磁盘压力",OffContent="标准速度"};quiet.Toggled+=(_,_)=>{services.Preferences.QuietScan=quiet.IsOn;_=Task.Run(()=>services.Preferences.Save());};
  var license=Ui.Stack(12,Ui.T("授权信息",20,true),Ui.T("CleanC "+lease.Edition+"  ·  "+services.License.Context.StatusText,15,true));
  void Field(string name,string value){var g=Ui.Columns(148,-1);Ui.Add(g,Ui.T(name,12,false,Ui.Muted),0);var t=Ui.T(value,12);t.IsTextSelectionEnabled=true;if(name=="剩余时间")settingsCountdownValue=t;Ui.Add(g,t,1);license.Children.Add(g);}
  string Local(DateTimeOffset? date)=>date?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")??"—";
  Field("授权码",services.License.MaskedKey);Field("设备码",services.License.DeviceId);Field("本机首次确认",Local(services.License.FirstAcceptedUtc));Field("授权到期",lease.IsPermanent?"永久授权":Local(lease.LicenseExpiresAt));Field("剩余时间",services.License.Context.Countdown);Field("最后服务器验证",Local(lease.ServerTime));Field("离线租约截止",Local(lease.ExpiresAt));
  license.Children.Add(Ui.Row(12,Ui.Button("刷新授权",()=>_=Guard(async()=>{await services.License.RefreshAsync();settingsViewCache=null;await TransitionContentAsync(()=>ShowSettings(),true,true);SetStatus(string.IsNullOrEmpty(services.License.LastError)?"授权已刷新。":services.License.LastError);})),Ui.Button("复制设备码",CopyDevice),Ui.Button("检查授权服务",()=>_=Guard(async()=>SetStatus(await services.License.DiagnosticsAsync())))));

  var recovery=Ui.Stack(12,Ui.T("历史恢复区",20,true),Ui.T("仅用于兼容旧版本已经创建的恢复副本。1.3.2 起，智能清理和空间分析永久删除都不会再创建新的恢复副本。",12,false,Ui.Muted));
  if(recoveryCache is null){recovery.Children.Add(Ui.T("恢复区正在后台加载…",13,false,Ui.Muted));_=EnsureRecoveryCacheAsync();}
  else if(recoveryCache.Count==0)recovery.Children.Add(Ui.T("恢复区为空。",13,false,Ui.Muted));
  else foreach(var item in recoveryCache.Take(50))recovery.Children.Add(Ui.Row(12,Ui.T(Path.GetFileName(item.OriginalPath)+" · "+Display.Bytes(item.Size),13),Ui.Button("恢复",()=>_=Guard(async()=>{await Task.Run(()=>services.Recovery.Restore(item));recoveryCache=null;settingsViewCache=null;_=EnsureRecoveryCacheAsync();await Notice("恢复完成","文件已恢复至原位置。恢复区副本仍保留。");}))));
  recovery.Children.Add(Ui.Button("打开恢复区",()=>OpenFolder(AppPaths.Recovery)));

  var logs=Ui.Stack(12,Ui.T("日志与报告",20,true),Ui.Row(12,Ui.Button("查看最近日志",()=>_=Guard(async()=>{SetStatus("正在读取本地日志…");var text=await Task.Run(()=>services.Log.ReadRecent());if(currentPage!="settings")return;var box=new TextBox{Text=text,IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,FontFamily=new FontFamily("Cascadia Mono"),FontSize=11,Height=360,Width=700};var d=new ContentDialog{XamlRoot=shell.XamlRoot,Title="本地日志",Content=box,CloseButtonText="关闭"};await ShowDialogAsync(d);})),Ui.Button("打开日志目录",()=>OpenFolder(services.Log.DirectoryPath))));
  var logo=new Image{Source=new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory,"Assets","CleanC-logo.png"))),Height=160,Stretch=Stretch.Uniform,HorizontalAlignment=HorizontalAlignment.Left};
  var about=Ui.Stack(16,logo,Ui.T("CleanC  1.7.1",20,true),Ui.T("只清理能够确认安全的数据。",14),Ui.T("无广告、无后台驻留、不上传用户文件、不做遥测。\n仅授权请求发送授权码、设备公钥、设备标识与软件版本。\nCopyright © 2026 CleanC",12,false,Ui.Muted));
  settingsViewCache=Ui.Stack(24,Heading("MAKE IT YOURS","设置","外观、扫描偏好和授权信息。"),Ui.Card(Ui.Stack(20,Ui.T("外观与性能",20,true),theme,quiet)),Ui.Card(license),Ui.Card(recovery),Ui.Card(logs),Ui.Card(Ui.Stack(12,Ui.T("软件更新",20,true),Ui.T("当前版本 1.7.1 · 从 GitHub 官方项目获取更新",13,false,Ui.Muted),Ui.Button("检查更新",()=>_=CheckForUpdatesAsync(),true))),Ui.Card(about));
  pageHost.Content=settingsViewCache;
 }
}
