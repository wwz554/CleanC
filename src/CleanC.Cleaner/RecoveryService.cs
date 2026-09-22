using System.Security.Cryptography;
using System.Text.Json;
using CleanC.Core;
using CleanC.Logging;
namespace CleanC.Cleaner;
public sealed record RecoveryItem(string Id,string OriginalPath,string StoredPath,string Sha256,DateTimeOffset SavedUtc,DateTimeOffset RetainUntilUtc,long Size);
/// <summary>User-confirmed quarantine, never automatically purge. Does not infer abandoned applications.</summary>
public sealed class RecoveryService(ICapabilityGate gate,AuditLog log,string? directory=null)
{
 readonly string root=directory??AppPaths.Recovery;
 public RecoveryItem Quarantine(FileSnapshot expected,SafetyPolicy policy,CancellationToken token=default)
 {
  gate.Demand(FeatureCapability.Cleanup);Directory.CreateDirectory(root);
  using var pin=new PinnedFile(expected.Path,deleteAccess:true,readContent:true);
  var f=pin.Snapshot;
  if(f.FileId!=expected.FileId||f.Volume!=expected.Volume||f.LastWriteUtc!=expected.LastWriteUtc||f.CreationUtc!=expected.CreationUtc||f.Size!=expected.Size||f.Links!=1)throw new IOException("文件已变化，停止移动。");
  var kind=policy.Classify(f,DateTime.UtcNow,true);
  if(kind.Safety==SafetyLevel.Protected)throw new IOException("受保护文件不能移动。");
  var id=Guid.NewGuid().ToString("N");var stored=Path.Combine(root,id+".bin");
  using var source=new FileStream(pin.Handle,FileAccess.Read);
  using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
  using(var output=new FileStream(stored,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
   byte[] buffer=new byte[128*1024];int n;while((n=source.Read(buffer))>0){token.ThrowIfCancellationRequested();gate.Demand(FeatureCapability.Cleanup);output.Write(buffer,0,n);hash.AppendData(buffer,0,n);}output.Flush(true);
  }
  var now=DateTimeOffset.UtcNow;var item=new RecoveryItem(id,f.Path,stored,Convert.ToHexString(hash.GetHashAndReset()),now,now.AddDays(7),f.Size);
  using(var manifest=new FileStream(Path.Combine(root,id+".json"),FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(manifest,item);manifest.Flush(true);}
  token.ThrowIfCancellationRequested();gate.Demand(FeatureCapability.Cleanup);
  log.Write("Recovery","QuarantineIntent","BackupVerified",f.Path);
  pin.MarkForDeletion();log.Write("Recovery","Quarantine","Completed",f.Path);return item;
 }
 public IReadOnlyList<RecoveryItem> List()
 {
  Directory.CreateDirectory(root);var result=new List<RecoveryItem>();
  foreach(var path in Directory.EnumerateFiles(root,"*.json")){try{var item=JsonSerializer.Deserialize<RecoveryItem>(File.ReadAllText(path));if(item is not null)result.Add(item);}catch(JsonException){log.Write("Recovery","Read","InvalidManifest",path);}}
  return result.OrderByDescending(x=>x.SavedUtc).ToList();
 }
 public void Restore(RecoveryItem item)
 {
  gate.Demand(FeatureCapability.Cleanup);
  var stored=Path.GetFullPath(item.StoredPath);
  if(!SafetyPolicy.Within(stored,root)||Path.GetFileName(stored)!=item.Id+".bin")throw new IOException("恢复记录路径无效。");
  var target=Path.GetFullPath(item.OriginalPath);
  if(File.Exists(target)||Directory.Exists(target))throw new IOException("原位置已有文件，停止恢复以避免覆盖。");
  var parent=Path.GetDirectoryName(target)??throw new IOException("恢复目标没有父目录。");
  if(SafetyPolicy.HasReparseAncestor(parent))throw new IOException("目标目录包含链接，停止恢复。");
  Directory.CreateDirectory(parent);
  if(SafetyPolicy.HasReparseAncestor(parent))throw new IOException("目标目录创建后检测到链接，停止恢复。");
  using var pin=new PinnedFile(stored,readContent:true);
  using var stream=new FileStream(pin.Handle,FileAccess.Read);
  var hash=Convert.ToHexString(SHA256.HashData(stream));if(hash!=item.Sha256)throw new IOException("恢复文件校验失败。");
  stream.Position=0;
  // CreateNew guarantees no overwriting of existing user data.
  using(var output=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.None)){stream.CopyTo(output);output.Flush(true);}
  log.Write("Recovery","Restore","Completed",target);
 }
}
