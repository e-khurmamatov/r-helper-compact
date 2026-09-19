using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RHelper.Compact;

// Runs only in explicit smoke modes. Never contacts GitHub, launches MSI or the controller.
internal static class UpdateSmoke
{
    sealed class FakeHttp(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token) {token.ThrowIfCancellationRequested();return Task.FromResult(respond(request));}
    }
    static void Require(bool value,string message) {if(!value)throw new InvalidOperationException(message);}
    static object Release(string tag,bool preview=false,bool draft=false,string? address=null) => new {
        tag_name=tag,prerelease=preview,draft,
        assets=new[]{new {name="R-Helper-Compact-"+tag.TrimStart('v')+"-x64.msi",size=3,browser_download_url=address??UpdateService.Repository+"/releases/download/"+tag+"/R-Helper-Compact-"+tag.TrimStart('v')+"-x64.msi"},
            new {name="SHA256SUMS.txt",size=100,browser_download_url=UpdateService.Repository+"/releases/download/"+tag+"/SHA256SUMS.txt"}}
    };
    public static async Task Run() {
        Require(ReleaseVersion.Parse("0.2.10")!.CompareTo(ReleaseVersion.Parse("0.2.9"))>0,"Numeric version order");
        Require(ReleaseVersion.Parse("v1.0.0")!.CompareTo(ReleaseVersion.Parse("1.0.0-preview"))>0,"Stable follows preview");
        Require(ReleaseVersion.Parse("1.0.0-preview.10")!.CompareTo(ReleaseVersion.Parse("1.0.0-preview.2"))>0,"Numeric prerelease order");
        Require(ReleaseVersion.Parse("01.0.0")==null&&ReleaseVersion.Parse("1.0.0-preview.01")==null,"Malformed versions");
        Require(ReleaseVersion.Parse("1.0.0+build")!.CompareTo(ReleaseVersion.Parse("1.0.0"))==0,"Build metadata");
        Require(ReleaseVersion.Parse("1.0.0+..") is null,"Empty build identifiers");
        Require(ReleaseVersion.Parse("1.0.0--1")!.CompareTo(ReleaseVersion.Parse("1.0.0-1"))>0,"Hyphenated identifier is not numeric");
        string json=JsonSerializer.Serialize(new[]{Release("v0.2.9"),Release("v0.2.10"),Release("v0.2.11-preview",true),Release("v9.0.0",false,true),Release("v8.0.0",address:"https://example.com/bad.msi")});
        Require(UpdateService.ParseReleases(json,false,true).MaxBy(x=>x.Version)!.Tag=="v0.2.10","Stable channel");
        var release=UpdateService.ParseReleases(json,true,true).MaxBy(x=>x.Version)!;
        Require(release.Tag=="v0.2.11-preview","Preview channel");
        Require(!UpdateService.ParseReleases(json,true,false).Any(),"Portable must not install MSI");
        byte[] bytes=Encoding.UTF8.GetBytes("abc");string hash=Convert.ToHexString(SHA256.HashData(bytes));
        string sums=hash+"  "+release.FileName+"\n";
        Require(UpdateService.Checksum(sums,release.FileName)==hash.ToLowerInvariant(),"Checksum lookup");
        try {UpdateService.Checksum(sums+sums,release.FileName);throw new Exception("Duplicate checksum accepted");}catch(IOException) { }
        string folder=Path.Combine(Path.GetTempPath(),"rhelper-update-test-"+Guid.NewGuid().ToString("N"));
        bool corrupt=false,untrusted=false;
        using var service=new UpdateService(new FakeHttp(request=> {
            if(request.RequestUri!.AbsolutePath.EndsWith("SHA256SUMS.txt"))return new(HttpStatusCode.OK) {Content=new StringContent(sums)};
            if(untrusted) {var r=new HttpResponseMessage(HttpStatusCode.Redirect);r.Headers.Location=new Uri("https://example.com/installer");return r;}
            return new(HttpStatusCode.OK) {Content=new ByteArrayContent(corrupt?Encoding.UTF8.GetBytes("xyz"):bytes)};
        }));
        try {
            var saved=await service.Download(release,Path.Combine(folder,"good"),new Progress<double>(),CancellationToken.None);
            Require(File.ReadAllBytes(saved.Path).SequenceEqual(bytes),"Verified download");
            corrupt=true;
            try {await service.Download(release,Path.Combine(folder,"bad"),new Progress<double>(),CancellationToken.None);throw new Exception("Corrupt download accepted");}catch(IOException) { }
            Require(!Directory.EnumerateFiles(Path.Combine(folder,"bad")).Any(),"Partial corrupt download retained");
            untrusted=true;
            try {await service.Download(release,Path.Combine(folder,"redirect"),new Progress<double>(),CancellationToken.None);throw new Exception("Untrusted redirect accepted");}catch(IOException) { }
            using var cancelled=new CancellationTokenSource();cancelled.Cancel();
            try {await service.Download(release,Path.Combine(folder,"cancelled"),new Progress<double>(),cancelled.Token);throw new Exception("Cancellation ignored");}catch(OperationCanceledException) { }
        }finally {if(Directory.Exists(folder))Directory.Delete(folder,true);}
        using var rateLimited=new UpdateService(new FakeHttp(_=>new(HttpStatusCode.Forbidden)));
        try {await rateLimited.Check(true,CancellationToken.None);throw new Exception("API error treated as up to date");}catch(IOException) { }
        using var empty=new UpdateService(new FakeHttp(_=>new(HttpStatusCode.OK) {Content=new StringContent("[]")}));
        Require(await empty.Check(true,CancellationToken.None)==null,"No releases");
        Require(UpdateInstaller.SuccessfulExit(0)&&UpdateInstaller.SuccessfulExit(3010)&&!UpdateInstaller.SuccessfulExit(1603),"MSI exit codes");
        var job=new UpdateInstaller.Job("unused.msi","unused","unused.exe",new(1,1),new(2,2));
        string order="";
        int result=UpdateInstaller.InstallAfterExit(job,p=>{order+=p.Id;return true;},()=>{order+="I";return 0;},()=>order+="R");
        Require(result==0&&order=="12IR","Wait for app and controller before installing and reopening");
        order="";
        try {UpdateInstaller.InstallAfterExit(job,p=>p.Id==1,()=>{order+="I";return 0;},()=>order+="R");throw new Exception("Installed while controller still running");}catch(IOException) { }
        Require(order=="","Busy controller must prevent install and duplicate controller restart");
        try {UpdateInstaller.InstallAfterExit(job,_=>true,()=>throw new System.ComponentModel.Win32Exception(1223),()=>order+="R");throw new Exception("UAC cancellation ignored");}catch(System.ComponentModel.Win32Exception) { }
        Require(order=="R","UAC cancellation must reopen previous app");
        order="";
        result=UpdateInstaller.InstallAfterExit(job,_=>true,()=>1603,()=>order+="R");
        Require(result==1603&&order=="R","Failed installer must preserve its error and reopen the app");
    }
}
