using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
namespace CleanC.Core;

public static class NativeFileSnapshot
{
 public static bool TryRead(string path,out FileSnapshot snapshot)
 {
  snapshot=new(path,0,DateTime.UnixEpoch,DateTime.UnixEpoch,0);
  try
  {
   using var pin=new PinnedFile(path,cooperativeShare:true);
   snapshot=pin.Snapshot;
   return snapshot.FileId!=0&&snapshot.Volume!=0&&snapshot.Links>=1;
  }
  catch(Exception e) when(e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException or NotSupportedException)
  {
   return false;
  }
 }
}

/// <summary>Holds each ancestor against rename and the leaf exclusively. Never follows reparse points.</summary>
public sealed class PinnedFile : IDisposable
{
 const uint ReadAttributes=0x80, DeleteAccess=0x10000, GenericRead=0x80000000;
 private readonly List<SafeFileHandle> parents=[];
 public SafeFileHandle Handle {get;}
 public FileSnapshot Snapshot {get;}
 public PinnedFile(string path,bool deleteAccess=false,bool readContent=false,PinnedDirectoryChain? directoryPin=null,bool cooperativeShare=false)
 {
  path=Path.GetFullPath(path);
  if(!Path.IsPathFullyQualified(path)||path.StartsWith(@"\\")||path[2..].Contains(':'))throw new IOException("仅支持本地普通文件。");
  string root=Path.GetPathRoot(path)!;
  try {
   if(directoryPin is null)
   {
    string cur=root;
    var parts=Path.GetRelativePath(root,path).Split(Path.DirectorySeparatorChar);
    for(int i=0;i<parts.Length-1;i++) {
     cur=Path.Combine(cur,parts[i]);
     var h=Open(cur,ReadAttributes,3,0x02200000); parents.Add(h);
     var info=Info(h);
     if(((FileAttributes)info.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("链接目录已保护。");
    }
   }
   else if(!directoryPin.Covers(path))throw new IOException("目录锁与文件路径不匹配。");
   Handle=Open(path,ReadAttributes|(deleteAccess?DeleteAccess:0)|(readContent?GenericRead:0),cooperativeShare?7u:0u,0x00200000);
   var f=Info(Handle);
   if(((FileAttributes)f.Attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)throw new IOException("链接或目录已保护。");
   var sb=new StringBuilder(32768);
   uint len=GetFinalPathNameByHandle(Handle,sb,(uint)sb.Capacity,0);
   if(len==0||len>=sb.Capacity)throw new Win32Exception(Marshal.GetLastWin32Error());
   var final=sb.ToString();
   if(final.StartsWith(@"\\?\",StringComparison.Ordinal)) final=final[4..];
   if(!string.Equals(final,path,StringComparison.OrdinalIgnoreCase))throw new IOException("路径已变化。");
   Snapshot=new(path,((long)f.SizeHigh<<32)|f.SizeLow,FileTime(f.Write),FileTime(f.Created),(FileAttributes)f.Attributes,((ulong)f.IndexHigh<<32)|f.IndexLow,f.Volume,f.Links);
  } catch { Dispose();throw; }
 }
 static DateTime FileTime(System.Runtime.InteropServices.ComTypes.FILETIME t)=>DateTime.FromFileTimeUtc(((long)t.dwHighDateTime<<32)|(uint)t.dwLowDateTime);
 static SafeFileHandle Open(string p,uint access,uint share,uint flags) {var h=CreateFile(p,access,share,IntPtr.Zero,3,flags,IntPtr.Zero);if(h.IsInvalid){h.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}return h;}
 static ByHandle Info(SafeFileHandle h){if(!GetFileInformationByHandle(h,out var f))throw new Win32Exception(Marshal.GetLastWin32Error());return f;}
 public void MarkForDeletion()
 {
  // Windows 10+ fast permanent delete: bypasses Recycle Bin. If the extended
  // disposition mode is unavailable for a particular filesystem, fall back
  // to the legacy handle-delete flag. Neither path creates a recovery copy.
  var ex=new DispositionEx{Flags=0x1|0x2|0x10};
  if(SetFileInformationByHandleEx(Handle,21,ref ex,4))return;
  var d=new Disposition{Delete=true};if(!SetFileInformationByHandle(Handle,4,ref d,1))throw new Win32Exception(Marshal.GetLastWin32Error());
 }
 public void Dispose(){Handle?.Dispose();for(int i=parents.Count-1;i>=0;i--)parents[i].Dispose();parents.Clear();}
 [StructLayout(LayoutKind.Sequential)]struct ByHandle{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Created,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
 [StructLayout(LayoutKind.Sequential)]struct Disposition{[MarshalAs(UnmanagedType.U1)]public bool Delete;}
 [StructLayout(LayoutKind.Sequential)]struct DispositionEx{public uint Flags;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string p,uint access,uint share,IntPtr sec,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle handle,out ByHandle info);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern uint GetFinalPathNameByHandle(SafeFileHandle handle,StringBuilder path,uint count,uint flags);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool SetFileInformationByHandle(SafeFileHandle handle,int type,ref Disposition data,uint size);
 [DllImport("kernel32.dll",EntryPoint="SetFileInformationByHandle",SetLastError=true)]static extern bool SetFileInformationByHandleEx(SafeFileHandle handle,int type,ref DispositionEx data,uint size);
}

/// <summary>
/// Pins one parent-directory chain for a batch of files in the same directory.
/// This preserves the anti-rename/reparse safety model without reopening every
/// ancestor for every individual file.
/// </summary>
public sealed class PinnedDirectoryChain : IDisposable
{
 const uint ReadAttributes=0x80;
 readonly List<SafeFileHandle> handles=[];
 readonly string directory;
 public PinnedDirectoryChain(string path)
 {
  var full=Path.GetFullPath(path);var root=Path.GetPathRoot(full)!;directory=string.Equals(full,root,StringComparison.OrdinalIgnoreCase)?root:full.TrimEnd(Path.DirectorySeparatorChar);
  if(!Path.IsPathFullyQualified(directory)||directory.StartsWith(@"\\")||(directory.Length>2&&directory[2..].Contains(':')))throw new IOException("仅支持本地普通目录。");
  var relative=Path.GetRelativePath(root,directory);
  if(relative==".")return;
  var current=root;
  try
  {
   foreach(var part in relative.Split(Path.DirectorySeparatorChar,StringSplitOptions.RemoveEmptyEntries))
   {
    current=Path.Combine(current,part);var h=Open(current);handles.Add(h);
    if(((FileAttributes)Info(h).Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("链接目录已保护。");
   }
  }
  catch{Dispose();throw;}
 }
 public bool Covers(string file)
 {
  var full=Path.GetDirectoryName(Path.GetFullPath(file))!;var root=Path.GetPathRoot(full)!;var parent=string.Equals(full,root,StringComparison.OrdinalIgnoreCase)?root:full.TrimEnd(Path.DirectorySeparatorChar);
  return string.Equals(parent,directory,StringComparison.OrdinalIgnoreCase);
 }
 static SafeFileHandle Open(string path){var h=CreateFile(path,ReadAttributes,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);if(h.IsInvalid){h.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}return h;}
 static ByHandle Info(SafeFileHandle h){if(!GetFileInformationByHandle(h,out var f))throw new Win32Exception(Marshal.GetLastWin32Error());return f;}
 public void Dispose(){for(int i=handles.Count-1;i>=0;i--)handles[i].Dispose();handles.Clear();}
 [StructLayout(LayoutKind.Sequential)]struct ByHandle{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Created,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern SafeFileHandle CreateFile(string p,uint access,uint share,IntPtr sec,uint creation,uint flags,IntPtr template);
 [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetFileInformationByHandle(SafeFileHandle handle,out ByHandle info);
}

