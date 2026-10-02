using System.Runtime.InteropServices;
using CleanC.Core;
using CleanC.Logging;
namespace CleanC.Cleaner;

public readonly record struct RecycleBinVolumeInfo(string Root,long Items,long Bytes);
public readonly record struct RecycleBinInfo(long Items,long Bytes,IReadOnlyList<RecycleBinVolumeInfo>? Volumes=null,bool QueryComplete=true)
{
 public IReadOnlyList<RecycleBinVolumeInfo> DriveItems=>Volumes??Array.Empty<RecycleBinVolumeInfo>();
}
public readonly record struct RecycleBinCleanupResult(
 bool Success,long Items,long FreedBytes,string Detail,long RemainingItems=0,long RemainingBytes=0,RecycleBinInfo FinalState=default);

public interface IRecycleBinHost
{
 IReadOnlyList<string> LocalRoots();
 bool TryQuery(string root,out RecycleBinVolumeInfo volume);
 int Empty(string root);
}
public sealed class RecycleBinService(ICapabilityGate gate,AuditLog log,IRecycleBinHost? platform=null)
{
 readonly IRecycleBinHost host=platform??new WindowsRecycleBinHost();
 static string Root(string value)
 {
  var root=Path.GetPathRoot(Path.GetFullPath(value))??"";
  if(root.Length!=3||!char.IsAsciiLetter(root[0])||root[1]!=':'||root[2]!='\\')
   throw new ArgumentException("只能操作明确的本地磁盘回收站。");
  return root;
 }
 bool TryQueryDrive(string root,out RecycleBinVolumeInfo volume)
 {
  volume=new(root,0,0);
  try
  {
   if(host.TryQuery(root,out var raw)&&raw.Items>=0&&raw.Bytes>=0)
   {volume=new(root,raw.Items,raw.Bytes);return true;}
  }
  catch(Exception e){log.Write("Cleanup","RecycleBinQuery","Failed",root,e.Message);}
  return false;
 }
 RecycleBinInfo QueryRoots(IEnumerable<string> roots)
 {
  var volumes=new List<RecycleBinVolumeInfo>();var complete=true;
  foreach(var root in roots.Select(Root).Distinct(StringComparer.OrdinalIgnoreCase))
  {
   if(!TryQueryDrive(root,out var volume)){complete=false;continue;}
   volumes.Add(volume); // Keep verified zeroes distinct from missing query results.
  }
  return new(volumes.Sum(x=>x.Items),volumes.Sum(x=>x.Bytes),volumes,complete);
 }
 public RecycleBinInfo Query(string root)=>QueryRoots([Root(root)]);
 public RecycleBinInfo QueryAll()=>QueryRoots(host.LocalRoots());

 public RecycleBinCleanupResult Empty(string root)=>EmptyConfirmed(Query(root),refreshAll:false);
 public RecycleBinCleanupResult EmptyAll()=>EmptyConfirmed(QueryAll());
 // Callers must pass the snapshot shown in their confirmation. Never add a newly attached drive.
 public RecycleBinCleanupResult EmptyConfirmed(RecycleBinInfo confirmed,CancellationToken token=default,bool refreshAll=true)
 {
  gate.Demand(FeatureCapability.Cleanup);
  using var maintenance=MaintenanceLock.Enter();
  if(!confirmed.QueryComplete)
   return new(false,0,0,"回收站状态无法确认，本次未执行清空，请重新查询。",FinalState:confirmed);
  var targets=confirmed.DriveItems.Where(x=>x.Items>0||x.Bytes>0).ToArray();
  if(targets.Any(x=>x.Items<0||x.Bytes<0)||targets.Select(x=>Root(x.Root)).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=targets.Length||
   confirmed.Items!=confirmed.DriveItems.Sum(x=>x.Items)||confirmed.Bytes!=confirmed.DriveItems.Sum(x=>x.Bytes))
   throw new ArgumentException("回收站确认快照无效。");
  var errors=new List<string>();
  foreach(var target in targets)
  {
   var root=Root(target.Root);
   if(token.IsCancellationRequested){errors.Add("已停止，未开始处理剩余磁盘");break;}
   try{gate.Demand(FeatureCapability.Cleanup);}
   catch(Exception e){errors.Add("授权校验未通过，未继续清空："+e.Message);break;}
   try
   {
    if(!host.LocalRoots().Contains(root,StringComparer.OrdinalIgnoreCase)||!TryQueryDrive(root,out var current))
    {errors.Add(root+" 状态无法确认，已跳过");continue;}
    if(current.Items==0&&current.Bytes==0)continue;
    if(current.Items!=target.Items||current.Bytes!=target.Bytes)
    {errors.Add(root+" 回收站内容已变化，请重新确认后清理");continue;}
    // Shell offers a per-volume operation, not an immutable per-item transaction.
    var hr=host.Empty(root);
    if(hr<0)errors.Add(root+$" 清空失败（HRESULT=0x{hr:X8}）");
   }
   catch(Exception e){errors.Add(root+" "+e.Message);}
  }
  var after=QueryRoots(targets.Select(x=>x.Root));
  for(var retry=0;retry<3&&after.QueryComplete&&after.Items>0&&errors.Count==0;retry++)
  {Thread.Sleep(120);after=QueryRoots(targets.Select(x=>x.Root));}
  // Only confirmed volumes with a successful post-query contribute reclaimed bytes.
  long cleared=0,freed=0;
  foreach(var target in targets)
  {
   var verified=after.DriveItems.FirstOrDefault(x=>x.Root.Equals(Root(target.Root),StringComparison.OrdinalIgnoreCase));
   if(string.IsNullOrEmpty(verified.Root))continue;
   cleared+=Math.Max(0,target.Items-verified.Items);freed+=Math.Max(0,target.Bytes-verified.Bytes);
  }
  var success=after.QueryComplete&&after.Items==0&&after.Bytes==0&&errors.Count==0;
  var detail=success?$"已清空所确认磁盘的回收站，清除 {cleared:N0} 项。"
   :$"回收站处理未全部完成；已复核减少 {cleared:N0} 项，剩余 {after.Items:N0} 项。"+
    (!after.QueryComplete?"部分磁盘无法复核，最终状态未知。":"")+string.Join("；",errors);
  // Refresh the UI's all-drive row, but never include unconfirmed drives in cleanup or success.
  var final=refreshAll?QueryAll():after;
  log.Write("Cleanup","RecycleBinEmpty",success?"Completed":"Partial",detail:detail);
  return new(success,cleared,freed,detail,after.Items,after.Bytes,final);
 }

 sealed class WindowsRecycleBinHost:IRecycleBinHost
 {
  public IReadOnlyList<string> LocalRoots()
  {
   var roots=new List<string>();
   foreach(var drive in DriveInfo.GetDrives())
   {
    try{if(drive.IsReady&&drive.DriveType is DriveType.Fixed or DriveType.Removable)roots.Add(Root(drive.RootDirectory.FullName));}
    catch(IOException){}catch(UnauthorizedAccessException){}
   }
   return roots.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
  }
  public bool TryQuery(string root,out RecycleBinVolumeInfo volume)
  {
   var info=new SHQUERYRBINFO{cbSize=(uint)Marshal.SizeOf<SHQUERYRBINFO>()};
   var hr=SHQueryRecycleBinW(root,ref info);volume=new(root,0,0);
   // File/path missing can mean this existing volume has no bin; invalid drive is not an empty bin.
   if(hr is unchecked((int)0x80070002) or unchecked((int)0x80070003))return LocalRoots().Contains(root,StringComparer.OrdinalIgnoreCase);
   if(hr<0||info.i64NumItems<0||info.i64Size<0)return false;
   volume=new(root,info.i64NumItems,info.i64Size);return true;
  }
  public int Empty(string root)=>SHEmptyRecycleBinW(0,root,0x1|0x2|0x4);
 }
 [StructLayout(LayoutKind.Sequential)]
 struct SHQUERYRBINFO{public uint cbSize;public long i64Size;public long i64NumItems;}
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
 static extern int SHQueryRecycleBinW(string pszRootPath,ref SHQUERYRBINFO info);
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
 static extern int SHEmptyRecycleBinW(nint hwnd,string pszRootPath,uint flags);
}
