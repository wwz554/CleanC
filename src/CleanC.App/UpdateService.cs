using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
namespace CleanC.App;
internal sealed record UpdateManifest(string Version,string Url,string Sha256,long Size,string Notes="");
internal static class UpdateService
{
 static readonly HttpClient http=new(){Timeout=TimeSpan.FromMinutes(20)};
 public static Version Current=>typeof(UpdateService).Assembly.GetName().Version??new Version(1,7,1);
 public static async Task<UpdateManifest?> CheckAsync(CancellationToken token=default)
 {
  using var request=new HttpRequestMessage(HttpMethod.Get,"https://raw.githubusercontent.com/wwz554/CleanC/main/updates/latest.json");request.Headers.UserAgent.ParseAdd("CleanC/"+Current);request.Headers.CacheControl=new(){NoCache=true};
  using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
  if(response.StatusCode==System.Net.HttpStatusCode.NotFound)return null;response.EnsureSuccessStatusCode();
  if(response.Content.Headers.ContentLength>32768)throw new IOException("更新信息过大。");
  using var stream=await response.Content.ReadAsStreamAsync(token);using var memory=new MemoryStream();var buf=new byte[4096];int n;
  while((n=await stream.ReadAsync(buf,token))>0){if(memory.Length+n>32768)throw new IOException("更新信息过大。");memory.Write(buf,0,n);}
  var manifest=JsonSerializer.Deserialize<UpdateManifest>(memory.ToArray(),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})??throw new IOException("更新信息无效。");
  if(!Version.TryParse(manifest.Version,out var version)||version<=Current)return null;
  if(!Uri.TryCreate(manifest.Url,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="github.com"||!uri.AbsolutePath.StartsWith("/wwz554/CleanC/releases/download/",StringComparison.Ordinal)||!uri.AbsolutePath.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)||!System.Text.RegularExpressions.Regex.IsMatch(manifest.Sha256??"","^[A-Fa-f0-9]{64}$")||manifest.Size<=0||manifest.Size>1024L*1024*1024)throw new IOException("更新来源或校验信息无效。");
  return manifest;
 }
 public static async Task<string> DownloadAsync(UpdateManifest update,IProgress<double> progress,CancellationToken token)
 {
  var directory=Path.Combine(CleanC.Core.AppPaths.MachineData,"Updates",Guid.NewGuid().ToString("N"));CleanC.Core.AppPaths.EnsurePrivate(directory);var path=Path.Combine(directory,"CleanC-Setup.exe");
  try{
   using var request=new HttpRequestMessage(HttpMethod.Get,update.Url);request.Headers.UserAgent.ParseAdd("CleanC/"+Current);
   using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);response.EnsureSuccessStatusCode();
   if(response.Content.Headers.ContentLength is {} length&&length!=update.Size)throw new IOException("更新文件大小不一致。");
   using var input=await response.Content.ReadAsStreamAsync(token);using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true)){
    var buffer=new byte[81920];long total=0;int count;while((count=await input.ReadAsync(buffer,token))>0){total+=count;if(total>update.Size)throw new IOException("更新文件大小异常。");await output.WriteAsync(buffer.AsMemory(0,count),token);progress.Report(total*100d/update.Size);}if(total!=update.Size)throw new IOException("更新下载不完整。");
   }
   using var file=File.OpenRead(path);var hash=Convert.ToHexString(await SHA256.HashDataAsync(file,token));if(!hash.Equals(update.Sha256,StringComparison.OrdinalIgnoreCase))throw new CryptographicException("更新文件校验失败，已停止更新。");return path;
  }catch{try{File.Delete(path);}catch{}throw;}
 }
 public static void Install(string installer)
 {
  // Inno Setup owns file replacement/rollback. Keep its real installation progress visible.
  var info=new ProcessStartInfo(installer){UseShellExecute=true,Verb="runas"};
  info.ArgumentList.Add("/SILENT");info.ArgumentList.Add("/SUPPRESSMSGBOXES");info.ArgumentList.Add("/NORESTART");info.ArgumentList.Add("/SP-");info.ArgumentList.Add("/UPDATE");info.ArgumentList.Add("/DIR="+AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
  if(Process.Start(info) is null)throw new IOException("无法启动更新程序。");
 }
}
