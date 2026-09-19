using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace RHelper.Compact;

internal sealed record UpdateRelease(string Tag,ReleaseVersion Version,string Page,string FileName,string Download,string Checksums,long Size);

// Manual requests only. No controller dependency and no GitHub credentials.
internal sealed class UpdateService : IDisposable
{
    internal const string Repository="https://github.com/e-khurmamatov/r-helper-compact";
    internal const string Api="https://api.github.com/repos/e-khurmamatov/r-helper-compact/releases";
    public static string CurrentVersion => (Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion??"0.0.0").Split('+')[0];
    readonly HttpClient client;
    public UpdateService(HttpMessageHandler? handler=null) {
        client=new(handler??new HttpClientHandler {AllowAutoRedirect=false}) {Timeout=TimeSpan.FromMinutes(10)};
        client.DefaultRequestHeaders.UserAgent.ParseAdd("R-Helper-Compact/"+CurrentVersion);
    }
    public void Dispose()=>client.Dispose();
    static bool AssetUrl(string value,string tag,string name) => value==Repository+"/releases/download/"+Uri.EscapeDataString(tag)+"/"+name;
    internal static IEnumerable<UpdateRelease> ParseReleases(string json,bool previews,bool installed) {
        using var doc=JsonDocument.Parse(json);
        foreach(var item in doc.RootElement.EnumerateArray()) {
            if(item.GetProperty("draft").GetBoolean())continue;
            string tag=item.GetProperty("tag_name").GetString()??"";
            var version=ReleaseVersion.Parse(tag);
            if(version is null || (!previews&&(item.GetProperty("prerelease").GetBoolean()||version.Preview.Length>0)))continue;
            string name="R-Helper-Compact-"+tag.TrimStart('v')+(installed?"-x64.msi":"-x64-portable.zip");
            string page=Repository+"/releases/tag/"+Uri.EscapeDataString(tag);
            var assets=item.GetProperty("assets").EnumerateArray().ToArray();
            var file=assets.Where(x=>x.GetProperty("name").GetString()==name).ToArray();
            var sums=assets.Where(x=>x.GetProperty("name").GetString()=="SHA256SUMS.txt").ToArray();
            // Incomplete or ambiguous releases are not installable.
            if(file.Length!=1||sums.Length!=1)continue;
            string url=file[0].GetProperty("browser_download_url").GetString()??"",sum=sums[0].GetProperty("browser_download_url").GetString()??"";
            long size=file[0].GetProperty("size").GetInt64();
            if(size<=0||size>1024L*1024*1024||!AssetUrl(url,tag,name)||!AssetUrl(sum,tag,"SHA256SUMS.txt"))continue;
            yield return new(tag,version,page,name,url,sum,size);
        }
    }
    public async Task<UpdateRelease?> Check(bool installed,CancellationToken token) {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(30));
        bool previews=ReleaseVersion.Parse(CurrentVersion)?.Preview.Length>0;
        var all=new List<UpdateRelease>();
        // Follow pages rather than trusting publication order or /latest (which excludes previews).
        for(int page=1;page<=20;page++) {
            string json=Encoding.UTF8.GetString(await ReadBounded(Api+"?per_page=100&page="+page,4*1024*1024,timeout.Token));
            all.AddRange(ParseReleases(json,previews,installed));
            using var parsed=JsonDocument.Parse(json);
            if(parsed.RootElement.GetArrayLength()<100)return all.OrderByDescending(x=>x.Version).FirstOrDefault();
        }
        throw new IOException(L.T("Too many releases. Open GitHub to check manually."));
    }
    async Task<HttpResponseMessage> Get(string url,CancellationToken token) {
        for(int i=0;i<6;i++) {
            using var request=new HttpRequestMessage(HttpMethod.Get,url);
            var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token);
            if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308) {
                var next=response.Headers.Location;response.Dispose();
                if(next is null)throw new IOException(L.T("Invalid download redirect."));
                var uri=next.IsAbsoluteUri?next:new Uri(new Uri(url),next);
                if(uri.Scheme!="https"||!uri.IsDefaultPort||uri.UserInfo.Length!=0||uri.Host is not ("github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com"))throw new IOException(L.T("Untrusted download address."));
                url=uri.AbsoluteUri;continue;
            }
            if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) {response.Dispose();throw new IOException(L.T("GitHub request limit reached. Try again later."));}
            if(!response.IsSuccessStatusCode) {int code=(int)response.StatusCode;response.Dispose();throw new IOException(L.F("GitHub request failed ({0}).",code));}
            return response;
        }
        throw new IOException(L.T("Too many download redirects."));
    }
    async Task<byte[]> ReadBounded(string url,int limit,CancellationToken token) {
        using var response=await Get(url,token);using var input=await response.Content.ReadAsStreamAsync(token);using var output=new MemoryStream();
        var buffer=new byte[16384];int n;
        while((n=await input.ReadAsync(buffer,token))>0) {if(output.Length+n>limit)throw new IOException(L.T("GitHub response is too large."));await output.WriteAsync(buffer.AsMemory(0,n),token);}
        return output.ToArray();
    }
    internal static string Checksum(string text,string name) {
        var matches=text.Split('\n').Select(x=>Regex.Match(x.Trim(),@"^([a-fA-F0-9]{64})\s+\*?(.+)$")).Where(x=>x.Success&&x.Groups[2].Value==name).ToArray();
        if(matches.Length!=1)throw new IOException(L.T("Release checksum is missing or ambiguous."));
        return matches[0].Groups[1].Value.ToLowerInvariant();
    }
    public async Task<(string Path,string Hash)> Download(UpdateRelease release,string directory,IProgress<double> progress,CancellationToken token) {
        string hash=Checksum(Encoding.UTF8.GetString(await ReadBounded(release.Checksums,65536,token)),release.FileName);
        Directory.CreateDirectory(directory);string path=System.IO.Path.Combine(directory,release.FileName),partial=path+".partial";
        try {
            using(var response=await Get(release.Download,token)) {
                using var input=await response.Content.ReadAsStreamAsync(token);
                using var output=new FileStream(partial,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,true);
                using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer=new byte[65536];long total=0;int n;
                while((n=await input.ReadAsync(buffer,token))>0) {
                    total+=n;if(total>release.Size)throw new IOException(L.T("Downloaded file size does not match the release."));
                    sha.AppendData(buffer,0,n);await output.WriteAsync(buffer.AsMemory(0,n),token);progress.Report(100.0*total/release.Size);
                }
                if(total!=release.Size||!string.Equals(Convert.ToHexString(sha.GetHashAndReset()),hash,StringComparison.OrdinalIgnoreCase))throw new IOException(L.T("Downloaded file failed checksum verification."));
            }
            File.Move(partial,path);return(path,hash);
        } catch {if(File.Exists(partial))File.Delete(partial);throw;}
    }
}
