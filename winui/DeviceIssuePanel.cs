using System;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace RHelper.Compact;
internal sealed class DeviceIssuePanel : StackPanel
{
    readonly TextBox model=new() { Header=L.T("Laptop model"),PlaceholderText="Razer Blade / RZ09-…" };
    readonly TextBox firmware=new() { Header=L.T("BIOS / EC (if known)"),PlaceholderText=L.T("Optional") };
    readonly TextBox notes=new() { Header=L.T("Comment"),AcceptsReturn=true,MinHeight=64,TextWrapping=TextWrapping.Wrap };
    readonly TextBox identifiers=new() { Header=L.T("Diagnostics (no serial number)"),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=130 };
    readonly TextBlock feedback=new() { TextWrapping=TextWrapping.Wrap,Opacity=.7 };
    readonly string? repository=ReadRepository();
    string previous="";
    public DeviceIssuePanel() {
        Spacing=6;
        Children.Add(new TextBlock { Text=L.T("Unsupported model"),FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold });
        Children.Add(new TextBlock { Text=L.T("Controls are disabled. Review the details below to request support for this laptop."),TextWrapping=TextWrapping.Wrap });
        Children.Add(model);Children.Add(firmware);Children.Add(notes);Children.Add(identifiers);
        var copy=new Button { Content=L.T("Copy request") };
        copy.Click+=(_,_)=> { try { var data=new DataPackage();data.SetText(Body());Clipboard.SetContent(data);feedback.Text=L.T("Request copied. Review it before posting."); }catch(Exception e){feedback.Text=e.Message;} };
        var open=new Button { Content=L.T("Open GitHub issue"),IsEnabled=repository is not null };
        open.Click+=(_,_)=> { try {
            if(repository is null)return;
            string url=repository+"/issues/new?template=device_support.md&title="+Uri.EscapeDataString("[Device] "+model.Text)+"&body="+Uri.EscapeDataString(Body());
            Process.Start(new ProcessStartInfo(url) { UseShellExecute=true });
        }catch(Exception e){feedback.Text=e.Message;} };
        Children.Add(copy);Children.Add(open);Children.Add(feedback);
        feedback.Text=repository is null?L.T("GitHub is not configured. You can copy the request."):L.T("This opens a GitHub draft. You submit it yourself.");
    }
    public void Update(JsonElement report,string detectedModel) {
        string fingerprint=report.ToString();if(previous==fingerprint)return;previous=fingerprint;
        model.Text=detectedModel;
        string sku=report.TryGetProperty("sku",out var s)?s.GetString()??"":"";
        string hid=report.TryGetProperty("hid",out var h)?string.Join("\n",System.Linq.Enumerable.Select(h.EnumerateArray(),x=>x.GetString())):"";
        string reason=report.TryGetProperty("reason",out var why)?why.ToString():"";
        identifiers.Text=$"SKU: {sku}\nHID:\n{hid}\n{L.T("Reason")}: {L.T(reason)}\nWindows: {Environment.OSVersion.Version}\n{L.T("Application")}: {Assembly.GetExecutingAssembly().GetName().Version}";
    }
    string Body()=>$"### Laptop model\n{model.Text}\n\n### BIOS / EC\n{firmware.Text}\n\n### Diagnostics\n{identifiers.Text}\n\n### Comment\n{notes.Text}\n\nHardware commands are blocked on unsupported models.\n";
    internal static string? ValidateRepository(string? value) {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="github.com"||uri.UserInfo!=""||!uri.IsDefaultPort||uri.Query!=""||uri.Fragment!="")return null;
        var parts=uri.AbsolutePath.Trim('/').Split('/');
        if(parts.Length!=2)return null;
        foreach(var part in parts)if(!System.Text.RegularExpressions.Regex.IsMatch(part,@"^[A-Za-z0-9_.-]+$")||part is "." or "..")return null;
        return "https://github.com/"+string.Join("/",parts);
    }
    static string? ReadRepository() => ValidateRepository(System.Linq.Enumerable.FirstOrDefault(
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>(),x=>x.Key=="RepositoryUrl")?.Value);
}
