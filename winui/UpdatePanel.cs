using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RHelper.Compact;

internal sealed class UpdatePanel : StackPanel,IDisposable
{
    readonly UpdateService service=new();
    readonly bool installed,smoke;
    readonly Func<string,string,CancellationToken,Task> install;
    readonly TextBlock status=new() {TextWrapping=TextWrapping.Wrap,Opacity=.75};
    readonly Button check=new() {Content=L.T("Check for updates")};
    readonly Button download=new() {Visibility=Visibility.Collapsed};
    readonly Button notes=new() {Content=L.T("What's new"),Visibility=Visibility.Collapsed};
    readonly Button cancel=new() {Content=L.T("Cancel"),Visibility=Visibility.Collapsed};
    readonly ProgressBar progress=new() {Minimum=0,Maximum=100,Visibility=Visibility.Collapsed};
    CancellationTokenSource? cancellation;
    UpdateRelease? release;
    bool disposed;
    public UpdatePanel(bool smoke,Func<string,string,CancellationToken,Task> install) {
        this.smoke=smoke;this.install=install;installed=!smoke&&UpdateInstaller.IsInstalled(Environment.ProcessPath!);Spacing=6;
        Children.Add(new TextBlock {Text=L.F("Version: {0}",UpdateService.CurrentVersion)});
        Children.Add(check);Children.Add(status);Children.Add(download);Children.Add(notes);Children.Add(progress);Children.Add(cancel);
        status.Text=L.T("Updates are checked only when you click the button.");
        check.Click+=async(_,_)=>await Run(false);
        download.Click+=async(_,_)=>await Run(true);
        cancel.Click+=(_,_)=>cancellation?.Cancel();
        notes.Click+=(_,_)=> {try {Process.Start(new ProcessStartInfo(release?.Page??UpdateService.Repository+"/releases") {UseShellExecute=true});}catch(Exception e){status.Text=e.Message;} };
    }
    async Task Run(bool downloading) {
        if(smoke||cancellation is not null||disposed)return;
        using var operation=new CancellationTokenSource(TimeSpan.FromMinutes(15));cancellation=operation;
        check.IsEnabled=download.IsEnabled=false;cancel.Visibility=Visibility.Visible;
        try {
            if(!downloading) {
                release=null;download.Visibility=notes.Visibility=Visibility.Collapsed;status.Text=L.T("Checking GitHub Releases…");
                release=await service.Check(installed,operation.Token);
                var current=ReleaseVersion.Parse(UpdateService.CurrentVersion)!;
                if(release is null) {status.Text=L.T("No compatible releases have been published yet.");return;}
                int comparison=release.Version.CompareTo(current);notes.Visibility=Visibility.Visible;
                status.Text=comparison==0?L.T("You have the latest version."):comparison<0?L.T("Your version is newer than the published release."):L.F("Version {0} is available.",release.Tag.TrimStart('v'));
                if(comparison>0) {
                    if(installed&&release.Version.Numeric<=current.Numeric) {status.Text+=" "+L.T("This release requires a manual reinstall. Open the release notes.");return;}
                    download.Content=L.T(installed?"Download and install":"Download portable ZIP");download.Visibility=Visibility.Visible;
                }
            } else if(release is not null) {
                progress.Value=0;progress.Visibility=Visibility.Visible;status.Text=L.T("Downloading and verifying…");
                string directory=Path.Combine(UpdateInstaller.CacheRoot,Guid.NewGuid().ToString("N"));
                var file=await service.Download(release,directory,new Progress<double>(value=>{if(!disposed)progress.Value=value;}),operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if(installed) {
                    status.Text=L.T("Preparing update. The application will close and reopen; Windows may request administrator permission.");
                    await install(file.Path,file.Hash,operation.Token);
                } else {
                    status.Text=L.T("ZIP verified. Extract it to a new folder to use the new version.");
                    Process.Start(new ProcessStartInfo("explorer.exe","/select,\""+file.Path+"\"") {UseShellExecute=true});
                }
            }
        }catch(OperationCanceledException) {if(!disposed)status.Text=operation.IsCancellationRequested?L.T("Update operation cancelled or timed out."):L.T("The request timed out. Try again.");}
        catch(Exception e) {if(!disposed)status.Text=L.T("Could not update: ")+e.Message;}
        finally {cancellation=null;if(!disposed) {check.IsEnabled=download.IsEnabled=true;cancel.Visibility=progress.Visibility=Visibility.Collapsed;} }
    }
    public void Dispose() {disposed=true;cancellation?.Cancel();service.Dispose();}
}
