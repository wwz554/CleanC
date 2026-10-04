using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using CleanC.Core;
namespace CleanC.Licensing;
public interface IDeviceIdentity {string DeviceId{get;}string PublicKeyPem{get;}string Sign(string nonce);bool CanSign=>true;}
public sealed class DeviceIdentity : IDeviceIdentity,IDisposable
{
 private readonly CngKey? key;
 public bool CanSign=>key is not null;
 public string DeviceId {get;}
 public string PublicKeyPem {get;}
 private sealed record IdentityRecord(string KeyName,string Provider,string DeviceId,string? PublicKeyPem=null);
 public DeviceIdentity(IProtectedStore store)
 {
  IdentityRecord? saved=null;
  try{saved=store.Read<IdentityRecord>("device.dat");}
  catch(Exception e) when(e is CryptographicException or IOException or JsonException){throw new LicenseException("DEVICE_STORAGE","设备身份记录无法读取，已保留原数据，拒绝自动生成另一份身份。请联系管理员恢复设备授权。");}

  using var reg=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
  var machine=(string?)reg?.GetValue("MachineGuid")??throw new IOException("无法读取设备身份。");
  var deterministicName="CleanC.Device."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machine+AppPaths.UserSid)))[..24];

  key=OpenExisting(saved,deterministicName);
  if(key is null&&saved is not null){DeviceId=saved.DeviceId;PublicKeyPem=saved.PublicKeyPem??"";return;}
  if(key is null){
   if(store.Read<SavedLicense>("license.dat") is not null||store.Read<OfflineActivationRecord>("offline-license.dat") is not null)
    throw new LicenseException("DEVICE_KEY_MISSING","原授权仍在，但设备密钥无法打开。已保留原记录，拒绝静默更换设备身份；请联系管理员恢复或明确解绑重激活。");
   key=CreateStable(deterministicName);
  }
  using var ec=new ECDsaCng(key);
  PublicKeyPem=ec.ExportSubjectPublicKeyInfoPem();
  DeviceId="DEVICE-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machine+"|"+AppPaths.UserSid+"|"+PublicKeyPem)))[..32];
  if(saved is not null&&DeviceId!=saved.DeviceId){key.Dispose();key=null;DeviceId=saved.DeviceId;PublicKeyPem=saved.PublicKeyPem??"";return;}

  // Upgrade never replaces a persisted identity because a TPM/CNG key vanished.
  try{store.Write("device.dat",new IdentityRecord(key.KeyName??deterministicName,key.Provider?.Provider??CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider,DeviceId,PublicKeyPem));}catch{}
 }
 static CngKey? OpenExisting(IdentityRecord? saved,string deterministicName)
 {
  var candidates=new List<(string Name,CngProvider Provider)>();
  if(saved is not null)
  {
   try{candidates.Add((saved.KeyName,new CngProvider(saved.Provider)));}catch{}
  }
  candidates.Add((deterministicName,CngProvider.MicrosoftSoftwareKeyStorageProvider));
  try{candidates.Add((deterministicName,new CngProvider("Microsoft Platform Crypto Provider")));}catch{}
  foreach(var c in candidates)
  {
   try{if(CngKey.Exists(c.Name,c.Provider))return CngKey.Open(c.Name,c.Provider,CngKeyOpenOptions.Silent);}
   catch(CryptographicException){}
   catch(PlatformNotSupportedException){}
  }
  return null;
 }
 static CngKey CreateStable(string name)
 {
  // Software CNG is the compatibility baseline for Win10/Win11 and survives normal reboots.
  // Existing TPM-backed identities are still reused by OpenExisting when available.
  try{return Create(name,CngProvider.MicrosoftSoftwareKeyStorageProvider);}
  catch(CryptographicException)
  {
   var tpm=new CngProvider("Microsoft Platform Crypto Provider");
   return Create(name,tpm);
  }
 }
 static CngKey Create(string name,CngProvider provider)=>CngKey.Create(CngAlgorithm.ECDsaP256,name,new CngKeyCreationParameters{Provider=provider,ExportPolicy=CngExportPolicies.None,KeyUsage=CngKeyUsages.Signing});
 public string Sign(string nonce){if(key is null)throw new LicenseException("DEVICE_KEY_MISSING","原设备密钥不可用，原授权与设备码已保留，请联系管理员恢复或明确解绑重激活。");if(nonce.Length is <16 or >256)throw new LicenseException("INVALID_CHALLENGE","服务器设备挑战格式异常。");using var ec=new ECDsaCng(key);return SignatureVerifier.Encode(ec.SignData(Encoding.UTF8.GetBytes(nonce),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));}
 public void Dispose()=>key?.Dispose();
}
