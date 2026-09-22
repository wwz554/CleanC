using System.Text.Json;
using System.Text.RegularExpressions;
using CleanC.Core;
namespace CleanC.Logging;
public sealed class AuditLog
{
 private readonly object sync=new();
 public string DirectoryPath {get;}
 public AuditLog(string? path=null)
 {
  var preferred=path;
  if(string.IsNullOrWhiteSpace(preferred))
  {
   try{preferred=AppPaths.Logs;Directory.CreateDirectory(preferred);}
   catch{preferred=Path.Combine(AppPaths.UserData,"Logs");Directory.CreateDirectory(preferred);}
  }
  DirectoryPath=preferred!;
 }
 public void Write(string module,string operation,string result,string? target=null,string? detail=null)
 {
  try
  {
   lock(sync){
    Directory.CreateDirectory(DirectoryPath);
    var line=JsonSerializer.Serialize(new {time=DateTimeOffset.UtcNow,module,operation,target,result,detail=Redact(detail)});
    File.AppendAllText(Path.Combine(DirectoryPath,$"{DateTime.UtcNow:yyyy-MM-dd}-{module.ToLowerInvariant()}.log"),line+Environment.NewLine);
   }
  }
  catch
  {
   // Logging must never become the reason the application or scanner terminates.
  }
 }
 static string? Redact(string? text)=>text is null?null:Regex.Replace(text,@"CLC-[A-Z0-9-]+","CLC-****",RegexOptions.IgnoreCase);
 public string ReadRecent(int count=150)
 {
  try{return string.Join(Environment.NewLine,Directory.EnumerateFiles(DirectoryPath,"*.log").OrderByDescending(File.GetLastWriteTimeUtc).Take(3).SelectMany(File.ReadLines).TakeLast(count));}
  catch{return "日志暂时不可读取。";}
 }
}
