using CleanC.Core;
namespace CleanC.Cleaner;
public sealed class UserFileService(ICapabilityGate gate,SafetyPolicy policy)
{
 public FileSnapshot Preview(string path)
 {
  gate.Demand(FeatureCapability.Cleanup);
  using var file=new PinnedFile(path);
  if(policy.Classify(file.Snapshot,DateTime.UtcNow,true).Safety==SafetyLevel.Protected)throw new IOException("此文件受保护，不能永久删除。");
  return file.Snapshot;
 }
 public void DeletePermanent(FileSnapshot expected,CancellationToken token)
 {
  gate.Demand(FeatureCapability.Cleanup);token.ThrowIfCancellationRequested();
  var directory=Path.GetDirectoryName(expected.Path)??Path.GetPathRoot(expected.Path)!;
  using var directoryPin=new PinnedDirectoryChain(directory);
  using var file=new PinnedFile(expected.Path,deleteAccess:true,directoryPin:directoryPin);
  var current=file.Snapshot;
  if((expected.FileId!=0&&(current.FileId!=expected.FileId||current.Volume!=expected.Volume))||current.Size!=expected.Size||current.LastWriteUtc!=expected.LastWriteUtc||current.CreationUtc!=expected.CreationUtc||current.Attributes!=expected.Attributes||current.Links!=1)throw new IOException("文件已变化，停止删除。");
  if(policy.Classify(current,DateTime.UtcNow,true).Safety==SafetyLevel.Protected)throw new IOException("此文件受保护，不能永久删除。");
  gate.Demand(FeatureCapability.Cleanup);token.ThrowIfCancellationRequested();file.MarkForDeletion();
 }
}
