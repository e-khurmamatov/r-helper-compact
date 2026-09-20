using System;
using System.Diagnostics;
using System.Reflection;
using System.Linq;
using System.Text.RegularExpressions;
using Windows.Storage.Pickers;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace RHelper.Compact;
internal sealed class DeviceIssuePanel : StackPanel
{
    readonly TextBox model=new() { Header=L.T("Laptop model"),PlaceholderText="Razer Blade / RZ09-…" };
    readonly TextBox firmware=new() { Header="BIOS / EC",PlaceholderText=L.T("Not available automatically"),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap };
    readonly TextBox notes=new() { Header=L.T("Comment"),AcceptsReturn=true,MinHeight=64,TextWrapping=TextWrapping.Wrap };
    readonly TextBox identifiers=new() { Header=L.T("Diagnostics (no serial number)"),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=130 };
    readonly TextBlock feedback=new() { TextWrapping=TextWrapping.Wrap,Opacity=.7 };
    readonly string? repository=ReadRepository();
    string previous="";
    string previousModel="";
    string previousFirmware="";
    readonly TextBlock modelSource=new() { TextWrapping=TextWrapping.Wrap,Opacity=.7 };
    public nint WindowHandle { get; set; }
    readonly TextBlock heading=new() { FontSize=18,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold };
    readonly Expander details=new() { Header=L.T("Diagnostics and support"),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch };
    bool allowed;
    readonly TextBlock reasonText=new() { TextWrapping=TextWrapping.Wrap };
    readonly System.Collections.Generic.Queue<string> events=new();
    internal string ReportText=>identifiers.Text;
    static readonly string[] Reasons={
        "Experimental support: this laptop has not been verified with Compact. Some controls may not work.",
        "User-confirmed registry profile.",
        "This computer is not identified as a Razer Blade laptop. Controls are disabled.",
        "Laptop manufacturer or SKU is unavailable. Controls are disabled.",
        "Multiple Blade HID controllers found. Controls are disabled.",
        "Razer laptop detected, but its Blade HID controller could not be identified. Controls are disabled.",
        "Multiple registry matches. Controls are disabled.",
        "Laptop SKU is unavailable. Controls are disabled.",
        "No Razer HID identifiers found. Controls are disabled.",
        "This registry entry is disabled pending verification. Controls are disabled.",
        "Unsupported model: no unique SKU and HID PID match in the registry. Controls are disabled.",
        "Could not enumerate HID devices. Controls are disabled."
    };
    internal static string SafeReason(string reason)=>Reasons.Contains(reason)?reason:"Support could not be verified. Controls are disabled.";
    internal static string SafeSku(string value)=>Regex.IsMatch(value,@"^RZ09-[0-9]{4}")?value[..9]:"Unavailable";
    internal static string SafeHid(string value)=>Regex.IsMatch(value,@"\A1532:[0-9A-Fa-f]{4} interface=-?[0-9]{1,3} usage=[0-9A-Fa-f]{4}:[0-9A-Fa-f]{4}\z")?value:"Omitted invalid HID identifier";
    internal static string SafeVersion(string value)=>Regex.IsMatch(value,@"\A[vV]?[0-9]{1,3}(\.[0-9]{1,3}){1,3}\z")?value:"Unavailable";
    internal static string SafeModel(string value)=>Regex.IsMatch(value,@"\ARazer Blade (Pro |Stealth )?(13|14|15|16|17|18)( Advanced| Base)?( \((Early |Mid |Late )?[0-9]{4}\))?\z")?value:"Unavailable";
    static string Field(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.String?item.GetString()??"":"";
    static string NormalizedLines(string text)=>text.Replace("\r\n","\n").Replace('\r','\n');
    public DeviceIssuePanel() {
        Spacing=6;
        Children.Add(heading);
        Children.Add(reasonText);
        var form=new StackPanel { Spacing=6 };
        details.Content=form;Children.Add(details);
        form.Children.Add(new TextBlock { Text=L.T("Details are filled automatically when available. Describe what does not work; missing details are optional. You can correct the fields below."),TextWrapping=TextWrapping.Wrap });
        form.Children.Add(model);form.Children.Add(modelSource);form.Children.Add(firmware);form.Children.Add(notes);form.Children.Add(identifiers);
        form.Children.Add(new TextBlock { Text=L.T("The report contains only model, chassis SKU, USB identifiers, app/Windows/BIOS/EC versions and support-check events. Raw logs and personal fields are excluded. Review it before attaching it to an issue."),TextWrapping=TextWrapping.Wrap });
        var save=new Button { Content=L.T("Save diagnostic report…") };
        save.Click+=async (_,_)=> { try {
            string reviewedReport=ReportText;
            var picker=new FileSavePicker { SuggestedFileName="r-helper-compact-diagnostics" };
            picker.FileTypeChoices.Add("Text report",new[]{".txt"});
            WinRT.Interop.InitializeWithWindow.Initialize(picker,WindowHandle);
            var file=await picker.PickSaveFileAsync();
            if(file is null)return;
            await Windows.Storage.FileIO.WriteTextAsync(file,reviewedReport);
            feedback.Text=L.T("Report saved. Review the text file and attach it yourself.");
        }catch(Exception){feedback.Text=L.T("Could not save the report. You can copy the request instead.");} };
        form.Children.Add(save);
        var copy=new Button { Content=L.T("Copy request") };
        copy.Click+=(_,_)=> { try { var data=new DataPackage();data.SetText(Body());Clipboard.SetContent(data);feedback.Text=L.T("Request copied. Review it before posting."); }catch(Exception e){feedback.Text=e.Message;} };
        var open=new Button { Content=L.T("Open GitHub issue"),IsEnabled=repository is not null };
        open.Click+=(_,_)=> { try {
            if(repository is null)return;
            // Never put diagnostics or user input into a browser URL.
            string url=repository+"/issues/new?template=device_support.md";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute=true });
        }catch(Exception e){feedback.Text=e.Message;} };
        form.Children.Add(copy);form.Children.Add(open);form.Children.Add(feedback);
        feedback.Text=repository is null?L.T("GitHub is not configured. You can copy the request."):L.T("This opens a GitHub draft. You submit it yourself.");
    }
    public void Update(JsonElement report,string detectedModel,bool ready=false,bool connectionError=false) {
        string fingerprint=report.ToString()+detectedModel+ready+connectionError;if(previous==fingerprint)return;previous=fingerprint;
        allowed=report.TryGetProperty("supported",out var supported)&&supported.ValueKind==JsonValueKind.True;
        string identity=report.TryGetProperty("identity",out var host)?host.ToString():"unavailable";
        heading.Text=allowed?L.T("Experimental support"):identity=="non_razer"?L.T("Not a Razer Blade laptop"):identity=="razer"?L.T("Razer laptop not connected"):L.T("Laptop identity unavailable");
        if(report.TryGetProperty("model",out var name)&&name.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(name.GetString()))detectedModel=name.GetString()!;
        JsonElement machine=report.TryGetProperty("machine",out var metadata)?metadata:default;
        string automaticModel=SafeModel(Field(machine,"model"));
        string source=Field(machine,"model_source");
        if(automaticModel!="Unavailable" && (source=="sku_catalogue" || source=="windows_family"&&SafeModel(detectedModel)=="Unavailable"))detectedModel=automaticModel;
        else source=SafeModel(detectedModel)!="Unavailable"?"controller_identity":"unavailable";
        modelSource.Text=(source=="sku_catalogue"||source=="controller_identity")?L.T("Model from chassis SKU catalogue; this does not confirm control compatibility."):
            source=="windows_family"?L.T("Model family reported by Windows; exact year is unknown."):L.T("Exact model could not be determined automatically. You can leave it unchanged.");
        string bios=SafeVersion(Field(machine,"bios_version"));
        string ec=SafeVersion(Field(machine,"ec_version"));
        string automaticFirmware=$"BIOS: {(bios=="Unavailable"?L.T("Not available automatically"):bios)}\nEC: {(ec=="Unavailable"?L.T("Not available automatically"):ec)}";
        if(firmware.Text.Length==0 || NormalizedLines(firmware.Text)==NormalizedLines(previousFirmware))firmware.Text=automaticFirmware;
        previousFirmware=automaticFirmware;
        if(model.Text.Length==0 || model.Text==previousModel)model.Text=L.T(detectedModel);
        previousModel=L.T(detectedModel);
        string sku=SafeSku(report.TryGetProperty("sku",out var s)&&s.ValueKind==JsonValueKind.String?s.GetString()??"":"");
        string hid=report.TryGetProperty("hid",out var h)&&h.ValueKind==JsonValueKind.Array
            ?string.Join("\n",h.EnumerateArray().Take(64).Select(x=>SafeHid(x.ValueKind==JsonValueKind.String?x.GetString()??"":""))):"Unavailable";
        string reason=SafeReason(report.TryGetProperty("reason",out var why)?why.ToString():"");
        reasonText.Text=L.T(reason);
        // Only fixed support events enter this bounded log; never import raw controller logs.
        string connection=!allowed?"blocked":ready?"ready":connectionError?"connection or device read failed":"awaiting device response";
        events.Enqueue($"{DateTime.UtcNow:O} Support check: {reason} Connection: {connection}");
        while(events.Count>12)events.Dequeue();
        string safeModel=SafeModel(detectedModel);
        identifiers.Text=$"R-Helper Compact diagnostic report\nModel: {safeModel}\nModel source: {(source is "sku_catalogue" or "windows_family"?source:"controller identity")}\nBIOS version (Windows): {bios}\nEC version (Windows): {ec}\nHost identity: {(identity is "razer" or "non_razer" ? identity : "unavailable")}\nControl authorization: {(allowed?"experimental":"blocked")}\nConnection: {connection}\nChassis SKU: {sku}\nHID (enumeration only):\n{hid}\nReason: {reason}\nWindows: {Environment.OSVersion.Version}\nApplication: {Assembly.GetExecutingAssembly().GetName().Version}\n\nSupport-check log (current UI session):\n{string.Join("\n",events)}\n\nRaw logs, serial numbers, device paths, settings and user-entered fields are excluded.";
    }
    internal static void SmokeTest() {
        var panel=new DeviceIssuePanel();
        using var initial=JsonDocument.Parse("{}");
        panel.Update(initial.RootElement,"Unknown");
        using var sample=JsonDocument.Parse("""{"supported":true,"identity":"razer","model":"Razer Blade (unregistered model)","sku":"RZ09-0510","machine":{"model":"Razer Blade 16 (2024)","model_source":"sku_catalogue","bios_version":"1.09","ec_version":"1.2"}}""");
        panel.Update(sample.RootElement,"Unknown");
        if(panel.model.Text!="Razer Blade 16 (2024)" || !panel.firmware.Text.Contains("1.09") || !panel.ReportText.Contains("EC version (Windows): 1.2"))
            throw new InvalidOperationException("Automatic diagnostic fields failed");
        panel.model.Text="Owner correction";panel.firmware.Text="Owner firmware correction";panel.notes.Text="Symptoms";
        panel.Update(sample.RootElement,"Unknown",true);
        if(panel.model.Text!="Owner correction" || panel.firmware.Text!="Owner firmware correction" || panel.notes.Text!="Symptoms" || panel.ReportText.Contains("Owner"))
            throw new InvalidOperationException("Polling overwrote user edits or exported free text");
        using var missing=JsonDocument.Parse("""{"machine":{"model":"PRIVATE_SECRET","model_source":"windows_family","bios_version":"1.09 SECRET","ec_version":null}}""");
        panel.Update(missing.RootElement,"Unknown");
        if(panel.ReportText.Contains("SECRET") || !panel.ReportText.Contains("EC version (Windows): Unavailable"))
            throw new InvalidOperationException("Missing or unsafe firmware metadata was exported");
    }
    internal void ExpandReport(bool expanded)=>details.IsExpanded=expanded;
    string Body()=>$"### Laptop model\n{model.Text}\n\n### BIOS / EC\n{firmware.Text}\n\n### Diagnostics\n{identifiers.Text}\n\n### Comment\n{notes.Text}\n\n{(allowed?"Experimental control is authorized; hardware compatibility is not confirmed.":"Hardware controls are blocked.")}\n";
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
