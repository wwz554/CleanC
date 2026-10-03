using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CleanC.Core;
using CleanC.Licensing;

sealed partial class Tests
{
 Lease OfflineLease(bool permanent=false) {
  var l=Lease(permanent);return l with{RenewalProtocol="offline-v3",LeaseHours=0,ExpiresAt=l.LicenseExpiresAt??DateTimeOffset.MaxValue};
 }
 SignedEnvelope SignObject(object value) {
  var raw=JsonSerializer.SerializeToUtf8Bytes(value,SignatureVerifier.Json);
  return new(SignatureVerifier.Encode(raw),SignatureVerifier.Encode(key.SignData(raw,HashAlgorithmName.SHA256,DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
 }
 SignedEnvelope OfflineEnvelope(Lease l,string requestHash="",string codeHash="")=>SignObject(new OfflineCredential(1,"CleanC","offline-entitlement-v1",l,requestHash,codeHash,requestHash.Length==0?"a-valid-nonce-for-test-only":""));
 MemoryStore LegacyOfflineStore() {
  var st=new MemoryStore();var l=OfflineLease() with{RenewalProtocol="offline-v2"};
  st.Write("offline-license.dat",new OfflineActivationRecord(l));
  var t=new TrustedTimeService(clock);t.Accept(l);st.Write("trusted-time.dat",t.Snapshot(l));return st;
 }
 // Ephemeral test fixtures only; never use a customer's license, identity or key.
 static string FixturePin(OfflineActivationSession session,Lease l) {
  uint meta=l.IsPermanent?0:((l.LicenseType=="duration"?1u:2u)<<30)|(uint)((l.LicenseExpiresAt!.Value-new DateTimeOffset(2020,1,1,0,0,0,TimeSpan.Zero)).TotalMinutes);
  var secret=(byte[])typeof(OfflineActivationSession).GetField("secret",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(session)!;
  var packed=new byte[10];System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(packed,meta);
  HMACSHA256.HashData(secret,Encoding.UTF8.GetBytes("CleanC/offline/v2\n"+session.Request+"\n"+meta)).AsSpan(0,6).CopyTo(packed.AsSpan(4));
  var code="";int value=0,bits=0;foreach(var b in packed){value=(value<<8)|b;bits+=8;while(bits>=5){bits-=5;code+="0123456789ABCDEFGHJKMNPQRSTVWXYZ"[(value>>bits)&31];}}return code;
 }
 string CredentialFile(OfflineActivationSession session,Lease l,string pin)=>JsonSerializer.Serialize(OfflineEnvelope(l,OfflineCredentialFile.Hash(session.Request),OfflineCredentialFile.Hash(pin)),SignatureVerifier.Json);
 static async Task Rejected(Func<Task> action){try{await action();}catch(LicenseException){return;}throw new Exception("Expected licensing rejection");}
 async Task RunOfflineMigrationTests()
 {
  await Test("In-app upgrade verifies legacy offline exactly once and retains full expiry",async()=>{
   var st=LegacyOfflineStore();var api=new FakeApi(this){Permanent=false};var m=Manager(api,st,channel:UpgradeChannel.InApp);
   await m.InitializeAsync();Check(m.Context.State==LicenseState.Active);
   Check(api.Paths.SequenceEqual(new[]{"offline/challenge","offline/refresh"}));
   Check(m.Context.Lease!.LicenseExpiresAt==Lease().LicenseExpiresAt&&m.Context.Lease.ExpiresAt==Lease().LicenseExpiresAt);
   Check(st.Read<OfflineActivationRecord>("offline-license.dat")!.Envelope is not null&&st.Read<SavedLicense>("license.dat") is null);
   var later=new FakeApi(this){Offline=true};var again=Manager(later,st,channel:UpgradeChannel.Manual);await again.InitializeAsync();
   Check(again.Context.State==LicenseState.Active&&later.Paths.Count==0);await again.TickAsync();Check(later.Paths.Count==0);
  });
  await Test("In-app migration failure preserves original record and retries without input",async()=>{
   var st=LegacyOfflineStore();var before=st.Read<OfflineActivationRecord>("offline-license.dat")!;
   var c=new FakeClock();var api=new FakeApi(this){Offline=true,Permanent=false};var m=Manager(api,st,c,UpgradeChannel.InApp);await m.InitializeAsync();
   Check(m.Context.State==LicenseState.UpgradeRequired&&m.Context.Countdown=="授权升级待验证");
   Check(st.Read<OfflineActivationRecord>("offline-license.dat")!.Lease.Nonce==before.Lease.Nonce);
   Throws(()=>m.Context.Demand(FeatureCapability.Cleanup));var calls=api.Paths.Count;await m.TickAsync();Check(api.Paths.Count==calls);
   api.Offline=false;c.Elapsed+=TimeSpan.FromMinutes(2);await m.TickAsync();Check(m.Context.State==LicenseState.Active);
  });
  await Test("Manual upgrade requires original key reissuance and never auto refreshes",async()=>{
   var st=LegacyOfflineStore();var api=new FakeApi(this);var m=Manager(api,st);await m.InitializeAsync();await m.TickAsync();
   Check(m.Context.State==LicenseState.UpgradeRequired&&!m.CanValidateExisting&&api.Paths.Count==0);
   await Rejected(()=>m.RefreshAsync());Check(api.Paths.Count==0);Throws(()=>m.Context.Demand(FeatureCapability.Scan));
  });
  await Test("Disabled migration stays locked and cannot expose a countdown",async()=>{
   var m=Manager(new FakeApi(this){Error="LICENSE_DISABLED"},LegacyOfflineStore(),channel:UpgradeChannel.InApp);await m.InitializeAsync();
   Check(m.Context.State==LicenseState.Suspended&&m.Context.Countdown=="授权已停用");
  });
  await Test("Automatic migration rejects replayed proof from another challenge",async()=>{
   var st=LegacyOfflineStore();var m=Manager(new FakeApi(this){WrongOfflineNonce=true},st,channel:UpgradeChannel.InApp);
   await m.InitializeAsync();Check(m.Context.State==LicenseState.InvalidSignature);
   Check(st.Read<OfflineActivationRecord>("offline-license.dat")!.Envelope is null);Throws(()=>m.Context.Demand(FeatureCapability.Cleanup));
  });
  await Test("Upgrade origin is explicit versioned marker and fails closed",()=>{
   var dir=Path.Combine(root,"origin");Directory.CreateDirectory(dir);
   Check(UpgradeOrigin.Read(dir,"1.7.2")==UpgradeChannel.Manual);
   foreach(var content in new[]{"{}", "[]", "{", "{\"version\":\"1.7.1\",\"channel\":\"in-app\"}", "{\"version\":\"1.7.2\",\"channel\":\"manual\"}"}){
    File.WriteAllText(Path.Combine(dir,"upgrade-origin.json"),content);Check(UpgradeOrigin.Read(dir,"1.7.2")==UpgradeChannel.Manual);
   }
   File.WriteAllText(Path.Combine(dir,"upgrade-origin.json"),"{\"version\":\"1.7.2\",\"channel\":\"in-app\"}");
   Check(UpgradeOrigin.Read(dir,"1.7.2")==UpgradeChannel.InApp);
  });
  await Test("16-character code alone cannot grant offline capability",async()=>{
   var m=Manager(new FakeApi(this){Offline=true},new MemoryStore());var s=m.BeginOfflineActivation();var code=FixturePin(s,OfflineLease(true));
   await Rejected(()=>m.CompleteOfflineActivationAsync(code));Throws(()=>m.Context.Demand(FeatureCapability.Cleanup));
  });
  foreach(var permanent in new[]{false,true})
  await Test("Signed file and unchanged 16-character PIN activate without network "+permanent,async()=>{
   var api=new FakeApi(this){Offline=true};var st=LegacyOfflineStore();var m=Manager(api,st);await m.InitializeAsync();
   var s=m.BeginOfflineActivation();var l=OfflineLease(permanent);if(!permanent)l=l with{LicenseExpiresAt=l.LicenseExpiresAt!.Value.AddSeconds(37),ExpiresAt=l.ExpiresAt.AddSeconds(37)};
   var code=FixturePin(s,l);var file=CredentialFile(s,l,code);Check(code.Length==16);
   await m.CompleteOfflineActivationAsync(code,file);Check(m.Context.State==LicenseState.Active&&api.Paths.Count==0);
   Check(m.Context.Lease!.ExpiresAt==l.ExpiresAt&&m.Context.Lease.LicenseExpiresAt==l.LicenseExpiresAt);
   var restarted=Manager(api,st);await restarted.InitializeAsync();Check(restarted.Context.State==LicenseState.Active&&api.Paths.Count==0);
  });
  await Test("Imported signed file from another scan is rejected without consuming session",async()=>{
   var m=Manager(new FakeApi(this),new MemoryStore());var old=m.BeginOfflineActivation();var l=OfflineLease();var code=FixturePin(old,l);var file=CredentialFile(old,l,code);
   var current=m.BeginOfflineActivation();await Rejected(()=>m.CompleteOfflineActivationAsync(code,file));Check(current.IsValid);
  });
  await Test("Signed credential for another device never rebinds local identity",async()=>{
   var m=Manager(new FakeApi(this),new MemoryStore());var s=m.BeginOfflineActivation();var l=OfflineLease() with{DeviceId="OTHER"};
   var code=FixturePin(s,l);await Rejected(()=>m.CompleteOfflineActivationAsync(code,CredentialFile(s,l,code)));
   Check(m.DeviceId=="DEVICE-TEST"&&m.Context.State!=LicenseState.Active);
  });
  await Test("Signed credential rejects changed payload, signature and wrong purpose",()=>{
   var good=OfflineEnvelope(OfflineLease());Throws(()=>verifier.VerifyOffline(good with{SignedPayload=SignatureVerifier.Encode(Encoding.UTF8.GetBytes("{}"))},"DEVICE-TEST"));
   Throws(()=>verifier.VerifyOffline(good with{Signature=SignatureVerifier.Encode(new byte[64])},"DEVICE-TEST"));
   Throws(()=>verifier.VerifyOffline(SignObject(new OfflineCredential(1,"CleanC","online",OfflineLease(),"","")),"DEVICE-TEST"));
   Throws(()=>verifier.VerifyOffline(Envelope(Lease()),"DEVICE-TEST"));
   Throws(()=>verifier.VerifyOffline(new(null!,null!),"DEVICE-TEST"));
  });
  await Test("Forged cached lease cannot extend a persisted signed entitlement",async()=>{
   var st=new MemoryStore();var l=OfflineLease();var t=new TrustedTimeService(clock);t.Accept(l);
   st.Write("trusted-time.dat",t.Snapshot(l));st.Write("offline-license.dat",new OfflineActivationRecord(OfflineLease(true),Envelope:OfflineEnvelope(l)));
   var m=Manager(new FakeApi(this){Offline=true},st);await m.InitializeAsync();
   Check(!m.Context.Lease!.IsPermanent&&m.Context.Lease.LicenseExpiresAt==l.LicenseExpiresAt);
  });
  await Test("Signed offline file parser rejects oversized, duplicate or untyped fields",()=>{
   foreach(var text in new[]{"null","[]","{}",new string('x',17000),"{\"signedPayload\":\"x\",\"signature\":null}","{\"signedPayload\":\"x\",\"signature\":\"y\",\"signature\":\"z\"}"})
    Throws(()=>OfflineCredentialFile.Parse(text));
  });
  await Test("Unsigned v3 marker cannot bypass migration gate",async()=>{
   var st=LegacyOfflineStore();var old=st.Read<OfflineActivationRecord>("offline-license.dat")!;
   st.Write("offline-license.dat",old with{Lease=OfflineLease(true)});
   var m=Manager(new FakeApi(this),st);await m.InitializeAsync();Check(m.Context.State==LicenseState.UpgradeRequired);
  });
  await Test("Signed entitlement validates all full-term constraints",()=>{
   var l=OfflineLease();
   foreach(var bad in new[]{l with{ExpiresAt=l.ExpiresAt.AddDays(1)},l with{LicenseType="unknown"},l with{LeaseHours=72},l with{RenewalProtocol="offline-v2"},l with{Features=null!},l with{ServerTime=l.ServerTime.AddMinutes(1)}})
    Throws(()=>verifier.VerifyOffline(OfflineEnvelope(bad),"DEVICE-TEST"));
  });
  await Test("Downloaded signature files remain protected inside cleanup roots",()=>{
   Check(new SafetyPolicy([new("test-cache",root,"cache",TimeSpan.Zero)]).Classify(SnapshotAge(Path.Combine(root,"CleanC-offline.cleanc-license"),365),DateTime.UtcNow).Safety==SafetyLevel.Protected);
  });
  await Test("Wrong 16-character PIN exhausts attempts without activating",async()=>{
   var m=Manager(new FakeApi(this){Offline=true},new MemoryStore());var s=m.BeginOfflineActivation();var l=OfflineLease();
   var file=CredentialFile(s,l,FixturePin(s,l));
   for(int i=0;i<5;i++)await Rejected(()=>m.CompleteOfflineActivationAsync("0000000000000000",file));
   Check(!s.IsValid&&m.Context.State!=LicenseState.Active);
  });
 }
}
