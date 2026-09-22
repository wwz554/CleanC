using CleanC.Core;
namespace CleanC.Licensing;
public sealed record Lease
{
 public int Version {get;init;}
 public int ApiVersion {get;init;}
 public string LicenseId {get;init;}="";
 public string DeviceId {get;init;}="";
 public string Edition {get;init;}="";
 public string LicenseType {get;init;}="";
 public bool IsPermanent {get;init;}
 public bool CountdownRequired {get;init;}
 public string[] Features {get;init;}=[];
 public DateTimeOffset IssuedAt {get;init;}
 public DateTimeOffset ServerTime {get;init;}
 public DateTimeOffset ExpiresAt {get;init;}
 public DateTimeOffset? LicenseExpiresAt {get;init;}
 public int LeaseHours {get;init;}
 public string RenewalProtocol {get;init;}="";
 public string Nonce {get;init;}="";
 public bool Has(FeatureCapability capability)=>Features.Contains(capability switch {FeatureCapability.Scan or FeatureCapability.AdvancedAnalysis=>"scan",FeatureCapability.Cleanup=>"clean",FeatureCapability.SystemRepair=>"optimize",_=>""},StringComparer.Ordinal);
}
public sealed record SignedEnvelope(string SignedPayload,string Signature);
public sealed record SavedLicense(string LicenseKey,SignedEnvelope Envelope,DateTimeOffset FirstAcceptedUtc,LicenseState? Lock=null);
public sealed class LicenseException(string code,string message):Exception(message){public string Code {get;}=code;}
