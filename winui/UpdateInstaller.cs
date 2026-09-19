using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RHelper.Compact;

// A staged copy runs this mode before creating any WinUI window or controller.
// It remains unelevated; only Windows Installer requests elevation.
internal static class UpdateInstaller
{
    internal sealed record ProcessIdentity(int Id,long Started);
    internal sealed record Job(string Package,string Hash,string Application,ProcessIdentity Parent,ProcessIdentity? Controller);
    public static string CacheRoot=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"r-helper-compact","updates");
    public static ProcessIdentity Identity(Process process)=>new(process.Id,process.StartTime.ToUniversalTime().Ticks);
    public static bool IsInstalled(string executable) {
        var product=new StringBuilder(39);
        for(uint i=0;i<100&&MsiEnumRelatedProducts("{C253AC71-2DEE-449E-BF85-6783B5C4D74F}",0,i,product)==0;i++) {
            uint length=32768;var path=new StringBuilder((int)length);
            if(MsiGetComponentPath(product.ToString(),"{8E3D560D-C35A-4F3E-BF05-205E551D46D3}",path,ref length)==3&&string.Equals(Path.GetFullPath(path.ToString()),Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase))return true;
        }
        return false;
    }
    public static async Task Launch(string package,string hash,ProcessIdentity? controller,CancellationToken token) {
        string directory=Path.GetDirectoryName(package)!,runner=Path.Combine(directory,"runner");
        // The self-contained .NET runtime is copied too, so no installed SDK/runtime is required.
        await Task.Run(()=> {
            foreach(var file in Directory.EnumerateFiles(AppContext.BaseDirectory,"*",SearchOption.AllDirectories)) {
                token.ThrowIfCancellationRequested();
                string target=Path.Combine(runner,Path.GetRelativePath(AppContext.BaseDirectory,file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(file,target,false);
            }
        },token);
        string jobPath=Path.Combine(directory,"update.json");
        File.WriteAllText(jobPath,JsonSerializer.Serialize(new Job(package,hash,Environment.ProcessPath!,Identity(Process.GetCurrentProcess()),controller)));
        var start=new ProcessStartInfo(Path.Combine(runner,"rhelper-compact.exe")) {UseShellExecute=false,CreateNoWindow=true};
        start.ArgumentList.Add("--apply-update");start.ArgumentList.Add(jobPath);
        using var process=Process.Start(start)??throw new IOException(L.T("Could not start the updater."));
        try {
            for(int i=0;i<100;i++) {
                token.ThrowIfCancellationRequested();
                if(File.Exists(jobPath+".ready")) {File.WriteAllText(jobPath+".proceed","proceed");return;}
                if(process.HasExited)throw new IOException(L.T("Updater could not start. The application is still running."));
                await Task.Delay(100,token);
            }
            throw new IOException(L.T("Updater did not respond. The application is still running."));
        }catch {File.WriteAllText(jobPath+".cancel","cancel");throw;}
    }
    static bool WaitForExit(ProcessIdentity identity,int timeout) {
        try {
            using var process=Process.GetProcessById(identity.Id);
            if(process.StartTime.ToUniversalTime().Ticks!=identity.Started)return true;
            return process.WaitForExit(timeout);
        }catch(ArgumentException) {return true;}catch(InvalidOperationException) {return true;}
    }
    internal static bool SuccessfulExit(int code)=>code is 0 or 3010;
    internal static int InstallAfterExit(Job job,Func<ProcessIdentity,bool> wait,Func<int> install,Action restart) {
        if(!wait(job.Parent))throw new IOException(L.T("Application did not close. Update was not installed."));
        if(job.Controller is not null&&!wait(job.Controller))throw new IOException(L.T("Controller did not close. Update was not installed."));
        try {return install();}finally {restart();}
    }
    public static int Run(string jobPath) {
        string? failure=null;int result=1;
        try {
            var job=JsonSerializer.Deserialize<Job>(File.ReadAllText(jobPath))??throw new IOException("Invalid update job");
            if(!job.Package.EndsWith(".msi",StringComparison.OrdinalIgnoreCase)||!IsInstalled(job.Application))throw new IOException(L.T("The installed application could not be located."));
            using(var input=File.OpenRead(job.Package))if(!string.Equals(Convert.ToHexString(SHA256.HashData(input)),job.Hash,StringComparison.OrdinalIgnoreCase))throw new IOException(L.T("Downloaded file failed checksum verification."));
            File.WriteAllText(jobPath+".ready","ready");
            for(int i=0;!File.Exists(jobPath+".proceed");i++) {
                if(i>=150||File.Exists(jobPath+".cancel"))return 1;
                Thread.Sleep(100);
            }
            result=InstallAfterExit(job,identity=>WaitForExit(identity,60000),()=> {
                var start=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"msiexec.exe")) {UseShellExecute=true,Verb="runas"};
                foreach(var argument in new[]{"/i",job.Package,"/quiet","/norestart","/L*v",Path.Combine(Path.GetDirectoryName(jobPath)!,"install.log")})start.ArgumentList.Add(argument);
                using var installer=Process.Start(start)??throw new IOException(L.T("Could not start Windows Installer."));
                installer.WaitForExit();return installer.ExitCode;
            },()=> {
                try {Process.Start(new ProcessStartInfo(job.Application) {UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(job.Application)});}
                catch(Exception e) {failure=L.T("Could not reopen the application: ")+e.Message;}
            });
            if(!SuccessfulExit(result))failure=L.F("Update failed (code {0}). Installer log: {1}",result,Path.Combine(Path.GetDirectoryName(jobPath)!,"install.log"));
            else if(result==3010)failure=L.T("Update installed. Windows needs a restart to finish applying it.");
        }catch(Win32Exception e) when(e.NativeErrorCode==1223) {failure=L.T("Update cancelled. The previous version will reopen.");}
        catch(Exception e) {failure=L.T("Update could not be completed: ")+e.Message;}
        finally {
            try {File.WriteAllText(jobPath+".result",failure??(SuccessfulExit(result)?"Update installed":"Update not installed"));}catch(IOException) { }catch(UnauthorizedAccessException) { }
            if(failure is not null)System.Windows.Forms.MessageBox.Show(failure,"R-Helper Compact",System.Windows.Forms.MessageBoxButtons.OK,System.Windows.Forms.MessageBoxIcon.Information);
        }
        return result;
    }
    [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern uint MsiEnumRelatedProducts(string upgradeCode,uint reserved,uint index,StringBuilder productCode);
    [DllImport("msi.dll",CharSet=CharSet.Unicode)] static extern int MsiGetComponentPath(string productCode,string componentCode,StringBuilder path,ref uint length);
}
