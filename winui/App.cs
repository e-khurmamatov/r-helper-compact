using System;
using System.Threading;
using System.IO;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
namespace RHelper.Compact;
internal static class Program
{
    [STAThread] static void Main(string[] args)
    {
        if(args.Length==2&&args[0]=="--update-smoke-test") {
            try {UpdateSmoke.Run().GetAwaiter().GetResult();File.WriteAllText(args[1],"PASS: offline updater tests; no UI, controller or installer launched.");}
            catch(Exception e) {File.WriteAllText(args[1],e.ToString());Environment.ExitCode=1;}
            return;
        }
        if(args.Length==2&&args[0]=="--apply-update") {Environment.ExitCode=UpdateInstaller.Run(args[1]);return;}
        bool smoke=Array.IndexOf(args,"--ui-smoke-test")>=0;
        // Culture override is restricted to the controller-free UI test.
        if(smoke)foreach(var arg in args)if(arg.StartsWith("--ui-culture=",StringComparison.Ordinal)) {
            var culture=System.Globalization.CultureInfo.GetCultureInfo(arg[13..]);
            System.Globalization.CultureInfo.CurrentUICulture=culture;
            System.Globalization.CultureInfo.CurrentCulture=culture;
        }
        using var singleton=new Mutex(true,smoke?@"Local\RHelper.Compact.WinUI.Smoke":@"Local\RHelper.Compact.WinUI",out var first);
        if (!first) return;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters => {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new CompactApp(Array.IndexOf(args,"--ui-smoke-test")>=0);
        });
    }
}
public sealed partial class CompactApp : Application
{
    readonly bool smoke;
    MainWindow? window;
    public CompactApp(bool smoke) { this.smoke=smoke; UnhandledException += (_,e) => { if(smoke) File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-error.txt"),e.Exception.ToString()); }; InitializeComponent(); }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window=new MainWindow(smoke);
        if (smoke) window.ShowPopup();
    }
}
