using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Text;
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
    readonly CheckBox includeDescription=new() { Content=L.T("Include my reviewed description and corrections"),IsChecked=false };
    readonly TextBox tried=new() { Header=L.T("What did you try, and what happened?"),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=2000 };
    readonly TextBox working=new() { Header=L.T("What works in Compact and Synapse?"),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=2000 };
    readonly TextBox comparison=new() { Header=L.T("Optional comparison: tool, sensors, workload, measured draw or power limit, Synapse running"),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=2000 };
    readonly TextBox components=new() { Header=L.T("Optional component replacement history"),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxLength=2000 };
    readonly Queue<string> commands=new();
    string context="Unavailable",automaticReport="";
    bool transportConnected=true;
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
    internal string ReportText=>identifiers.Text+"\n\n"+(includeDescription.IsChecked==true
        ?$"Reviewed user description (user-entered; not automatic evidence):\nModel correction: {model.Text}\nFirmware correction: {firmware.Text}\nTried/result: {tried.Text}\nWorking features / Synapse: {working.Text}\nComparison: {comparison.Text}\nComponent history: {components.Text}\nComment: {notes.Text}"
        :"User description and corrections omitted: inclusion was not selected.");
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
        model.MaxLength=firmware.MaxLength=notes.MaxLength=2000;
        form.Children.Add(model);form.Children.Add(modelSource);form.Children.Add(firmware);form.Children.Add(tried);form.Children.Add(working);
        var comparisons=new Expander {Header=L.T("Optional comparison and component history"),Content=new StackPanel {Spacing=6,Children={comparison,components}}};
        form.Children.Add(comparisons);form.Children.Add(notes);form.Children.Add(includeDescription);form.Children.Add(identifiers);
        form.Children.Add(new TextBlock { Text=L.T("The automatic report includes identity, capabilities, settings, command outcomes and temperature sources. Raw logs, serial numbers and device paths are excluded. Description and corrections are included only when you select the option above. Review before sharing."),TextWrapping=TextWrapping.Wrap });
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
        copy.Click+=(_,_)=> { try { var data=new DataPackage();data.SetText(ReportText);Clipboard.SetContent(data);feedback.Text=L.T("Request copied. Review it before posting."); }catch(Exception e){feedback.Text=e.Message;} };
        var open=new Button { Content=L.T("Open GitHub issue"),IsEnabled=repository is not null };
        open.Click+=(_,_)=> { try {
            if(repository is null)return;
            // Never put diagnostics or user input into a browser URL.
            string url=repository+"/issues/new?template=device_support.md";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute=true });
            feedback.Text=L.T("Paste the copied request or attach the saved report in GitHub. Nothing is submitted automatically.");
        }catch(Exception e){feedback.Text=e.Message;} };
        form.Children.Add(copy);form.Children.Add(open);form.Children.Add(feedback);
        feedback.Text=repository is null?L.T("GitHub is not configured. You can copy the request."):L.T("This opens a GitHub draft. You submit it yourself.");
    }
    public void Update(JsonElement report,string detectedModel,bool ready=false,bool connectionError=false) {
        string fingerprint=report.ToString()+detectedModel+ready+connectionError;if(previous==fingerprint)return;previous=fingerprint;
        allowed=report.TryGetProperty("supported",out var supported)&&supported.ValueKind==JsonValueKind.True;
        string identity=report.TryGetProperty("identity",out var host)?host.ToString():"unavailable";
        bool experimental=report.TryGetProperty("experimental",out var experimentalValue)&&experimentalValue.ValueKind==JsonValueKind.True;
        heading.Visibility=reasonText.Visibility=allowed&&!experimental?Visibility.Collapsed:Visibility.Visible;
        heading.Text=allowed?(experimental?L.T("Experimental support"):L.T("Diagnostics and support")):identity=="non_razer"?L.T("Not a Razer Blade laptop"):identity=="razer"?L.T("Razer laptop not connected"):L.T("Laptop identity unavailable");
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
        automaticReport=$"R-Helper Compact diagnostic report\nModel: {safeModel}\nModel source: {(source is "sku_catalogue" or "windows_family"?source:"controller identity")}\nBIOS version (Windows): {bios}\nEC version (Windows): {ec}\nHost identity: {(identity is "razer" or "non_razer" ? identity : "unavailable")}\nControl authorization: {(allowed?(experimental?"experimental":"verified"):"blocked")}\nConnection: {connection}\nChassis SKU: {sku}\nHID (enumeration only):\n{hid}\nReason: {reason}\nWindows: {Environment.OSVersion.Version}\nApplication: {Assembly.GetExecutingAssembly().GetName().Version}\n\nSupport-check log (current UI session):\n{string.Join("\n",events)}\n\nRaw logs, serial numbers and device paths are excluded. Acknowledgement and readback are not user-confirmed hardware behavior.";
        RefreshReport();
    }
    void RefreshReport()=>identifiers.Text=automaticReport+"\n\nCurrent controller evidence (last received snapshot):\n"+context
        +"\nTransport: "+(transportConnected?"connected":"disconnected; evidence retained, not current")
        +"\n\nUser command journal (last 64 commands, current UI session):\n"+string.Join("\n",commands);
    internal void UpdateState(JsonElement state,bool connected=true) {
        transportConnected=connected;
        if(state.ValueKind==JsonValueKind.Object)context=JsonSerializer.Serialize(Context(state),new JsonSerializerOptions {WriteIndented=true});
        RefreshReport();
    }
    static readonly string[] Modes={"Balanced","Performance","Custom","Silent","Battery","Hyperboost"};
    static readonly string[] Levels={"Low","Medium","High","Boost"};
    static readonly string[] Features={"perf","fan","kbd-backlight","lid-logo","lights-always-on","battery-care","cpu-boost","gpu-boost","max-fan"};
    static readonly string[] Actions={"performance","cpu","gpu","fan_auto","fan_rpm","brightness","logo","lights","battery","keyboard_effect","lighting_external","auto_profiles","save_ac","save_battery","cap","cap_rpm","startup","reconnect","diagnostic_mode","diagnostic_test","pad_mode","pad_manual","pad_min","pad_max","pad_off","pad_full","pad_follow","pad_light_mode","pad_brightness","pad_color","pad_effect"};
    static bool Flag(JsonElement value,string key)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
    static object? Scalar(JsonElement value,string key,string[]? choices=null) {
        if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty(key,out var v))return null;
        if(choices is not null)return v.ValueKind==JsonValueKind.String&&choices.Contains(v.GetString())?v.GetString():null;
        return v.ValueKind==JsonValueKind.True?true:v.ValueKind==JsonValueKind.False?false:null;
    }
    static double? Numeric(JsonElement value,string key,double min,double max) =>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)&&double.IsFinite(n)&&n>=min&&n<=max?n:null;
    static string[] List(JsonElement value,string key,string[] choices)=>value.ValueKind==JsonValueKind.Object&&value.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Array?v.EnumerateArray().Take(16).Where(x=>x.ValueKind==JsonValueKind.String&&choices.Contains(x.GetString())).Select(x=>x.GetString()!).Distinct().ToArray():Array.Empty<string>();
    static Dictionary<string,object?> Readback(JsonElement state)=>new() {
        ["device_age_ms"]=Numeric(state,"device_age_ms",0,long.MaxValue),["ready"]=Scalar(state,"ready"),
        ["mode"]=Scalar(state,"mode",Modes),["cpu_boost"]=Scalar(state,"cpu_boost",Levels),["gpu_boost"]=Scalar(state,"gpu_boost",Levels),
        ["fan_auto"]=Scalar(state,"fan_auto"),["fan_rpm"]=Numeric(state,"fan_rpm",0,10000),["brightness"]=Numeric(state,"brightness",0,255),
        ["logo"]=Scalar(state,"logo",new[]{"Off","Static","Breathing"}),["battery"]=Scalar(state,"battery",new[]{"Disable","Percent50","Percent55","Percent60","Percent65","Percent70","Percent75","Percent80"}),["ac"]=Scalar(state,"ac")
    };
    static Dictionary<string,object?> Context(JsonElement state) {
        var data=Readback(state);
        data["readback_fresh"]=Numeric(state,"device_age_ms",0,5000) is not null;
        data["ready"]=Scalar(state,"ready");data["device_age_ms"]=Numeric(state,"device_age_ms",0,long.MaxValue);
        data["support_status"]=Scalar(state,"support_status",new[]{"supported","experimental","unsupported"});
        data["available_modes"]=List(state,"modes",Modes);data["cpu_options"]=List(state,"cpu_options",Levels);data["gpu_options"]=List(state,"gpu_options",Levels);
        data["features"]=List(state,"features",Features);data["lighting_external"]=Scalar(state,"lighting_external");data["session_locked"]=Scalar(state,"session_locked");data["diagnostic_active"]=state.TryGetProperty("diagnostic",out var diagnostic)&&Flag(diagnostic,"active");
        if(state.TryGetProperty("support_context",out var identity)&&identity.ValueKind==JsonValueKind.Object) {
            string pid=Field(identity,"selected_pid");
            data["controller"]=new {selected_pid=Regex.IsMatch(pid,@"\A1532:[0-9A-Fa-f]{4}\z")?pid:null,
                registry_match=Scalar(identity,"registry_match",new[]{"exact_pid_sku","pid_sku_mismatch","unregistered_pid","unavailable"}),
                verification=Scalar(identity,"verification",new[]{"user_confirmed","upstream_profile","candidate"}),registry_enabled=Scalar(identity,"registry_enabled"),
                protocol=Scalar(identity,"protocol",new[]{"Legacy4","Modern6","Discovery"}),keyboard_protocol=Scalar(identity,"keyboard_protocol",new[]{"none","standard_matrix_ff"})};
        }
        var features=List(state,"features",Features);
        string Gate(string feature,bool lighting=false)=>Field(state,"support_status")=="unsupported"?"host_or_device_blocked":!Flag(state,"ready")?"device_unavailable":Flag(state,"session_locked")?"windows_session_locked":data["diagnostic_active"] is true?"diagnostic_session_active":lighting&&Flag(state,"lighting_external")?"external_lighting_owner":!features.Contains(feature)?"capability_unavailable":"available";
        data["controls"]=new {performance=Gate("perf"),fans=Gate("fan"),battery=Gate("battery-care"),keyboard_brightness=Gate("kbd-backlight",true),lid_logo=Gate("lid-logo",true),idle_lighting=Gate("lights-always-on",true),
            keyboard_effects=Gate("kbd-backlight",true)!="available"?Gate("kbd-backlight",true):!Flag(state,"keyboard_effect_supported")?"keyboard_effect_protocol_unavailable":"available",
            cpu=Gate("perf")!="available"?Gate("perf"):Field(state,"mode")!="Custom"?"requires_custom_mode":List(state,"cpu_options",Levels).Length==0?"no_available_options":"available",
            gpu=Gate("perf")!="available"?Gate("perf"):Field(state,"mode")!="Custom"?"requires_custom_mode":List(state,"gpu_options",Levels).Length==0?"no_available_options":"available"};
        if(state.TryGetProperty("thermal_evidence",out var thermal)&&thermal.ValueKind==JsonValueKind.Object) {
            var t=new Dictionary<string,object?> { ["cpu_source"]=Scalar(thermal,"cpu_source",new[]{"lhm","perf_counter","acpi"}),["gpu_source"]=Scalar(thermal,"gpu_source",new[]{"lhm","nvml"}),["age_ms"]=Numeric(thermal,"age_ms",0,long.MaxValue),["fresh"]=Scalar(thermal,"fresh") };
            foreach(string field in new[]{"cpu_raw_c","gpu_raw_c","cpu_filtered_c","gpu_filtered_c"})t[field]=Numeric(thermal,field,-20,150);
            data["temperatures"]=t;
        } else data["temperatures"]=new {status="unavailable",age_ms=Numeric(state,"thermal_age_ms",0,long.MaxValue)};
        return data;
    }
    static object? CommandValue(string action,JsonElement value) {
        if(value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)return null;
        if(value.ValueKind is JsonValueKind.True or JsonValueKind.False)return new[]{"fan_auto","lights","lighting_external","auto_profiles","cap","startup","diagnostic_mode","pad_follow"}.Contains(action)?value.GetBoolean():null;
        if(value.ValueKind==JsonValueKind.Number&&value.TryGetInt32(out int n)) {
            int max=action is "brightness" or "pad_brightness"?15:action is "pad_off" or "pad_full"?100:10000;
            return new[]{"fan_rpm","cap_rpm","brightness","pad_brightness","pad_manual","pad_min","pad_max","pad_off","pad_full"}.Contains(action)&&n>=0&&n<=max?n:null;
        }
        if(value.ValueKind==JsonValueKind.String) {
            string[] choices=action switch {"performance"=>Modes,"cpu" or "gpu"=>Levels,"logo"=>new[]{"Off","Static","Breathing"},"battery"=>new[]{"Disable","Percent50","Percent55","Percent60","Percent65","Percent70","Percent75","Percent80"},"pad_mode"=>new[]{"off","manual","auto"},"pad_light_mode"=>new[]{"Off","Static","Wave","Breathing","Spectrum"},_=>Array.Empty<string>()};
            return choices.Contains(value.GetString())?value.GetString():null;
        }
        if(action=="pad_color")return RgbValue(value);
        if(action=="diagnostic_test"&&value.ValueKind==JsonValueKind.Object&&value.TryGetProperty("operation",out var operation)&&operation.ValueKind==JsonValueKind.Object) {
            string kind=Field(operation,"kind");
            string[] kinds={"performance","fan_mode","fan_rpm","cpu_boost","gpu_boost","max_fan","battery","brightness","logo","idle_lighting","effect"};
            string command=kind switch {"cpu_boost"=>"cpu","gpu_boost"=>"gpu","effect"=>"keyboard_effect",_=>kind};
            object? selected=null;
            if(operation.TryGetProperty("value",out var argument)) {
                selected=kind=="brightness"?Numeric(operation,"value",0,255):kind=="fan_mode"?Scalar(operation,"value",new[]{"Auto","Manual"}):kind is "idle_lighting" or "max_fan"?Scalar(operation,"value",new[]{"Enable","Disable"}):CommandValue(command,argument);
            }
            return new {protocol=Scalar(value,"protocol",new[]{"Legacy4","Modern6","StandardMatrix","ExtendedMatrix"}),write=Scalar(value,"write"),operation=new {kind=kinds.Contains(kind)?kind:null,value=selected}};
        }
        if(action is "keyboard_effect" or "pad_effect"&&value.ValueKind==JsonValueKind.Object) {
            object? Rgb(string key)=>value.TryGetProperty(key,out var rgb)?RgbValue(rgb):null;
            if(action=="pad_effect")return new {mode=Scalar(value,"mode",new[]{"Off","Static","Wave","Breathing","Spectrum"}),color=Rgb("color")};
            return new {effect=Scalar(value,"effect",new[]{"off","static","wave","breathing","breathing_dual","reactive","starlight","spectrum"}),color=Rgb("color"),color2=Rgb("color2"),direction=Scalar(value,"direction",new[]{"left","right"}),speed=Numeric(value,"speed",1,4)};
        }
        return null;
    }
    static object? RgbValue(JsonElement rgb)=>rgb.ValueKind==JsonValueKind.Array&&rgb.GetArrayLength()==3&&rgb.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.Number&&x.TryGetInt32(out int c)&&c>=0&&c<=255)?rgb.EnumerateArray().Select(x=>x.GetInt32()).ToArray():null;
    static string SafeError(string error)=>error.Contains("diagnostic controls",StringComparison.OrdinalIgnoreCase)?"diagnostic_session_active":error.Contains("Device busy",StringComparison.OrdinalIgnoreCase)?"device_busy":error.Contains("identity",StringComparison.OrdinalIgnoreCase)?"identity_check_failed":error.Contains("Device is not ready",StringComparison.OrdinalIgnoreCase)?"device_unavailable":error.Contains("locked",StringComparison.OrdinalIgnoreCase)?"windows_session_locked":error.Contains("external",StringComparison.OrdinalIgnoreCase)||error.Contains("OpenRGB",StringComparison.OrdinalIgnoreCase)?"external_lighting_owner":error.Contains("Unsupported",StringComparison.OrdinalIgnoreCase)||error.Contains("Invalid",StringComparison.OrdinalIgnoreCase)||error.Contains("out of range",StringComparison.OrdinalIgnoreCase)?"option_rejected":error.Contains("closed",StringComparison.OrdinalIgnoreCase)||error.Contains("exited",StringComparison.OrdinalIgnoreCase)?"controller_disconnected":error.Contains("cancel",StringComparison.OrdinalIgnoreCase)||error.Contains("timeout",StringComparison.OrdinalIgnoreCase)?"timeout_or_cancelled":"command_failed_details_omitted";
    internal void RecordCommand(string action,object? value,JsonElement state,string? error=null) {
        if(action is "snapshot" or "visibility" or "cap_editing" or "brightness_editing")return;
        string safeAction=Actions.Contains(action)?action:"unknown_command";
        using var serialized=JsonDocument.Parse(JsonSerializer.Serialize(value));
        commands.Enqueue(JsonSerializer.Serialize(new {at=DateTime.UtcNow.ToString("O"),action=safeAction,value=CommandValue(safeAction,serialized.RootElement),result=error is null?"controller_acknowledged":"failed",error=error is null?null:SafeError(error),readback=Readback(state),readback_note="last reported state; not hardware verification"}));
        while(commands.Count>64)commands.Dequeue();RefreshReport();
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
    internal static void EvidenceSmokeTest() {
        var panel=new DeviceIssuePanel();
        using var state=JsonDocument.Parse("""{"support_status":"supported","ready":true,"device_age_ms":100,"ac":true,"mode":"Custom","modes":["Balanced","Custom"],"cpu_boost":"High","gpu_boost":"Medium","cpu_options":["Low","High"],"gpu_options":["Low","Medium"],"features":["perf","fan","lid-logo","kbd-backlight"],"keyboard_effect_supported":false,"support_context":{"selected_pid":"1532:02B7","registry_match":"pid_sku_mismatch","protocol":"Discovery","verification":"candidate","registry_enabled":false},"thermal_evidence":{"cpu_source":"perf_counter","gpu_source":"nvml","cpu_raw_c":96,"cpu_filtered_c":68,"gpu_raw_c":null,"gpu_filtered_c":null,"age_ms":1000,"fresh":true}}""");
        using var identity=JsonDocument.Parse("""{"supported":true,"experimental":false,"identity":"razer","sku":"RZ09-0483","reason":"User-confirmed registry profile."}""");
        panel.Update(identity.RootElement,"Razer Blade 16 (2023)",true);panel.UpdateState(state.RootElement);
        panel.RecordCommand("cpu","High",state.RootElement);
        panel.RecordCommand("keyboard_effect",new {effect="static",color=new[]{18,52,86}},state.RootElement,@"PRIVATE_SECRET path C:\Users\SECRET");
        panel.RecordCommand("reconnect",null,state.RootElement);
        panel.RecordCommand("diagnostic_test",new {protocol="ExtendedMatrix",write=true,operation=new {kind="brightness",value=99}},state.RootElement);
        panel.tried.Text="Reviewed symptoms";panel.working.Text="Synapse works";panel.comparison.Text="Comparison tool / sensor";panel.components.Text="Replaced components";
        panel.UpdateState(state.RootElement);
        string automatic=panel.ReportText;
        foreach(string expected in new[]{"1532:02B7","pid_sku_mismatch","Custom","High","perf_counter","96","68","keyboard_effect_protocol_unavailable","controller_acknowledged","command_failed_details_omitted","reconnect","omitted"})
            if(!automatic.Contains(expected))throw new InvalidOperationException("Missing support evidence: "+expected);
        if(automatic.Contains("SECRET")||automatic.Contains("Reviewed symptoms")||panel.tried.Text!="Reviewed symptoms")throw new InvalidOperationException("Support privacy or polling preservation failed");
        panel.includeDescription.IsChecked=true;
        if(!panel.ReportText.Contains("Reviewed symptoms")||!panel.ReportText.Contains("Replaced components"))throw new InvalidOperationException("Reviewed description missing");
        panel.includeDescription.IsChecked=false;
        string path=System.IO.Path.Combine(AppContext.BaseDirectory,"synthetic-support-request.txt");
        System.IO.File.WriteAllText(path,panel.ReportText);
        if(System.IO.File.ReadAllText(path)!=panel.ReportText)throw new InvalidOperationException("Support copy/save mismatch");
        panel.UpdateState(default,false);
        if(!panel.ReportText.Contains("disconnected")||!panel.ReportText.Contains("command_failed_details_omitted")||!panel.ReportText.Contains("pid_sku_mismatch"))throw new InvalidOperationException("Support disconnect lost evidence");
        using var hostile=JsonDocument.Parse("""{"support_status":"unsupported","ready":true,"features":["PRIVATE_SECRET"],"mode":"SECRET","cpu_boost":"SECRET","support_context":{"selected_pid":"1532:02B7 SECRET","registry_match":"SECRET","protocol":"SECRET"},"thermal_evidence":{"cpu_source":"SECRET","cpu_raw_c":999,"fresh":false,"age_ms":10000},"path":"PRIVATE_SECRET"}""");
        panel.UpdateState(hostile.RootElement);
        panel.RecordCommand("PRIVATE_SECRET",new {secret="PRIVATE_SECRET"},hostile.RootElement,"PRIVATE_SECRET");
        panel.RecordCommand("keyboard_effect",new {effect="SECRET",color=new[]{"SECRET","SECRET","SECRET"}},hostile.RootElement,"SECRET");
        panel.RecordCommand("diagnostic_test",new {protocol="SECRET",write=true,operation=new {kind="SECRET",value="SECRET"}},hostile.RootElement,"SECRET");
        if(panel.ReportText.Contains("SECRET")||!panel.ReportText.Contains("host_or_device_blocked")||!panel.ReportText.Contains("10000"))throw new InvalidOperationException("Hostile context escaped allowlist or stale age lost");
        for(int i=0;i<100;i++)panel.RecordCommand("cpu","High",state.RootElement);
        if(panel.commands.Count!=64||panel.ReportText.Length>100000)throw new InvalidOperationException("Unbounded support journal");
        using var blocked=JsonDocument.Parse("""{"support_status":"unsupported","supported":false,"identity":"non_razer"}""");
        panel.Update(blocked.RootElement,"Unknown");
        if(string.IsNullOrEmpty(panel.ReportText))throw new InvalidOperationException("Blocked support export unavailable");
        System.IO.File.AppendAllText(System.IO.Path.Combine(AppContext.BaseDirectory,"smoke-checks.txt"),"\nPASS: support PID/SKU mismatch, modes/levels/AC, capability and keyboard/logo restrictions, raw/filtered temperature sources and expiry, successful/failed commands and reconnect, preserved reviewed fields, identical copy/save evidence, disconnect retention, bounded journal and hostile-input filtering. No HID or controller used; save picker and GitHub submission remain manual.");
    }
    internal void ExpandReport(bool expanded)=>details.IsExpanded=expanded;
    internal static string? ValidateRepository(string? value) {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="github.com"||uri.UserInfo!=""||!uri.IsDefaultPort||uri.Query!=""||uri.Fragment!="")return null;
        var parts=uri.AbsolutePath.Trim('/').Split('/');
        if(parts.Length!=2)return null;
        foreach(var part in parts)if(!System.Text.RegularExpressions.Regex.IsMatch(part,@"^[A-Za-z0-9_.-]+$")||part is "." or "..")return null;
        return "https://github.com/"+string.Join("/",parts);
    }
    internal static string? ReadRepository() => ValidateRepository(System.Linq.Enumerable.FirstOrDefault(
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>(),x=>x.Key=="RepositoryUrl")?.Value);
}
