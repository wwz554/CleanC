using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CleanC.Core;
namespace CleanC.Licensing;
public interface IProtectedStore {T? Read<T>(string name);void Write<T>(string name,T value);}
public sealed class ProtectedStore : IProtectedStore
{
 private readonly string directory;
 private static readonly byte[] Entropy=Encoding.UTF8.GetBytes("CleanC/License/v4");
 public ProtectedStore(string? path=null){directory=path??AppPaths.License;AppPaths.EnsurePrivate(directory);}
 public T? Read<T>(string name){var p=Path.Combine(directory,name);if(!File.Exists(p))return default;if((File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("授权文件不能是链接。");var bytes=ProtectedData.Unprotect(File.ReadAllBytes(p),Entropy,DataProtectionScope.CurrentUser);try{return JsonSerializer.Deserialize<T>(bytes,SignatureVerifier.Json);}finally{CryptographicOperations.ZeroMemory(bytes);}}
 public void Write<T>(string name,T value)
 {
  var raw=JsonSerializer.SerializeToUtf8Bytes(value,SignatureVerifier.Json);
  try { var encrypted=ProtectedData.Protect(raw,Entropy,DataProtectionScope.CurrentUser);var path=Path.Combine(directory,name);var temp=path+".tmp";using(var fs=new FileStream(temp,FileMode.Create,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){fs.Write(encrypted);fs.Flush(true);}File.Move(temp,path,true); }
  finally{CryptographicOperations.ZeroMemory(raw);}
 }
}
