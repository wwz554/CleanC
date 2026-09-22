using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using CleanC.Core;
using CleanC.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;
namespace CleanC.Scanner;
/// <summary>NTFS USN/MFT name index; file sizes and timestamps are re-read from live file metadata.</summary>
public sealed class MftIndex : IDisposable
{
 readonly SqliteConnection db;
 readonly Dictionary<string,string> directories=new(StringComparer.OrdinalIgnoreCase);
 MftIndex(ScanDatabase database){db=database.Open();}
 public static MftIndex? TryCreate(string root,ScanDatabase database,AuditLog log,CancellationToken token)
 {
  if(!string.Equals(root,Path.GetPathRoot(root),StringComparison.OrdinalIgnoreCase)||new DriveInfo(root).DriveFormat!="NTFS")return null;
  using var volume=CreateFile(@"\\.\"+root[..2],0x80000000,3,IntPtr.Zero,3,0,IntPtr.Zero);
  if(volume.IsInvalid){log.Write("Scanner","MFT","Fallback",detail:"Volume access unavailable");return null;}
  var index=new MftIndex(database);
  try{index.Build(volume,root,token);return index;}
  catch(Exception e)when(e is IOException or Win32Exception or SqliteException or InvalidDataException){index.Dispose();log.Write("Scanner","MFT","Fallback",detail:e.GetType().Name);return null;}
  catch{index.Dispose();throw;}
 }
 void Build(SafeFileHandle volume,string root,CancellationToken token)
 {
  using(var setup=db.CreateCommand()){setup.CommandText="DROP TABLE IF EXISTS mft; CREATE TABLE mft(id TEXT PRIMARY KEY,parent TEXT,name TEXT,attrs INTEGER);";setup.ExecuteNonQuery();}
  using var tx=db.BeginTransaction();using var insert=db.CreateCommand();insert.Transaction=tx;insert.CommandText="INSERT OR IGNORE INTO mft VALUES($id,$parent,$name,$attrs)";
  foreach(var n in new[]{"id","parent","name","attrs"})insert.Parameters.Add(new("$"+n,""));
  var dirs=new Dictionary<ulong,(ulong Parent,string Name,uint Attributes)>();
  var input=new EnumData{Start=0,Low=0,High=long.MaxValue};byte[] buffer=new byte[1024*1024];int records=0;
  while(true){
   token.ThrowIfCancellationRequested();
   if(!DeviceIoControl(volume,0x000900b3,ref input,Marshal.SizeOf<EnumData>(),buffer,buffer.Length,out var returned,IntPtr.Zero)){
    int error=Marshal.GetLastWin32Error();if(error==38)break;throw new Win32Exception(error);
   }
   if(returned<8)throw new InvalidDataException("MFT buffer");
   int offset=8;
   while(offset<returned){
    if(offset+60>returned)throw new InvalidDataException("USN record");
    int length=BitConverter.ToInt32(buffer,offset);ushort major=BitConverter.ToUInt16(buffer,offset+4);
    if(length<60||offset+length>returned||major!=2)throw new InvalidDataException("Unsupported USN record version");
    ulong id=BitConverter.ToUInt64(buffer,offset+8),parent=BitConverter.ToUInt64(buffer,offset+16);
    uint attrs=BitConverter.ToUInt32(buffer,offset+52);
    ushort nameLength=BitConverter.ToUInt16(buffer,offset+56),nameOffset=BitConverter.ToUInt16(buffer,offset+58);
    if(nameOffset+nameLength>length)throw new InvalidDataException("USN name");
    var name=Encoding.Unicode.GetString(buffer,offset+nameOffset,nameLength);
    if(name.IndexOfAny(['\\','/',':','\0'])<0){
     insert.Parameters[0].Value=id.ToString();insert.Parameters[1].Value=parent.ToString();insert.Parameters[2].Value=name;insert.Parameters[3].Value=attrs;insert.ExecuteNonQuery();
     if((attrs&16)!=0)dirs[id]=(parent,name,attrs);
    }
    offset+=length;records++;if(records%4096==0)token.ThrowIfCancellationRequested();
   }
   var next=BitConverter.ToUInt64(buffer,0);if(next<=input.Start)throw new InvalidDataException("MFT cursor");input.Start=next;
  }
  tx.Commit();
  using(var idx=db.CreateCommand()){idx.CommandText="CREATE INDEX IF NOT EXISTS idx_mft_parent ON mft(parent)";idx.ExecuteNonQuery();}
  var paths=new Dictionary<ulong,string>();
  string? Resolve(ulong id,int depth=0){
   if(depth>256)return null;
   if(paths.TryGetValue(id,out var known))return known;
   if((id&0x0000ffffffffffffUL)==5)return paths[id]=root;
   if(!dirs.TryGetValue(id,out var d)||(d.Attributes&0x400)!=0)return null;
   var parent=Resolve(d.Parent,depth+1);return parent is null?null:paths[id]=Path.Combine(parent,d.Name);
  }
  foreach(var d in dirs){var path=Resolve(d.Key);if(path is not null)directories[path]=d.Key.ToString();}
  if(!directories.ContainsKey(root))throw new InvalidDataException("MFT root unavailable");
 }
 public IEnumerable<FileSystemInfo> Entries(string directory)
 {
  if(!directories.TryGetValue(directory,out var id))yield break;
  using var c=db.CreateCommand();c.CommandText="SELECT name,attrs FROM mft WHERE parent=$p AND id<>parent";c.Parameters.AddWithValue("$p",id);using var r=c.ExecuteReader();
  while(r.Read()){
   var path=Path.Combine(directory,r.GetString(0));var attr=(FileAttributes)r.GetInt64(1);
   if((attr&FileAttributes.ReparsePoint)!=0)continue;
   yield return (attr&FileAttributes.Directory)!=0?new DirectoryInfo(path):new FileInfo(path);
  }
 }
 public void Dispose()=>db.Dispose();
 [StructLayout(LayoutKind.Sequential)]struct EnumData{public ulong Start;public long Low,High;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool DeviceIoControl(SafeFileHandle file,uint code,ref EnumData input,int inputSize,byte[] output,int outputSize,out int bytes,IntPtr overlapped);
}

