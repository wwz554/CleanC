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

public sealed class RecycleBinService(ICapabilityGate gate,AuditLog log)
{
 [StructLayout(LayoutKind.Sequential)]
 struct SHQUERYRBINFO{public uint cbSize;public long i64Size;public long i64NumItems;}

 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
 static extern int SHQueryRecycleBinW(string? pszRootPath,ref SHQUERYRBINFO pSHQueryRBInfo);

 [DllImport("shell32.dll",CharSet=CharSet.Unicode)]
 static extern int SHEmptyRecycleBinW(nint hwnd,string? pszRootPath,uint dwFlags);

 const uint NoConfirmation=0x1,NoProgressUi=0x2,NoSound=0x4;
 const int HrFileNotFound=unchecked((int)0x80070002);
 const int HrPathNotFound=unchecked((int)0x80070003);
 const int HrInvalidDrive=unchecked((int)0x8007000F);

 static string Root(string root)=>Path.GetPathRoot(Path.GetFullPath(root))??root;
 static bool IsBenignMissing(int hr)=>hr is HrFileNotFound or HrPathNotFound or HrInvalidDrive;

 static IReadOnlyList<string> LocalRecycleRoots()
 {
  var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var drive in DriveInfo.GetDrives())
  {
   try
   {
    if(!drive.IsReady)continue;
    if(drive.DriveType is not (DriveType.Fixed or DriveType.Removable))continue;
    var root=Root(drive.RootDirectory.FullName);
    if(!string.IsNullOrWhiteSpace(root))roots.Add(root);
   }
   catch{}
  }
  return roots.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToArray();
 }

 bool TryQueryDrive(string root,out RecycleBinVolumeInfo volume)
 {
  root=Root(root);
  volume=new(root,0,0);
  try
  {
   var info=new SHQUERYRBINFO{cbSize=(uint)Marshal.SizeOf<SHQUERYRBINFO>()};
   var hr=SHQueryRecycleBinW(root,ref info);
   if(hr<0)
   {
    if(IsBenignMissing(hr))
    {
     log.Write("Cleanup","RecycleBinQuery","NoBin",root,$"HRESULT=0x{hr:X8}");
     return true;
    }
    log.Write("Cleanup","RecycleBinQuery","Failed",root,$"HRESULT=0x{hr:X8}");
    return false;
   }
   volume=new(root,Math.Max(0,info.i64NumItems),Math.Max(0,info.i64Size));
   return true;
  }
  catch(Exception e)
  {
   log.Write("Cleanup","RecycleBinQuery","Failed",root,e.ToString());
   return false;
  }
 }

 public RecycleBinInfo Query(string root)
 {
  return TryQueryDrive(root,out var volume)
   ?new RecycleBinInfo(volume.Items,volume.Bytes,volume.Items>0||volume.Bytes>0?new[]{volume}:Array.Empty<RecycleBinVolumeInfo>(),true)
   :new RecycleBinInfo(0,0,Array.Empty<RecycleBinVolumeInfo>(),false);
 }

 public RecycleBinInfo QueryAll()
 {
  var volumes=new List<RecycleBinVolumeInfo>();
  long items=0,bytes=0;var complete=true;
  foreach(var root in LocalRecycleRoots())
  {
   if(!TryQueryDrive(root,out var volume)){complete=false;continue;}
   if(volume.Items<=0&&volume.Bytes<=0)continue;
   volumes.Add(volume);
   items+=volume.Items;
   bytes+=volume.Bytes;
  }
  return new(Math.Max(0,items),Math.Max(0,bytes),volumes,complete);
 }

 public RecycleBinCleanupResult Empty(string root)
 {
  gate.Demand(FeatureCapability.Cleanup);
  var before=Query(root);
  if(!before.QueryComplete)return new(false,0,0,"该磁盘回收站状态无法确认，本次不执行清空。",FinalState:before);
  if(before.Items<=0)return new(true,0,0,"该磁盘回收站已经为空。",FinalState:before);
  return EmptyVolumes(before.DriveItems);
 }

 public RecycleBinCleanupResult EmptyAll()
 {
  gate.Demand(FeatureCapability.Cleanup);
  var before=QueryAll();
  if(!before.QueryComplete)return new(false,0,0,"至少一个本地磁盘回收站状态无法确认，本次不执行批量清空，避免把查询失败误认为空。",FinalState:before);
  if(before.Items<=0)return new(true,0,0,"所有本地磁盘回收站已经为空。",FinalState:before);
  return EmptyVolumes(before.DriveItems);
 }

 RecycleBinCleanupResult EmptyVolumes(IReadOnlyList<RecycleBinVolumeInfo> targets)
 {
  if(targets.Count==0)return new(true,0,0,"回收站已经为空。",FinalState:new RecycleBinInfo(0,0,Array.Empty<RecycleBinVolumeInfo>(),true));

  var beforeItems=targets.Sum(x=>x.Items);
  var beforeBytes=targets.Sum(x=>x.Bytes);
  var errors=new List<string>();

  foreach(var target in targets.Where(x=>x.Items>0||x.Bytes>0))
  {
   try
   {
    var hr=SHEmptyRecycleBinW(0,target.Root,NoConfirmation|NoProgressUi|NoSound);
    if(hr>=0)continue;

    // The bin can disappear between scan and cleanup. If Shell says the path is
    // gone and a re-query is already empty, this is a successful final state.
    if(IsBenignMissing(hr)&&TryQueryDrive(target.Root,out var verifyMissing)&&verifyMissing.Items<=0)
    {
     log.Write("Cleanup","RecycleBinEmpty","AlreadyEmpty",target.Root,$"HRESULT=0x{hr:X8}");
     continue;
    }

    var detail=Marshal.GetExceptionForHR(hr)?.Message??$"HRESULT=0x{hr:X8}";
    errors.Add($"{target.Root.TrimEnd('\\')}：{detail}");
    log.Write("Cleanup","RecycleBinEmpty","Failed",target.Root,$"HRESULT=0x{hr:X8}; {detail}");
   }
   catch(Exception e)
   {
    errors.Add($"{target.Root.TrimEnd('\\')}：{e.Message}");
    log.Write("Cleanup","RecycleBinEmpty","Failed",target.Root,e.ToString());
   }
  }

  // Re-query the aggregate result instead of assuming a successful HRESULT means
  // that every volume is empty. Short retries cover delayed Shell counter updates.
  RecycleBinInfo after=QueryAll();
  for(var attempt=0;attempt<3&&after.Items>0;attempt++)
  {
   Thread.Sleep(120);
   after=QueryAll();
  }

  var clearedItems=Math.Max(0,beforeItems-after.Items);
  var freedBytes=Math.Max(0,beforeBytes-after.Bytes);
  var remainingDrives=after.DriveItems.Where(x=>x.Items>0)
   .Select(x=>$"{x.Root.TrimEnd('\\')} {x.Items:N0} 项")
   .ToArray();

  if(!after.QueryComplete)
  {
   var verifyDetailText=$"回收站清理后至少一个磁盘无法复核；已确认清除 {clearedItems:N0} 项，但最终状态未知。";
   log.Write("Cleanup","RecycleBinEmpty","VerifyFailed","AllLocalDrives",verifyDetailText);
   return new(false,clearedItems,freedBytes,verifyDetailText,after.Items,after.Bytes,after);
  }

  if(after.Items<=0)
  {
   log.Write("Cleanup","RecycleBinEmpty","Completed","AllLocalDrives",
    $"cleared={clearedItems}; freed={freedBytes}; drives={targets.Count}");
   return new(true,clearedItems,freedBytes,
    $"已清空所有本地磁盘回收站，共清除 {clearedItems:N0} 项。",0,0,after);
  }

  var remainingText=remainingDrives.Length==0
   ?$"仍有 {after.Items:N0} 项"
   :$"仍有 {after.Items:N0} 项（{string.Join(" · ",remainingDrives)}）";
  var errorText=errors.Count==0?"":$"；异常：{string.Join("；",errors)}";
  var partialDetailText=$"回收站只完成了部分清理：已清除 {clearedItems:N0} 项，{remainingText}{errorText}";

  log.Write("Cleanup","RecycleBinEmpty","Partial","AllLocalDrives",
   $"cleared={clearedItems}; freed={freedBytes}; remaining={after.Items}; {string.Join(" | ",errors)}");
  return new(false,clearedItems,freedBytes,partialDetailText,after.Items,after.Bytes,after);
 }
}
