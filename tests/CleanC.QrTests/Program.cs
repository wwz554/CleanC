using System.Security.Cryptography;
using CleanC.App;
using CleanC.Licensing;
using ZXing;

using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var session = new OfflineActivationSession("DEVICE-"+new string('A',32),key.ExportSubjectPublicKeyInfoPem(),DateTimeOffset.UtcNow);
var renderer = new StandardParticleQrRenderer(session.Url);
if(renderer.ModuleCount>129)throw new Exception("The activation QR exceeds the tested visual density.");
var reader = new BarcodeReaderGeneric {AutoRotate=true,Options=new ZXing.Common.DecodingOptions {
    TryHarder=true,TryInverted=true,PossibleFormats=[BarcodeFormat.QR_CODE]}};
var original = new byte[720*720*4];
foreach(var dark in new[]{false,true})
foreach(var second in new[]{0d,1.2,3.4})
foreach(var size in new[]{350,450,550})
{
    renderer.Render(original,720,second,dark);
    var pixels=new byte[size*size*4];
    for(var y=0;y<size;y++)for(var x=0;x<size;x++)
    {
        var from=(((int)((y+.5)*720/size)*720)+((int)((x+.5)*720/size)))*4;
        var to=(y*size+x)*4;var alpha=original[from+3]/255d;
        pixels[to]=(byte)Math.Clamp(original[from]+(dark?28:251)*(1-alpha),0,255);
        pixels[to+1]=(byte)Math.Clamp(original[from+1]+(dark?24:247)*(1-alpha),0,255);
        pixels[to+2]=(byte)Math.Clamp(original[from+2]+(dark?19:244)*(1-alpha),0,255);
        pixels[to+3]=255;
    }
    var decoded=reader.Decode(pixels,size,size,ZXing.RGBLuminanceSource.BitmapFormat.BGRA32);
    if(decoded?.Text!=session.Url)throw new Exception($"QR failed to return the exact activation URL: dark={dark}, second={second}, size={size}");
}
Console.WriteLine($"PASS 18 dynamic QR renders decoded the exact original activation URL ({renderer.ModuleCount} modules).");
// Optional real WinUI captures. Each BGRA filename ends with -WIDTHxHEIGHT.bgra.
if(args.Length==1){
 var expected=File.ReadAllText(Path.Combine(args[0],"fixture-url.txt"));
 foreach(var file in Directory.GetFiles(args[0],"*.bgra")){
  var match=System.Text.RegularExpressions.Regex.Match(file,@"-(\d+)x(\d+)\.bgra$");
  if(!match.Success)throw new Exception("Capture dimensions missing: "+file);
  var width=int.Parse(match.Groups[1].Value);var height=int.Parse(match.Groups[2].Value);
  var actual=reader.Decode(File.ReadAllBytes(file),width,height,RGBLuminanceSource.BitmapFormat.BGRA32);
  if(actual?.Text!=expected)throw new Exception("Actual WinUI capture decode failed: "+Path.GetFileName(file));
  Console.WriteLine("PASS actual WinUI "+Path.GetFileName(file));
 }
}
