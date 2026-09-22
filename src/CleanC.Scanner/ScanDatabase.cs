using CleanC.Core;
using Microsoft.Data.Sqlite;
namespace CleanC.Scanner;
public readonly record struct ExitDatabaseCleanupResult(
 bool Success,long RemovedEntries,long RemovedReports,long BeforeBytes,long AfterBytes,string? Error);

public sealed class ScanDatabase
{
 public string DatabasePath{get;}
 public ScanDatabase(string? path=null)
 {
  DatabasePath=path??Path.Combine(AppPaths.UserData,"Data","CleanC.db");
  Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
  try{Initialize();}
  catch(SqliteException){QuarantineBrokenDatabase();Initialize();}
 }
 void Initialize()
 {
  using var db=Open();using var cmd=db.CreateCommand();cmd.CommandText="""
 PRAGMA journal_mode=WAL;
 PRAGMA synchronous=NORMAL;
 CREATE TABLE IF NOT EXISTS entries(id INTEGER PRIMARY KEY,path TEXT NOT NULL,parent TEXT NOT NULL,name TEXT NOT NULL,size INTEGER NOT NULL,modified INTEGER NOT NULL,created INTEGER NOT NULL,attributes INTEGER NOT NULL,safety INTEGER NOT NULL,category TEXT NOT NULL,reason TEXT NOT NULL,rule TEXT,fileid TEXT,volume INTEGER,links INTEGER,isdir INTEGER NOT NULL,selected INTEGER NOT NULL);
 CREATE INDEX IF NOT EXISTS idx_path ON entries(path);
 CREATE INDEX IF NOT EXISTS idx_parent ON entries(parent);
 CREATE INDEX IF NOT EXISTS idx_safety ON entries(safety,selected);
 CREATE TABLE IF NOT EXISTS summaries(id INTEGER PRIMARY KEY,started TEXT,summary TEXT);
 CREATE TABLE IF NOT EXISTS reports(id INTEGER PRIMARY KEY,started TEXT,report TEXT);
 """;cmd.ExecuteNonQuery();
 }
 void QuarantineBrokenDatabase()
 {
  QuarantineDatabaseSet(DatabasePath,"broken");
 }
 static void QuarantineDatabaseSet(string databasePath,string reason)
 {
  // Never delete the only database copy merely because quarantine/rename failed.
  // A failed move is fail-closed: leave the original set in place for manual recovery.
  var token=DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")[..8];
  var copies=new List<(string Source,string Target)>();
  foreach(var suffix in new[]{"","-wal","-shm"})
  {
   var source=databasePath+suffix;if(!File.Exists(source))continue;
   var target=source+"."+reason+"-"+token;
   File.Copy(source,target,false);
   if(new FileInfo(source).Length!=new FileInfo(target).Length)throw new IOException($"数据库隔离副本校验失败：{source}");
   copies.Add((source,target));
  }
  foreach(var item in copies)
  {
   try{File.Delete(item.Source);}
   catch(Exception e) when(e is IOException or UnauthorizedAccessException)
   {
    throw new IOException($"数据库已安全复制到隔离文件，但原文件无法移除：{item.Source}。原文件和隔离副本都已保留。",e);
   }
  }
 }
 public static void RecoverAfterUncleanExit(string? path=null)
 {
  var databasePath=path??Path.Combine(AppPaths.UserData,"Data","CleanC.db");
  if(!File.Exists(databasePath)&&!File.Exists(databasePath+"-wal")&&!File.Exists(databasePath+"-shm"))return;
  try
  {
   // Keep db + WAL + SHM together and let SQLite perform its documented crash recovery.
   // Only after a successful open + quick_check do we clear transient scan tables.
   SqliteConnection.ClearAllPools();
   using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=databasePath,DefaultTimeout=30,Mode=SqliteOpenMode.ReadWrite}.ToString());
   db.Open();
   using(var check=db.CreateCommand())
   {
    check.CommandText="PRAGMA quick_check;";
    var result=Convert.ToString(check.ExecuteScalar())??string.Empty;
    if(!result.Equals("ok",StringComparison.OrdinalIgnoreCase))throw new IOException("SQLite quick_check failed after crash recovery: "+result);
   }
   using(var clear=db.CreateCommand())
   {
    clear.CommandText="DROP TABLE IF EXISTS mft; DELETE FROM entries; DELETE FROM reports;";
    clear.ExecuteNonQuery();
   }
   using(var checkpoint=db.CreateCommand()){checkpoint.CommandText="PRAGMA wal_checkpoint(TRUNCATE);";checkpoint.ExecuteNonQuery();}
  }
  catch(Exception e) when(e is SqliteException or IOException or UnauthorizedAccessException)
  {
   SqliteConnection.ClearAllPools();
   QuarantineDatabaseSet(databasePath,"unclean-corrupt");
  }
 }
 public string SQLiteVersion(){using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT sqlite_version();";return Convert.ToString(c.ExecuteScalar())??"unknown";}
 public SqliteConnection Open(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=DatabasePath,DefaultTimeout=30,Mode=SqliteOpenMode.ReadWriteCreate}.ToString());c.Open();return c;}
 public void Reset(){using var db=Open();using var c=db.CreateCommand();c.CommandText="DROP TABLE IF EXISTS mft; DELETE FROM entries;";c.ExecuteNonQuery();}
 public List<ScanItem> Items(SafetyLevel? safety,int offset=0,int limit=100,bool selectedOnly=false)
 {
  using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT id,path,size,modified,created,attributes,fileid,volume,links,safety,category,reason,rule,selected FROM entries WHERE isdir=0 AND ($s IS NULL OR safety=$s) AND ($selected=0 OR selected=1) ORDER BY size DESC LIMIT $limit OFFSET $offset";
  c.Parameters.AddWithValue("$s",safety.HasValue?(object)(int)safety:DBNull.Value);c.Parameters.AddWithValue("$selected",selectedOnly?1:0);c.Parameters.AddWithValue("$limit",limit);c.Parameters.AddWithValue("$offset",offset);
  using var r=c.ExecuteReader();var result=new List<ScanItem>();while(r.Read())result.Add(Read(r));return result;
 }
 public List<ScanItem> ItemsAll(CancellationToken token=default)
 {
  using var db=Open();using var c=db.CreateCommand();
  c.CommandText="SELECT id,path,size,modified,created,attributes,fileid,volume,links,safety,category,reason,rule,selected FROM entries WHERE isdir=0 ORDER BY safety ASC,size DESC";
  using var r=c.ExecuteReader();var result=new List<ScanItem>();while(r.Read()){token.ThrowIfCancellationRequested();result.Add(Read(r));}return result;
 }
 public List<ScanItem> ItemsRange(long firstId,long lastId,CancellationToken token=default)
 {
  if(lastId<firstId)return new();
  using var db=Open();using var c=db.CreateCommand();
  c.CommandText="SELECT id,path,size,modified,created,attributes,fileid,volume,links,safety,category,reason,rule,selected FROM entries WHERE isdir=0 AND id BETWEEN $first AND $last ORDER BY id";
  c.Parameters.AddWithValue("$first",firstId);c.Parameters.AddWithValue("$last",lastId);
  using var r=c.ExecuteReader();var result=new List<ScanItem>();while(r.Read()){token.ThrowIfCancellationRequested();result.Add(Read(r));}return result;
 }
 static ScanItem Read(SqliteDataReader r)=>new(r.GetInt64(0),new(r.GetString(1),r.GetInt64(2),new DateTime(r.GetInt64(3),DateTimeKind.Utc),new DateTime(r.GetInt64(4),DateTimeKind.Utc),(FileAttributes)r.GetInt64(5),ulong.TryParse(r.GetString(6),out var id)?id:0,(uint)r.GetInt64(7),(uint)r.GetInt64(8)),new((SafetyLevel)r.GetInt32(9),r.GetString(10),r.GetString(11),r.IsDBNull(12)?null:r.GetString(12)),r.GetInt32(13)==1);
 public long SelectedBytes(){using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT COALESCE(SUM(size),0) FROM entries WHERE selected=1 AND isdir=0";return Convert.ToInt64(c.ExecuteScalar());}
 public int SelectedCount(){using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT COUNT(*) FROM entries WHERE selected=1 AND isdir=0";return Convert.ToInt32(c.ExecuteScalar());}
 public void Select(long id,bool selected){using var db=Open();using var c=db.CreateCommand();c.CommandText="UPDATE entries SET selected=$s WHERE id=$id AND safety IN (0,1,2) AND isdir=0";c.Parameters.AddWithValue("$s",selected?1:0);c.Parameters.AddWithValue("$id",id);c.ExecuteNonQuery();}
 public void SelectMany(IEnumerable<long> ids,bool selected){using var db=Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="UPDATE entries SET selected=$s WHERE id=$id AND safety IN (0,1,2) AND isdir=0";var ps=c.Parameters.Add("$s",SqliteType.Integer);var pi=c.Parameters.Add("$id",SqliteType.Integer);ps.Value=selected?1:0;c.Prepare();foreach(var id in ids){pi.Value=id;c.ExecuteNonQuery();}tx.Commit();}
 public void SelectAllSafe(bool selected){using var db=Open();using var c=db.CreateCommand();c.CommandText="UPDATE entries SET selected=$s WHERE safety=0 AND isdir=0";c.Parameters.AddWithValue("$s",selected?1:0);c.ExecuteNonQuery();}
 public void ClearSelections(){using var db=Open();using var c=db.CreateCommand();c.CommandText="UPDATE entries SET selected=0 WHERE safety IN (0,1,2) AND isdir=0";c.ExecuteNonQuery();}
 public void ClearManualSelections(){using var db=Open();using var c=db.CreateCommand();c.CommandText="UPDATE entries SET selected=0 WHERE safety IN (1,2) AND isdir=0";c.ExecuteNonQuery();}
 public List<DirectoryStat> Children(string path,int limit=250){using var db=Open();using var c=db.CreateCommand();c.CommandText="SELECT path,name,size,isdir FROM entries WHERE parent=$p ORDER BY size DESC LIMIT $limit";c.Parameters.AddWithValue("$p",path);c.Parameters.AddWithValue("$limit",limit);using var r=c.ExecuteReader();var items=new List<DirectoryStat>();while(r.Read())items.Add(new(r.GetString(0),r.GetString(1),r.GetInt64(2),0,r.GetInt32(3)==1));return items;}
 public void SaveSummary(ScanSummary summary){using var db=Open();using var c=db.CreateCommand();c.CommandText="INSERT INTO summaries(started,summary) VALUES($d,$s)";c.Parameters.AddWithValue("$d",DateTimeOffset.UtcNow.ToString("O"));c.Parameters.AddWithValue("$s",System.Text.Json.JsonSerializer.Serialize(summary));c.ExecuteNonQuery();}
 public void SaveReport(CleanupReport report){using var db=Open();using var c=db.CreateCommand();c.CommandText="INSERT INTO reports(started,report) VALUES($d,$s)";c.Parameters.AddWithValue("$d",report.StartedAt.ToString("O"));c.Parameters.AddWithValue("$s",System.Text.Json.JsonSerializer.Serialize(report));c.ExecuteNonQuery();}

 public ExitDatabaseCleanupResult CleanupForExit()
 {
  var beforeBytes=SafeLength(DatabasePath)+SafeLength(DatabasePath+"-wal")+SafeLength(DatabasePath+"-shm");
  long removedEntries=0,removedReports=0;
  var token=Guid.NewGuid().ToString("N");
  var temp=DatabasePath+".exit-compact-"+token+".tmp";
  var backup=DatabasePath+".exit-old-"+token;
  var hadOriginal=File.Exists(DatabasePath);
  var replacementInstalled=false;
  var installedVerified=false;
  string? rollbackError=null;

  try
  {
   // Microsoft.Data.Sqlite pools connections by default. Empty all idle pools
   // before the replacement phase so a disposed UI/scanner connection cannot
   // keep CleanC.db, -wal or -shm open on Windows.
   SqliteConnection.ClearAllPools();

   var summaries=new List<(long Id,string? Started,string? Summary)>();
   using(var old=Open())
   {
    using(var count=old.CreateCommand())
    {
     count.CommandText="SELECT (SELECT COUNT(*) FROM entries),(SELECT COUNT(*) FROM reports)";
     using var r=count.ExecuteReader();
     if(r.Read()){removedEntries=r.GetInt64(0);removedReports=r.GetInt64(1);}
    }

    using(var read=old.CreateCommand())
    {
     read.CommandText="SELECT id,started,summary FROM summaries ORDER BY id";
     using var r=read.ExecuteReader();
     while(r.Read())summaries.Add((
      r.GetInt64(0),
      r.IsDBNull(1)?null:r.GetString(1),
      r.IsDBNull(2)?null:r.GetString(2)));
    }

    // Make the original main file self-contained before replacement. A busy
    // result means another SQLite user still exists, so do not touch the files.
    using(var checkpoint=old.CreateCommand())
    {
     checkpoint.CommandText="PRAGMA wal_checkpoint(TRUNCATE);";
     using var r=checkpoint.ExecuteReader();
     if(r.Read()&&r.GetInt64(0)!=0)
      throw new IOException("CleanC.db WAL checkpoint is busy; database replacement was not started.");
    }
   }

   // Disposing a pooled connection normally returns it to the pool. Explicitly
   // empty the pool again before rename/delete operations.
   SqliteConnection.ClearAllPools();
   DeleteFileOrThrow(DatabasePath+"-wal");
   DeleteFileOrThrow(DatabasePath+"-shm");

   // Build the replacement under a unique name in the SAME directory/volume.
   // Pooling is disabled for maintenance-only connections so the temp file is
   // guaranteed to be released before File.Replace/File.Move.
   using(var compact=OpenMaintenance(temp,SqliteOpenMode.ReadWriteCreate))
   {
    using(var schema=compact.CreateCommand())
    {
     schema.CommandText="""
     PRAGMA journal_mode=DELETE;
     PRAGMA synchronous=FULL;
     CREATE TABLE entries(id INTEGER PRIMARY KEY,path TEXT NOT NULL,parent TEXT NOT NULL,name TEXT NOT NULL,size INTEGER NOT NULL,modified INTEGER NOT NULL,created INTEGER NOT NULL,attributes INTEGER NOT NULL,safety INTEGER NOT NULL,category TEXT NOT NULL,reason TEXT NOT NULL,rule TEXT,fileid TEXT,volume INTEGER,links INTEGER,isdir INTEGER NOT NULL,selected INTEGER NOT NULL);
     CREATE INDEX idx_path ON entries(path);
     CREATE INDEX idx_parent ON entries(parent);
     CREATE INDEX idx_safety ON entries(safety,selected);
     CREATE TABLE summaries(id INTEGER PRIMARY KEY,started TEXT,summary TEXT);
     CREATE TABLE reports(id INTEGER PRIMARY KEY,started TEXT,report TEXT);
     """;
     schema.ExecuteNonQuery();
    }

    using var tx=compact.BeginTransaction();
    using var insert=compact.CreateCommand();
    insert.Transaction=tx;
    insert.CommandText="INSERT INTO summaries(id,started,summary) VALUES($id,$started,$summary)";
    var id=insert.Parameters.Add("$id",SqliteType.Integer);
    var started=insert.Parameters.Add("$started",SqliteType.Text);
    var summary=insert.Parameters.Add("$summary",SqliteType.Text);
    insert.Prepare();
    foreach(var row in summaries)
    {
     id.Value=row.Id;
     started.Value=(object?)row.Started??DBNull.Value;
     summary.Value=(object?)row.Summary??DBNull.Value;
     insert.ExecuteNonQuery();
    }
    tx.Commit();

    using var optimize=compact.CreateCommand();
    optimize.CommandText="PRAGMA optimize;";
    optimize.ExecuteNonQuery();
   }

   SqliteConnection.ClearAllPools();
   DeleteFileOrThrow(temp+"-wal");
   DeleteFileOrThrow(temp+"-shm");

   // Do not replace the old DB until the new file independently passes SQLite
   // integrity and content checks.
   VerifyCompactDatabase(temp,summaries.Count);
   SqliteConnection.ClearAllPools();

   if(hadOriginal)
   {
    // File.Replace is a single Windows replacement operation. The previous main
    // file is kept only as a short-lived rollback backup until post-check passes.
    File.Replace(temp,DatabasePath,backup,true);
   }
   else File.Move(temp,DatabasePath,false);
   replacementInstalled=true;

   // Verify the installed path, not merely the temp source. Only after this check
   // is it safe to delete the old DB backup and report cleanup success.
   VerifyCompactDatabase(DatabasePath,summaries.Count);
   installedVerified=true;
   SqliteConnection.ClearAllPools();
   DeleteFileOrThrow(DatabasePath+"-wal");
   DeleteFileOrThrow(DatabasePath+"-shm");
   DeleteFileOrThrow(backup);
   DeleteFileOrThrow(backup+"-wal");
   DeleteFileOrThrow(backup+"-shm");
   DeleteFileOrThrow(temp);
   DeleteFileOrThrow(temp+"-wal");
   DeleteFileOrThrow(temp+"-shm");

   var afterBytes=SafeLength(DatabasePath)+SafeLength(DatabasePath+"-wal")+SafeLength(DatabasePath+"-shm");
   return new(true,removedEntries,removedReports,beforeBytes,afterBytes,null);
  }
  catch(Exception e)
  {
   try{SqliteConnection.ClearAllPools();}catch{}

   // If the atomic replacement completed but the installed file did not verify,
   // put the original main DB back. A verified new DB is never rolled back merely
   // because deleting its already-obsolete backup was temporarily blocked.
   if(replacementInstalled&&!installedVerified&&hadOriginal&&File.Exists(backup))
   {
    try
    {
     TryDeleteSidecar(DatabasePath+"-wal");
     TryDeleteSidecar(DatabasePath+"-shm");
     if(File.Exists(DatabasePath))File.Replace(backup,DatabasePath,null,true);
     else File.Move(backup,DatabasePath,false);
    }
    catch(Exception rollback){rollbackError=rollback.Message;}
   }
   else if(replacementInstalled&&!installedVerified&&!hadOriginal)
   {
    TryDeleteSidecar(DatabasePath);
    TryDeleteSidecar(DatabasePath+"-wal");
    TryDeleteSidecar(DatabasePath+"-shm");
   }

   TryDeleteSidecar(temp);TryDeleteSidecar(temp+"-wal");TryDeleteSidecar(temp+"-shm");
   var afterBytes=SafeLength(DatabasePath)+SafeLength(DatabasePath+"-wal")+SafeLength(DatabasePath+"-shm");
   var error=rollbackError is null?e.Message:e.Message+" | rollback failed: "+rollbackError;
   return new(false,removedEntries,removedReports,beforeBytes,afterBytes,error);
  }
 }

 static SqliteConnection OpenMaintenance(string path,SqliteOpenMode mode)
 {
  var c=new SqliteConnection(new SqliteConnectionStringBuilder{
   DataSource=path,DefaultTimeout=5,Mode=mode,Pooling=false}.ToString());
  c.Open();return c;
 }

 static void VerifyCompactDatabase(string path,long expectedSummaries)
 {
  using var db=OpenMaintenance(path,SqliteOpenMode.ReadWrite);
  using(var check=db.CreateCommand())
  {
   check.CommandText="PRAGMA quick_check;";
   var result=Convert.ToString(check.ExecuteScalar());
   if(!string.Equals(result,"ok",StringComparison.OrdinalIgnoreCase))
    throw new InvalidDataException("SQLite quick_check failed: "+(result??"<null>"));
  }
  using(var count=db.CreateCommand())
  {
   count.CommandText="SELECT (SELECT COUNT(*) FROM entries),(SELECT COUNT(*) FROM summaries),(SELECT COUNT(*) FROM reports)";
   using var r=count.ExecuteReader();
   if(!r.Read())throw new InvalidDataException("Unable to verify compact database row counts.");
   var entries=r.GetInt64(0);var summaries=r.GetInt64(1);var reports=r.GetInt64(2);
   if(entries!=0||reports!=0||summaries!=expectedSummaries)
    throw new InvalidDataException($"Compact database verification mismatch: entries={entries}, summaries={summaries}/{expectedSummaries}, reports={reports}.");
  }
 }

 static long SafeLength(string path)
 {
  try{return File.Exists(path)?new FileInfo(path).Length:0;}catch{return 0;}
 }

 static void DeleteFileOrThrow(string path)
 {
  if(!File.Exists(path))return;
  Exception? last=null;
  for(var attempt=0;attempt<8;attempt++)
  {
   try
   {
    File.Delete(path);
    if(!File.Exists(path))return;
   }
   catch(Exception e) when(e is IOException or UnauthorizedAccessException){last=e;}
   Thread.Sleep(75*(attempt+1));
  }
  if(File.Exists(path))throw new IOException("Unable to remove database maintenance file: "+path,last);
 }

 static void TryDeleteSidecar(string path)
 {
  try{if(File.Exists(path))File.Delete(path);}catch{}
 }
 public void MarkCleaned(IEnumerable<string> paths)=>RemovePaths(paths);
 public void RemovePaths(IEnumerable<string> paths)
 {
  using var db=Open();using var tx=db.BeginTransaction();using var c=db.CreateCommand();c.Transaction=tx;
  c.CommandText="DELETE FROM entries WHERE path=$p";var parameter=c.Parameters.Add("$p",SqliteType.Text);c.Prepare();
  foreach(var p in paths.Distinct(StringComparer.OrdinalIgnoreCase)){parameter.Value=p;c.ExecuteNonQuery();}
  tx.Commit();
 }
 public void UpdateItem(long id,FileSnapshot f,Classification kind,bool selected=false)
 {
  using var db=Open();using var c=db.CreateCommand();
  c.CommandText="""
 UPDATE entries SET size=$size,modified=$modified,created=$created,attributes=$attrs,
 safety=$safety,category=$category,reason=$reason,rule=$rule,fileid=$fileid,volume=$volume,links=$links,selected=$selected
 WHERE id=$id AND isdir=0
 """;
  c.Parameters.AddWithValue("$size",f.Size);c.Parameters.AddWithValue("$modified",f.LastWriteUtc.Ticks);c.Parameters.AddWithValue("$created",f.CreationUtc.Ticks);
  c.Parameters.AddWithValue("$attrs",(long)f.Attributes);c.Parameters.AddWithValue("$safety",(int)kind.Safety);c.Parameters.AddWithValue("$category",kind.Category);
  c.Parameters.AddWithValue("$reason",kind.Reason);c.Parameters.AddWithValue("$rule",(object?)kind.RuleId??DBNull.Value);c.Parameters.AddWithValue("$fileid",f.FileId.ToString());
  c.Parameters.AddWithValue("$volume",(long)f.Volume);c.Parameters.AddWithValue("$links",(long)f.Links);c.Parameters.AddWithValue("$selected",selected&&kind.Safety!=SafetyLevel.Protected?1:0);
  c.Parameters.AddWithValue("$id",id);c.ExecuteNonQuery();
 }

 public void ApplyItemChanges(IReadOnlyList<(long Id,FileSnapshot File,Classification Kind,bool Selected)> updates,IEnumerable<string> removedPaths)
 {
  using var db=Open();using var tx=db.BeginTransaction();
  if(updates.Count>0)
  {
   using var update=db.CreateCommand();update.Transaction=tx;
   update.CommandText="""
 UPDATE entries SET size=$size,modified=$modified,created=$created,attributes=$attrs,
 safety=$safety,category=$category,reason=$reason,rule=$rule,fileid=$fileid,volume=$volume,links=$links,selected=$selected
 WHERE id=$id AND isdir=0
 """;
   var pSize=update.Parameters.Add("$size",SqliteType.Integer);var pModified=update.Parameters.Add("$modified",SqliteType.Integer);var pCreated=update.Parameters.Add("$created",SqliteType.Integer);var pAttrs=update.Parameters.Add("$attrs",SqliteType.Integer);
   var pSafety=update.Parameters.Add("$safety",SqliteType.Integer);var pCategory=update.Parameters.Add("$category",SqliteType.Text);var pReason=update.Parameters.Add("$reason",SqliteType.Text);var pRule=update.Parameters.Add("$rule",SqliteType.Text);
   var pFileId=update.Parameters.Add("$fileid",SqliteType.Text);var pVolume=update.Parameters.Add("$volume",SqliteType.Integer);var pLinks=update.Parameters.Add("$links",SqliteType.Integer);var pSelected=update.Parameters.Add("$selected",SqliteType.Integer);var pId=update.Parameters.Add("$id",SqliteType.Integer);update.Prepare();
   foreach(var row in updates)
   {
    var f=row.File;var kind=row.Kind;pSize.Value=f.Size;pModified.Value=f.LastWriteUtc.Ticks;pCreated.Value=f.CreationUtc.Ticks;pAttrs.Value=(long)f.Attributes;pSafety.Value=(int)kind.Safety;pCategory.Value=kind.Category;pReason.Value=kind.Reason;pRule.Value=(object?)kind.RuleId??DBNull.Value;pFileId.Value=f.FileId.ToString();pVolume.Value=(long)f.Volume;pLinks.Value=(long)f.Links;pSelected.Value=row.Selected&&kind.Safety!=SafetyLevel.Protected?1:0;pId.Value=row.Id;update.ExecuteNonQuery();
   }
  }
  using(var delete=db.CreateCommand())
  {
   delete.Transaction=tx;delete.CommandText="DELETE FROM entries WHERE path=$p";var p=delete.Parameters.Add("$p",SqliteType.Text);delete.Prepare();
   foreach(var path in removedPaths.Distinct(StringComparer.OrdinalIgnoreCase)){p.Value=path;delete.ExecuteNonQuery();}
  }
  tx.Commit();
 }

}
