using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using CleanC.Core;
namespace CleanC.Licensing;
public interface IDeviceIdentity {string DeviceId{get;}string PublicKeyPem{get;}string Sign(string nonce);}
public sealed class DeviceIdentity : IDeviceIdentity,IDisposable
{
 private readonly CngKey key;
 public string DeviceId {get;}
 public string PublicKeyPem {get;}
 private sealed record IdentityRecord(string KeyName,string Provider,string DeviceId);
 public DeviceIdentity(IProtectedStore store)
 {
  IdentityRecord? saved=null;
  try{saved=store.Read<IdentityRecord>("device.dat");}
  catch(Exception e) when(e is CryptographicException or IOException or JsonException){saved=null;}

  using var reg=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
  var machine=(string?)reg?.GetValue("MachineGuid")??throw new IOException("无法读取设备身份。");
  var deterministicName="CleanC.Device."+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machine+AppPaths.UserSid)))[..24];

  key=OpenExisting(saved,deterministicName)??CreateStable(deterministicName);
  using var ec=new ECDsaCng(key);
  PublicKeyPem=ec.ExportSubjectPublicKeyInfoPem();
  DeviceId="DEVICE-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(machine+"|"+AppPaths.UserSid+"|"+PublicKeyPem)))[..32];

  // Do not crash application startup if an old TPM/CNG key disappeared after firmware,
  // Windows or provider changes. The signed lease validation will detect a changed DeviceId
  // and present the normal re-validation/re-activation UI instead.
  try{store.Write("device.dat",new IdentityRecord(key.KeyName??deterministicName,key.Provider?.Provider??CngProvider.MicrosoftSoftwareKeyStorageProvider.Provider,DeviceId));}catch{}
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
 public string Sign(string nonce){if(nonce.Length is <16 or >256)throw new LicenseException("INVALID_CHALLENGE","服务器设备挑战格式异常。");using var ec=new ECDsaCng(key);return SignatureVerifier.Encode(ec.SignData(Encoding.UTF8.GetBytes(nonce),HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation));}
 public void Dispose()=>key.Dispose();
}
