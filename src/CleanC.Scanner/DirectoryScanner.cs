using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using CleanC.Core;
using CleanC.Logging;
using Microsoft.Data.Sqlite;

namespace CleanC.Scanner;

public sealed class DirectoryScanner(ICapabilityGate gate,SafetyPolicy policy,ScanDatabase database,AuditLog log)
{
 public Task<ScanSummary> ScanAsync(string root,IProgress<ScanProgress>? progress,CancellationToken token,bool quiet=true,Action<long,long>? committedRange=null)
 {
  // Authorization is checked on the caller/UI thread. During the worker scan the UI license
  // timer cancels the token if capability is lost, avoiding concurrent access to mutable
  // license/trusted-time state from the scan thread.
  gate.Demand(FeatureCapability.Scan);
  return Task.Run(()=>Scan(root,progress,token,quiet,committedRange),CancellationToken.None);
 }

 ScanSummary Scan(string root,IProgress<ScanProgress>? progress,CancellationToken token,bool quiet,Action<long,long>? committedRange)
 {
  root=Path.GetFullPath(root);
  log.Write("Scanner","ScanStart","Started",root);
  if(!Directory.Exists(root))throw new IOException("扫描目录不存在或当前不可访问。");
  try{if((File.GetAttributes(root)&FileAttributes.ReparsePoint)!=0)throw new IOException("扫描目录不可为链接或重解析点。");}
  catch(Exception e) when(IsRecoverable(e)){throw new IOException("无法读取扫描目录属性。",e);}

  try{database.Reset();log.Write("Scanner","DatabaseReset","Completed",database.DatabasePath);}
  catch(Exception e){log.Write("Scanner","DatabaseReset","Failed",database.DatabasePath,e.ToString());throw;}
  using var db=database.Open();
  log.Write("Scanner","DatabaseOpen","Completed",database.DatabasePath);
  var watch=Stopwatch.StartNew();var ui=Stopwatch.StartNew();
  long files=0,dirs=0,bytes=0,safe=0;int skipped=0;bool canceled=false;
  var totals=new Dictionary<string,long>(StringComparer.OrdinalIgnoreCase);
  var pending=new Stack<string>();pending.Push(root);
  SqliteTransaction? tx=db.BeginTransaction();long lastPublishedId=0;
  using var insert=db.CreateCommand();
  insert.CommandText="""
 INSERT INTO entries(path,parent,name,size,modified,created,attributes,safety,category,reason,rule,fileid,volume,links,isdir,selected)
 VALUES($p,$parent,$name,$size,$modified,$created,$attrs,$safety,$category,$reason,$rule,$fileid,$volume,$links,$isdir,$selected)
 """;
  foreach(var n in new[]{"p","parent","name","size","modified","created","attrs","safety","category","reason","rule","fileid","volume","links","isdir","selected"})insert.Parameters.Add(new SqliteParameter("$"+n,DBNull.Value));

  void Put(FileSnapshot f,Classification kind,bool isDir)
  {
   insert.Transaction=tx;
   object[] values=[f.Path,Path.GetDirectoryName(f.Path)??"",Path.GetFileName(f.Path),f.Size,f.LastWriteUtc.Ticks,f.CreationUtc.Ticks,(long)f.Attributes,(int)kind.Safety,kind.Category,kind.Reason,(object?)kind.RuleId??DBNull.Value,f.FileId.ToString(),f.Volume,f.Links,isDir?1:0,kind.DefaultSelected?1:0];
   for(int i=0;i<values.Length;i++)insert.Parameters[i].Value=values[i];
   insert.ExecuteNonQuery();
  }

  try
  {
   while(pending.TryPop(out var directory))
   {
    token.ThrowIfCancellationRequested();
    if(SafetyPolicy.Within(directory,AppPaths.UserData)||SafetyPolicy.Within(directory,AppPaths.MachineData))continue;

    try
    {
     if(directory!=root&&(File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0){skipped++;continue;}
    }
    catch(Exception e) when(IsRecoverable(e)){skipped++;log.Write("Scanner","DirectoryAttributes","Skip",directory,e.GetType().Name);continue;}

    totals.TryAdd(directory,0);dirs++;
    IEnumerable<FileSystemInfo> entries;
    try
    {
     entries=new DirectoryInfo(directory).EnumerateFileSystemInfos("*",new EnumerationOptions{
      IgnoreInaccessible=true,
      AttributesToSkip=FileAttributes.ReparsePoint,
      ReturnSpecialDirectories=false,
      RecurseSubdirectories=false
     });
    }
    catch(Exception e) when(IsRecoverable(e)){skipped++;log.Write("Scanner","EnumerateStart","Skip",directory,e.GetType().Name);continue;}

    try
    {
     foreach(var entry in entries)
     {
      token.ThrowIfCancellationRequested();
      try
      {
       var attributes=entry.Attributes;
       if((attributes&FileAttributes.ReparsePoint)!=0){skipped++;continue;}
       var isDir=(attributes&FileAttributes.Directory)!=0;
       if(isDir)
       {
        var f=new FileSnapshot(entry.FullName,0,SafeWrite(entry),SafeCreate(entry),attributes);
        Put(f,new(SafetyLevel.Protected,"目录","仅统计，不直接删除目录"),true);
        pending.Push(entry.FullName);
        continue;
       }

       var info=entry as FileInfo??new FileInfo(entry.FullName);
       var snapshot=new FileSnapshot(info.FullName,info.Length,info.LastWriteTimeUtc,info.CreationTimeUtc,attributes);
       var kind=policy.Classify(snapshot,DateTime.UtcNow);

       // Capture the Windows handle identity during scan. A Safe item without
       // a reliable identity is downgraded rather than being auto-selected.
       if(kind.Safety!=SafetyLevel.Protected)
       {
        if(NativeFileSnapshot.TryRead(info.FullName,out var native))
        {
         snapshot=native;
         kind=policy.Classify(snapshot,DateTime.UtcNow);
        }
        else if(kind.Safety==SafetyLevel.Safe)
         kind=new(SafetyLevel.Optional,"文件身份未确认","Windows 文件身份读取失败；为避免路径替换造成误删，本次不进入安全清理",kind.RuleId);
       }

       if(kind.Safety==SafetyLevel.Safe)
       {
        if(!TryRefreshSnapshot(info,out var refreshed))
        {
         log.Write("Scanner","VolatileGone","Ignore",SafePath(entry),"safe candidate disappeared during scan");
         continue;
        }
        var changed=refreshed.Size!=snapshot.Size||refreshed.LastWriteUtc!=snapshot.LastWriteUtc||
         refreshed.CreationUtc!=snapshot.CreationUtc||refreshed.Attributes!=snapshot.Attributes;
        snapshot=refreshed;
        kind=policy.Classify(snapshot,DateTime.UtcNow);
        if(changed&&kind.Safety==SafetyLevel.Safe&&policy.IsVolatileRule(kind.RuleId))
         kind=new(SafetyLevel.Optional,"活动缓存","扫描过程中内容仍在变化，默认暂不清理；下次稳定后再重新判断",kind.RuleId);
       }

       Put(snapshot,kind,false);
       files++;bytes+=snapshot.Size;totals[directory]+=snapshot.Size;if(kind.DefaultSelected)safe+=snapshot.Size;

       if(files%1000==0)
       {
        RenewTransaction(db,ref tx,committedRange,ref lastPublishedId);
        if(quiet)Thread.Sleep(8);
        if(files%10000==0)log.Write("Scanner","Progress","Running",directory,$"{files} files; {dirs} dirs; {skipped} skipped");
       }
       if(ui.ElapsedMilliseconds>=150){progress?.Report(new(files,dirs,bytes,safe,directory,skipped));ui.Restart();}
      }
      catch(Exception e) when(IsRecoverable(e)||e is ArgumentException)
      {
       skipped++;log.Write("Scanner","Entry","Skip",SafePath(entry),e.GetType().Name);
      }
     }
    }
    catch(Exception e) when(IsRecoverable(e)||e is ArgumentException)
    {
     skipped++;log.Write("Scanner","Enumerate","Skip",directory,e.GetType().Name);
    }
   }
  }
  catch(OperationCanceledException){canceled=true;}
  catch(Exception e) when(e.GetType().Name=="LicenseException"){canceled=true;log.Write("Scanner","Scan","LicenseStopped");}
  catch(Exception e){log.Write("Scanner","Scan","Failed",root,e.ToString());throw;}
  finally
  {
   if(tx is not null)
   {
    var current=tx;tx=null;
    try
    {
     current.Commit();
     PublishCommittedRange(db,committedRange,ref lastPublishedId);
    }
    catch(SqliteException e){log.Write("Scanner","DatabaseCommit","Failed",database.DatabasePath,e.Message);throw;}
    finally{current.Dispose();}
   }
  }

  foreach(var path in totals.Keys.OrderByDescending(p=>p.Length))
  {
   var parent=Path.GetDirectoryName(path);
   if(parent is not null&&totals.ContainsKey(parent))totals[parent]+=totals[path];
  }
  try
  {
   using var rollup=db.BeginTransaction();using var update=db.CreateCommand();update.Transaction=rollup;
   update.CommandText="UPDATE entries SET size=$size WHERE path=$p AND isdir=1";update.Parameters.Add(new("$size",0L));update.Parameters.Add(new("$p",""));
   foreach(var row in totals){update.Parameters[0].Value=row.Value;update.Parameters[1].Value=row.Key;update.ExecuteNonQuery();}
   rollup.Commit();
  }
  catch(SqliteException e){log.Write("Scanner","Rollup","Failed",database.DatabasePath,e.Message);throw;}

  progress?.Report(new(files,dirs,bytes,safe,root,skipped));
  var result=new ScanSummary(root,files,dirs,bytes,safe,skipped,canceled,"Managed Enumeration + Windows handle identity",watch.Elapsed);
  database.SaveSummary(result);
  log.Write("Scanner","Scan",canceled?"Canceled":"Completed",root,$"{files} files; {skipped} skipped; {watch.Elapsed}");
  return result;
 }

 static void RenewTransaction(SqliteConnection db,ref SqliteTransaction? tx,Action<long,long>? committedRange,ref long lastPublishedId)
 {
  var current=tx??throw new InvalidOperationException("扫描事务不存在。");
  tx=null;
  try
  {
   current.Commit();
   PublishCommittedRange(db,committedRange,ref lastPublishedId);
  }
  finally{current.Dispose();}
  tx=db.BeginTransaction();
 }
 static void PublishCommittedRange(SqliteConnection db,Action<long,long>? committedRange,ref long lastPublishedId)
 {
  if(committedRange is null)return;
  using var c=db.CreateCommand();c.CommandText="SELECT last_insert_rowid();";
  var last=Convert.ToInt64(c.ExecuteScalar());
  if(last<=lastPublishedId)return;
  var first=lastPublishedId+1;lastPublishedId=last;
  try{committedRange(first,last);}catch{}
 }
 static bool TryRefreshSnapshot(FileInfo info,out FileSnapshot snapshot)
 {
  // A Safe item must keep the Windows handle identity obtained during scan.
  // Re-reading with FileInfo alone would silently discard FileId/Volume and weaken the delete-time check.
  return NativeFileSnapshot.TryRead(info.FullName,out snapshot);
 }
 static DateTime SafeWrite(FileSystemInfo f){try{return f.LastWriteTimeUtc;}catch{return DateTime.UnixEpoch;}}
 static DateTime SafeCreate(FileSystemInfo f){try{return f.CreationTimeUtc;}catch{return DateTime.UnixEpoch;}}
 static string SafePath(FileSystemInfo f){try{return f.FullName;}catch{return "<unavailable>";}}
 static bool IsRecoverable(Exception e)=>e is IOException or UnauthorizedAccessException or Win32Exception or SecurityException or NotSupportedException;
}
