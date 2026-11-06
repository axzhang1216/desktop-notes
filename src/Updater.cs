using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DesktopNotes {
    public sealed class UpdateInfo {
        public string Tag { get; set; }
        public string Version { get; set; }
        public string DownloadUrl { get; set; }
    }

    internal static class NetworkSupport {
        public static void EnableModernTls() {
            const SecurityProtocolType tls12=(SecurityProtocolType)3072;
            const SecurityProtocolType tls13=(SecurityProtocolType)12288;
            ServicePointManager.SecurityProtocol |= tls12 | tls13;
        }
        public static bool UsesSystemProxy(string url) {
            try {
                IWebProxy proxy=WebRequest.DefaultWebProxy;
                if(proxy==null) return false;
                Uri uri=new Uri(url);
                return !proxy.IsBypassed(uri) && proxy.GetProxy(uri)!=null;
            } catch { return false; }
        }
        public static string ErrorText(Exception error) {
            var text=new StringBuilder(error.Message??"");
            for(Exception inner=error.InnerException;inner!=null;inner=inner.InnerException) text.Append("\n").Append(inner.Message);
            return text.ToString();
        }
    }

    public static class Updater {
        const string LatestApi="https://api.github.com/repos/axzhang1216/desktop-notes/releases/latest";
        static readonly JavaScriptSerializer serializer=new JavaScriptSerializer { MaxJsonLength=4*1024*1024, RecursionLimit=40 };

        public static string CurrentVersion {
            get {
                var assembly=typeof(Updater).Assembly;
                var attribute=Attribute.GetCustomAttribute(assembly,typeof(AssemblyInformationalVersionAttribute)) as AssemblyInformationalVersionAttribute;
                if(attribute!=null && !string.IsNullOrWhiteSpace(attribute.InformationalVersion)) return attribute.InformationalVersion.Trim();
                return FileVersionInfo.GetVersionInfo(assembly.Location).FileVersion;
            }
        }

        public static bool IsNewer(string tag,string current) {
            Version latest=ParseVersion(tag), now=ParseVersion(current);
            return latest!=null && now!=null && latest>now;
        }

        static Version ParseVersion(string value) {
            if(string.IsNullOrWhiteSpace(value)) return null;
            Version version;
            return Version.TryParse(value.Trim().TrimStart('v','V'),out version)?version:null;
        }

        public static UpdateInfo ParseRelease(string json) {
            var root=serializer.DeserializeObject(json) as Dictionary<string,object>;
            if(root==null) throw new InvalidDataException("无法读取 GitHub 返回的数据。");
            object tag; root.TryGetValue("tag_name",out tag);
            string tagName=Convert.ToString(tag);
            string download=null;
            object rawAssets; root.TryGetValue("assets",out rawAssets);
            var assets=rawAssets as object[];
            if(assets!=null) {
                foreach(object item in assets) {
                    var asset=item as Dictionary<string,object>;
                    if(asset==null) continue;
                    object name; asset.TryGetValue("name",out name);
                    if(!Convert.ToString(name).EndsWith("-Windows.zip",StringComparison.OrdinalIgnoreCase)) continue;
                    object url; asset.TryGetValue("browser_download_url",out url);
                    download=Convert.ToString(url);
                    if(!string.IsNullOrWhiteSpace(download)) break;
                }
            }
            if(string.IsNullOrWhiteSpace(tagName)) throw new InvalidDataException("GitHub 返回的版本号为空。");
            if(string.IsNullOrWhiteSpace(download)) throw new InvalidDataException("最新版本没有 Windows 压缩包。");
            return new UpdateInfo { Tag=tagName, Version=tagName.TrimStart('v','V'), DownloadUrl=download };
        }

        public static async Task<UpdateInfo> FindAsync() {
            UpdateInfo info=ParseRelease(Encoding.UTF8.GetString(await GetBytesAsync(LatestApi)));
            return IsNewer(info.Tag,CurrentVersion)?info:null;
        }

        public static async Task<string> DownloadAsync(UpdateInfo info) {
            string folder=Path.Combine(Path.GetTempPath(),"DesktopNotes-Update-"+DateTime.Now.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(folder);
            string zip=Path.Combine(folder,"update.zip");
            File.WriteAllBytes(zip,await GetBytesAsync(info.DownloadUrl));
            return zip;
        }

        public static void Install(string zipPath) {
            string target=Assembly.GetExecutingAssembly().Location;
            string probe=Path.Combine(Path.GetDirectoryName(target),".update-probe");
            File.WriteAllText(probe,""); File.Delete(probe);
            string folder=Path.GetDirectoryName(zipPath);
            string incoming=Path.Combine(folder,"DesktopNotes.exe");
            using(var archive=new ZipArchive(new FileStream(zipPath,FileMode.Open,FileAccess.Read),ZipArchiveMode.Read)) {
                var entry=archive.Entries.FirstOrDefault(e=>string.Equals(e.Name,"DesktopNotes.exe",StringComparison.OrdinalIgnoreCase));
                if(entry==null) throw new InvalidDataException("更新包里没有 DesktopNotes.exe。");
                using(var input=entry.Open()) using(var output=File.Create(incoming)) input.CopyTo(output);
            }
            Version current=FileVersion(target), next=FileVersion(incoming);
            if(current!=null && next!=null && next<=current) throw new InvalidOperationException("更新包版本不比当前版本新。");
            string script=Path.Combine(folder,"apply-update.ps1");
            string content=
                "$ErrorActionPreference='SilentlyContinue'\r\n"+
                "Wait-Process -Id "+Process.GetCurrentProcess().Id+" -Timeout 60\r\n"+
                "Start-Sleep -Milliseconds 500\r\n"+
                "for($i=0;$i -lt 20;$i++){ try { Copy-Item -LiteralPath "+Quote(incoming)+" -Destination "+Quote(target)+" -Force -ErrorAction Stop; break } catch { Start-Sleep -Milliseconds 500 } }\r\n"+
                "Start-Process -FilePath "+Quote(target)+"\r\n"+
                "Remove-Item -LiteralPath "+Quote(script)+" -Force\r\n";
            File.WriteAllText(script,content,new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo {
                FileName="powershell.exe",
                Arguments="-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \""+script+"\"",
                UseShellExecute=false,
                CreateNoWindow=true
            });
        }

        static string Quote(string value) { return "'"+value.Replace("'","''")+"'"; }

        static Version FileVersion(string path) {
            try {
                Version version;
                return Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion,out version)?version:null;
            } catch { return null; }
        }

        static async Task<byte[]> GetBytesAsync(string url) {
            NetworkSupport.EnableModernTls();
            try {
                return await GetBytesAsync(url,true);
            } catch(HttpRequestException) {
                if(!NetworkSupport.UsesSystemProxy(url)) throw;
            }
            return await GetBytesAsync(url,false);
        }

        static async Task<byte[]> GetBytesAsync(string url,bool useProxy) {
            using(var handler=new HttpClientHandler { UseProxy=useProxy })
            using(var client=new HttpClient(handler)) {
                client.Timeout=TimeSpan.FromSeconds(30);
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent","DesktopNotes/"+CurrentVersion);
                using(var response=await client.GetAsync(url)) {
                    if(!response.IsSuccessStatusCode) throw new InvalidOperationException("GitHub 返回 "+(int)response.StatusCode+"。");
                    return await response.Content.ReadAsByteArrayAsync();
                }
            }
        }
    }
}