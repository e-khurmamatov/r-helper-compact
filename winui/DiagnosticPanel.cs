using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace RHelper.Compact;
internal sealed class DiagnosticPanel : StackPanel
{
    readonly Func<string,object?,Task> send;
    readonly ToggleSwitch mode=new() { Header=L.T("Diagnostic mode") };
    readonly StackPanel controls=new() { Spacing=8 };
    readonly ContentControl controlHost=new() { HorizontalContentAlignment=HorizontalAlignment.Stretch };
    readonly StackPanel reportPanel=new() { Spacing=8 };
    readonly ComboBox function=new() { Header=L.T("Function") };
    readonly ComboBox protocol=new() { Header=L.T("Protocol") };
    readonly ComboBox option=new() { Header=L.T("Test value") };
    readonly NumberBox number=new() { Header=L.T("Test value"),SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact };
    readonly ColorPicker color=new() { IsAlphaEnabled=false,IsMoreButtonVisible=false,Color=Windows.UI.Color.FromArgb(255,0,255,0) };
    readonly ColorPicker color2=new() { IsAlphaEnabled=false,IsMoreButtonVisible=false,Color=Windows.UI.Color.FromArgb(255,0,128,255) };
    readonly ComboBox direction=new() { Header=L.T("Wave direction") };
    readonly ComboBox duration=new() { Header=L.T("Fade duration") };
    readonly Expander colorSection=new() { Header=L.T("Color"),HorizontalAlignment=HorizontalAlignment.Stretch };
    readonly Expander color2Section=new() { Header=L.T("Second color"),HorizontalAlignment=HorizontalAlignment.Stretch };
    readonly TextBox result=new() { Header=L.T("Device responses"),IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,MaxHeight=280 };
    readonly TextBox observation=new() { Header=L.T("Observed behavior"),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,PlaceholderText=L.T("Describe what actually changed on the laptop.") };
    readonly TextBlock feedback=new() { TextWrapping=TextWrapping.Wrap,Opacity=.7 };
    readonly NumberBox issue=new() { Header=L.T("Existing issue number (optional)"),Minimum=1,Maximum=int.MaxValue,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact };
    readonly Button read=new() { Content=L.T("Read value") },write=new() { Content=L.T("Write and read back") },save=new() { Content=L.T("Save session log…") },copy=new() { Content=L.T("Copy session log") },open=new() { Content=L.T("Open GitHub issue") };
    readonly string? repository=DeviceIssuePanel.ReadRepository();
    JsonElement report;
    bool syncing,working,active,available;
    long session;
    public nint WindowHandle { get;set; }
    internal bool Active=>active;
    internal string ReportText=>FormatReport(report)+"\nObserved behavior (user-entered; review before sharing):\n"+observation.Text;

    public DiagnosticPanel(Func<string,object?,Task> send) {
        this.send=send;Spacing=8;
        Children.Add(mode);
        Children.Add(new TextBlock { Text=L.T("Experiments use implemented protocols without changing the model registry or saved profiles. Laptop automation is paused during the session. Device settings may survive a reboot; ending the session does not restore them. Normal automation resumes when you end the session."),TextWrapping=TextWrapping.Wrap,Opacity=.7 });
        mode.Toggled+=async(_,_)=> { if(syncing||working)return;await Run("diagnostic_mode",mode.IsOn); };
        foreach(var pair in new[]{("performance","Performance"),("fan_mode","Fan control"),("fan_rpm","Speed, RPM"),("cpu_boost","CPU boost"),("gpu_boost","GPU boost"),("max_fan","Maximum fan speed"),("battery","Charge limit"),("brightness","Keyboard brightness"),("logo","Logo"),("idle_lighting","Keep backlight on while idle"),("effect","Standard effect")})
            function.Items.Add(new ComboBoxItem { Tag=pair.Item1,Content=L.T(pair.Item2) });
        foreach(var p in new[]{"Legacy4","Modern6","StandardMatrix","ExtendedMatrix"})protocol.Items.Add(new ComboBoxItem { Tag=p,Content=p });
        foreach(var value in new[]{"left","right"})direction.Items.Add(new ComboBoxItem { Tag=value,Content=L.T(value=="left"?"Left":"Right") });
        foreach(var value in new[]{"1","2","3","4"})duration.Items.Add(new ComboBoxItem { Tag=value,Content=value });
        direction.SelectedIndex=1;duration.SelectedIndex=1;
        function.SelectionChanged+=(_,_)=>Configure();protocol.SelectionChanged+=(_,_)=>ConfigureOptions();option.SelectionChanged+=(_,_)=>EffectOptions();
        controls.Children.Add(function);controls.Children.Add(protocol);controls.Children.Add(option);controls.Children.Add(number);
        colorSection.Content=color;color2Section.Content=color2;
        controls.Children.Add(colorSection);controls.Children.Add(color2Section);controls.Children.Add(direction);controls.Children.Add(duration);
        controls.Children.Add(new TextBlock { Text=L.T("Legacy4 and Modern6 share control commands and differ in available modes. Matrix protocols test keyboard commands. No model initialization sequence is sent. Set Custom before boost tests and Manual before RPM tests. Close Synapse and disable Windows Dynamic Lighting when testing lighting; external lighting ownership still blocks writes."),TextWrapping=TextWrapping.Wrap,Opacity=.7 });
        read.Click+=async(_,_)=>await Test(false);write.Click+=async(_,_)=>await Test(true);
        controls.Children.Add(read);controls.Children.Add(write);controlHost.Content=controls;Children.Add(controlHost);Children.Add(reportPanel);reportPanel.Children.Add(result);reportPanel.Children.Add(observation);
        reportPanel.Children.Add(new TextBlock { Text=L.T("The session log includes typed HID requests and responses, failed attempts, and values before and after a write. An acknowledged command or matching readback does not prove hardware behavior. Review the log and your comments before sharing. Nothing is uploaded automatically."),TextWrapping=TextWrapping.Wrap,Opacity=.7 });
        save.Click+=async(_,_)=>await Save();copy.Click+=(_,_)=> { try { var data=new DataPackage();data.SetText(ReportText);Clipboard.SetContent(data);feedback.Text=L.T("Request copied. Review it before posting."); }catch(Exception e){feedback.Text=e.Message;} };
        open.Click+=(_,_)=> { try {
            if(repository is null)return;
            string url=IssueUrl(repository,issue.Value);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute=true });
            feedback.Text=L.T("Attach the saved session log to the issue or its comment yourself.");
        }catch(Exception e){feedback.Text=e.Message;} };
        reportPanel.Children.Add(save);reportPanel.Children.Add(copy);reportPanel.Children.Add(issue);reportPanel.Children.Add(open);Children.Add(feedback);
        function.SelectedIndex=0;protocol.SelectedIndex=0;Availability();
    }
    static string Choice(ComboBox box)=>(box.SelectedItem as ComboBoxItem)?.Tag as string??"";
    void Configure() {
        string kind=Choice(function);
        bool keyboard=kind is "effect" or "brightness";
        foreach(var item in protocol.Items.Cast<ComboBoxItem>())item.IsEnabled=kind=="effect"?item.Tag as string is "StandardMatrix" or "ExtendedMatrix":keyboard||item.Tag as string is "Legacy4" or "Modern6";
        if(!keyboard&&Choice(protocol) is "StandardMatrix" or "ExtendedMatrix")protocol.SelectedIndex=0;
        if(kind=="effect"&&Choice(protocol) is "Legacy4" or "Modern6")protocol.SelectedIndex=2;
        number.Visibility=kind is "fan_rpm" or "brightness"?Visibility.Visible:Visibility.Collapsed;
        option.Visibility=number.Visibility==Visibility.Visible?Visibility.Collapsed:Visibility.Visible;
        if(kind=="fan_rpm") { number.Minimum=2000;number.Maximum=5500;number.SmallChange=100;number.Value=4000; }
        else if(kind=="brightness") { number.Minimum=0;number.Maximum=255;number.SmallChange=1;number.Value=128; }
        ConfigureOptions();
    }
    void ConfigureOptions() {
        string kind=Choice(function),previous=Choice(option);
        string[] values=kind switch {
            "performance"=>Choice(protocol)=="Modern6"?new[]{"Balanced","Performance","Custom","Silent","Battery","Hyperboost"}:new[]{"Balanced","Custom","Silent","Battery"},
            "fan_mode"=>new[]{"Auto","Manual"},
            "cpu_boost"=>new[]{"Low","Medium","High","Boost"},
            "gpu_boost"=>new[]{"Low","Medium","High"},
            "battery"=>new[]{"Disable","Percent50","Percent55","Percent60","Percent65","Percent70","Percent75","Percent80"},
            "logo"=>new[]{"Off","Static","Breathing"},
            "idle_lighting" or "max_fan"=>new[]{"Disable","Enable"},
            "effect"=>Choice(protocol)=="ExtendedMatrix"?new[]{"off","static","breathing"}:new[]{"off","static","wave","breathing","breathing_dual","reactive","starlight","spectrum"},
            _=>Array.Empty<string>() };
        option.Items.Clear();foreach(var value in values)option.Items.Add(new ComboBoxItem { Tag=value,Content=ValueLabel(value) });
        option.SelectedItem=option.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Tag as string==previous)??option.Items.Cast<ComboBoxItem>().FirstOrDefault();EffectOptions();
    }
    static string ValueLabel(string value)=>L.T(value switch { "off"=>"Off","static"=>"Solid color","wave"=>"Wave","breathing"=>"Breathing","breathing_dual"=>"Two-color breathing","reactive"=>"Reactive","starlight"=>"Starlight","spectrum"=>"Spectrum","Disable"=>"Off",_=>value });
    void EffectOptions() {
        string value=Choice(option);bool effect=Choice(function)=="effect";
        colorSection.Visibility=effect&&value is "static" or "breathing" or "breathing_dual" or "reactive"?Visibility.Visible:Visibility.Collapsed;
        color2Section.Visibility=effect&&value=="breathing_dual"?Visibility.Visible:Visibility.Collapsed;
        direction.Visibility=effect&&value=="wave"?Visibility.Visible:Visibility.Collapsed;
        duration.Visibility=effect&&value=="reactive"?Visibility.Visible:Visibility.Collapsed;
        read.IsEnabled=active&&!working&&!effect;
    }
    object Experiment(bool writing) {
        string kind=Choice(function);
        object value;
        if(kind is "fan_rpm" or "brightness") {
            if(!double.IsFinite(number.Value)||number.Value!=Math.Truncate(number.Value)||number.Value<number.Minimum||number.Value>number.Maximum||kind=="fan_rpm"&&number.Value%100!=0)
                throw new InvalidOperationException(L.T("Enter an integer within the displayed range. RPM uses steps of 100."));
            value=(int)number.Value;
        }else if(kind=="effect") {
            int[] rgb={color.Color.R,color.Color.G,color.Color.B},rgb2={color2.Color.R,color2.Color.G,color2.Color.B};
            value=Choice(option) switch {
                "static" or "breathing"=>new {effect=Choice(option),color=rgb},
                "breathing_dual"=>new {effect="breathing_dual",color=rgb,color2=rgb2},
                "wave"=>new {effect="wave",direction=Choice(direction)},
                "reactive"=>new {effect="reactive",color=rgb,speed=int.Parse(Choice(duration),CultureInfo.InvariantCulture)},
                _=>new {effect=Choice(option)} };
        }else value=Choice(option);
        return new {protocol=Choice(protocol),write=writing,operation=new {kind,value}};
    }
    async Task Test(bool writing) {
        if(!active||working)return;
        try {await Run("diagnostic_test",Experiment(writing));}catch(Exception e){feedback.Text=e.Message;}
    }
    async Task Run(string action,object? value) {
        if(working)return;
        working=true;Availability();
        try { await send(action,value); }
        catch(Exception e) {feedback.Text=e.Message;}
        finally {working=false;syncing=true;mode.IsOn=active;syncing=false;Availability();}
    }
    void Availability() {
        mode.IsEnabled=!working&&(active||available);
        controlHost.IsEnabled=active&&!working;controlHost.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        write.IsEnabled=active&&!working;read.IsEnabled=active&&!working&&Choice(function)!="effect";
        bool recorded=report.ValueKind==JsonValueKind.Object;
        reportPanel.Visibility=recorded?Visibility.Visible:Visibility.Collapsed;
        save.IsEnabled=copy.IsEnabled=recorded&&!working;open.IsEnabled=recorded&&!working&&repository is not null;
    }
    public void Update(JsonElement state) {
        syncing=true;
        try {
            active=state.ValueKind==JsonValueKind.Object&&state.TryGetProperty("diagnostic",out var summary)&&summary.TryGetProperty("active",out var a)&&a.ValueKind==JsonValueKind.True;
            available=state.ValueKind==JsonValueKind.Object&&state.TryGetProperty("diagnostic_available",out var can)&&can.ValueKind==JsonValueKind.True;
            mode.IsOn=active;
            if(state.ValueKind==JsonValueKind.Object&&state.TryGetProperty("diagnostic_report",out var current)&&current.TryGetProperty("started_ms",out var started)&&started.TryGetInt64(out var id)&&id!=0) {
                if(session!=id) {observation.Text="";session=id;}
                report=current.Clone();result.Text=FormatLatest(report);
                feedback.Text=active?L.T("Recording diagnostic experiments. Starting another session replaces this log; save it first."):L.T("Diagnostic session ended. You can save the log and attach it to an issue.");
            }
            // A controller restart loses its in-memory session, but keep the reviewed UI copy for export.
            if(report.ValueKind==JsonValueKind.Object&&!active&&report.TryGetProperty("active",out var old)&&old.ValueKind==JsonValueKind.True)
                feedback.Text=L.T("Controller disconnected or restarted. The last received session log is still available to save.");
        }finally {syncing=false;Availability();}
    }
    async Task Save() {
        try {
            string text=ReportText;
            var picker=new FileSavePicker { SuggestedFileName="r-helper-compact-session-"+session.ToString(CultureInfo.InvariantCulture) };
            picker.FileTypeChoices.Add("Text log",new[]{".txt"});WinRT.Interop.InitializeWithWindow.Initialize(picker,WindowHandle);
            var file=await picker.PickSaveFileAsync();if(file is null)return;
            await Windows.Storage.FileIO.WriteTextAsync(file,text);feedback.Text=L.T("Report saved. Review the text file and attach it yourself.");
        }catch(Exception){feedback.Text=L.T("Could not save the report. You can copy the request instead.");}
    }
    internal static string IssueUrl(string repository,double issueNumber)=>double.IsFinite(issueNumber)&&issueNumber>=1&&issueNumber<=int.MaxValue&&issueNumber==Math.Truncate(issueNumber)
        ?repository+"/issues/"+((int)issueNumber).ToString(CultureInfo.InvariantCulture):repository+"/issues/new?template=device_support.md";
    static string FormatLatest(JsonElement report) {
        if(!report.TryGetProperty("records",out var records)||records.ValueKind!=JsonValueKind.Array||records.GetArrayLength()==0)
            return L.T("No experiments recorded yet.");
        var last=records.EnumerateArray().Last();
        string Reading(string key) {
            var reading=last.GetProperty(key);
            return reading.TryGetProperty("value",out var value)&&value.ValueKind!=JsonValueKind.Null?value.ToString():L.T("Unavailable")+" ("+reading.GetProperty("error").ToString()+")";
        }
        var b=new StringBuilder();
        b.Append(L.T("Before write")).Append(": ").Append(Reading("before")).Append('\n');
        b.Append(L.T("Write result")).Append(": ").Append(last.GetProperty("write_result").ToString());
        if(last.TryGetProperty("write_error",out var error)&&error.ValueKind==JsonValueKind.String)b.Append(" (").Append(error.GetString()).Append(')');
        b.Append('\n').Append(L.T("After write")).Append(": ").Append(Reading("after")).Append('\n');
        b.Append("#").Append(last.GetProperty("sequence").ToString()).Append(" · ").Append(last.GetProperty("experiment").ToString()).Append('\n');
        foreach(var exchange in last.GetProperty("exchanges").EnumerateArray()) {
            b.Append('+').Append(exchange.GetProperty("elapsed_ms").ToString()).Append(" ms · ").Append(exchange.GetProperty("phase").GetString()).Append(" · ").Append(exchange.GetProperty("event").GetString());
            if(exchange.TryGetProperty("status",out var status)&&status.ValueKind==JsonValueKind.Number&&status.TryGetByte(out var code))b.Append(" · status=0x").Append(code.ToString("X2",CultureInfo.InvariantCulture));
            b.Append('\n').Append(exchange.GetProperty("hex").GetString()).Append('\n');
        }
        b.Append("Dropped experiments: ").Append(report.GetProperty("dropped_records").ToString()).Append("; dropped packets: ").Append(last.GetProperty("dropped_exchanges").ToString());
        return b.ToString();
    }
    static string FormatReport(JsonElement report) {
        if(report.ValueKind!=JsonValueKind.Object)return "";
        // Dedicated structured capture only. Never import stderr, controller.log or process settings.
        var b=new StringBuilder("R-Helper Compact diagnostic session\nAn acknowledged command or matching readback is not hardware verification.\nNo automatic restore is performed.\n");
        b.Append("Windows: ").Append(Environment.OSVersion.Version).Append("\nUI application: ").Append(Assembly.GetExecutingAssembly().GetName().Version).Append('\n');
        b.Append(JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));
        return b.ToString();
    }
    internal void PreviewSmoke(bool effect) {
        function.SelectedIndex=effect?10:2;protocol.SelectedIndex=effect?2:0;
        if(effect)option.SelectedIndex=4;else number.Value=4300;
        observation.Text="Synthetic observation: verify the physical behavior before sharing.";
    }
    internal static async Task SmokeTest() {
        DiagnosticPanel panel=null!;
        int sends=0;
        TaskCompletionSource? pending=new();
        panel=new DiagnosticPanel(async(action,value)=> {
            if(action!="diagnostic_mode")throw new InvalidOperationException("Unexpected synthetic command");
            sends++;
            if(pending is not null)await pending.Task;
            using var reply=JsonDocument.Parse(JsonSerializer.Serialize(new {diagnostic_available=true,diagnostic=new {active=(bool)value!}}));
            panel.Update(reply.RootElement);
        });
        using var ready=JsonDocument.Parse("""{"diagnostic_available":true,"diagnostic":{"active":false}}""");
        panel.Update(ready.RootElement);
        if(!panel.mode.IsEnabled||panel.controlHost.IsEnabled||panel.save.IsEnabled)throw new InvalidOperationException("Diagnostic inactive availability failed");
        var starting=panel.Run("diagnostic_mode",true);
        if(panel.mode.IsEnabled||panel.controlHost.IsEnabled||panel.write.IsEnabled)throw new InvalidOperationException("Diagnostic command availability failed");
        pending.SetResult();pending=null;await starting;
        if(!panel.Active||!panel.mode.IsOn||!panel.controlHost.IsEnabled||!panel.write.IsEnabled)throw new InvalidOperationException("Diagnostic start failed");
        using var on=JsonDocument.Parse("""{"diagnostic_available":true,"diagnostic":{"active":true},"diagnostic_report":{"started_ms":1,"active":true,"records":[]}}""");
        panel.Update(on.RootElement);
        panel.color.Color=Windows.UI.Color.FromArgb(255,18,52,86);
        panel.color2.Color=Windows.UI.Color.FromArgb(255,171,205,239);
        panel.direction.SelectedIndex=0;panel.duration.SelectedIndex=3;
        for(int f=0;f<panel.function.Items.Count;f++) {
            panel.function.SelectedIndex=f;
            for(int p=0;p<panel.protocol.Items.Count;p++) {
                if(!((ComboBoxItem)panel.protocol.Items[p]).IsEnabled)continue;
                panel.protocol.SelectedIndex=p;
                if(panel.option.Items.Count==0) {
                    using var numeric=JsonDocument.Parse(JsonSerializer.Serialize(panel.Experiment(true)));
                    if(numeric.RootElement.GetProperty("operation").GetProperty("value").ValueKind!=JsonValueKind.Number)throw new InvalidOperationException("Diagnostic numeric value failed");
                }
                for(int v=0;v<panel.option.Items.Count;v++) {
                    panel.option.SelectedIndex=v;
                    string expected=JsonSerializer.Serialize(panel.Experiment(true));
                    panel.observation.Text="User observation";panel.Update(on.RootElement);
                    if(expected!=JsonSerializer.Serialize(panel.Experiment(true))||panel.observation.Text!="User observation")throw new InvalidOperationException("Diagnostic refresh overwrote selections");
                    if(Choice(panel.function)=="effect"&&panel.read.IsEnabled)throw new InvalidOperationException("Effect readback enabled");
                }
            }
        }
        panel.function.SelectedIndex=2;panel.number.Value=4300;panel.Update(on.RootElement);
        if(panel.number.Value!=4300)throw new InvalidOperationException("Diagnostic polling overwrote RPM");
        panel.number.Value=4350;
        try {panel.Experiment(true);throw new InvalidOperationException("Invalid diagnostic RPM accepted");}catch(InvalidOperationException e) when(e.Message==L.T("Enter an integer within the displayed range. RPM uses steps of 100.")) { }
        using var reading=JsonDocument.Parse("""{"started_ms":1,"active":true,"dropped_records":0,"records":[{"sequence":1,"experiment":{"protocol":"Legacy4"},"before":{"value":null,"error":"device_read_failed"},"write_result":"failed","write_error":"device_write_failed","after":{"value":99,"error":null},"dropped_exchanges":0,"exchanges":[{"elapsed_ms":1,"phase":"write","event":"read_error","status":null,"hex":""}]}]}""");
        using var recorded=JsonDocument.Parse(JsonSerializer.Serialize(new {diagnostic_available=true,diagnostic=new {active=true},diagnostic_report=reading.RootElement}));
        panel.Update(recorded.RootElement);
        if(!panel.result.Text.Contains(L.T("Unavailable"))||!panel.result.Text.Contains("device_read_failed")||!panel.result.Text.Contains("99")||!panel.result.Text.Contains("device_write_failed"))throw new InvalidOperationException("Diagnostic partial writes or unavailable reads lost");
        using var blocked=JsonDocument.Parse(reading.RootElement.GetRawText().Replace("device_write_failed","windows_session_locked").Replace("\"failed\"","\"blocked\""));
        if(!FormatLatest(blocked.RootElement).Contains("windows_session_locked")||!FormatLatest(blocked.RootElement).Contains("blocked"))throw new InvalidOperationException("Blocked diagnostic result lost");
        string saved=panel.ReportText;
        using var disconnected=JsonDocument.Parse("""{"diagnostic":{"active":true},"diagnostic_available":false}""");panel.Update(disconnected.RootElement);
        if(!panel.mode.IsEnabled||panel.ReportText!=saved)throw new InvalidOperationException("Cannot end or export after device disconnect");
        await panel.Run("diagnostic_mode",false);
        if(panel.Active||panel.mode.IsOn||panel.controlHost.IsEnabled||!panel.save.IsEnabled||sends!=2)throw new InvalidOperationException("Diagnostic end after disconnect failed");
        panel.Update(default);
        if(panel.ReportText!=saved||!panel.save.IsEnabled||!panel.copy.IsEnabled||!panel.open.IsEnabled)throw new InvalidOperationException("Controller loss discarded last report");
        string path=Path.Combine(AppContext.BaseDirectory,"synthetic-diagnostic-session.txt");
        File.WriteAllText(path,panel.ReportText);
        if(File.ReadAllText(path)!=saved)throw new InvalidOperationException("Session export round trip failed");
        const string repo="https://github.com/e-khurmamatov/r-helper-compact";
        if(IssueUrl(repo,21)!=repo+"/issues/21"||IssueUrl(repo,double.NaN)!=repo+"/issues/new?template=device_support.md")throw new InvalidOperationException("Diagnostic issue URL or privacy failed");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"smoke-checks.txt"),"\nPASS: diagnostic start/end, all functions/protocols/values/effects, refresh preservation, unavailable/blocked/partial results, retained log after device/controller loss, export round trip, new issue and issue #21 URLs without diagnostic data. Synthetic transport only; file picker and browser not launched.");
    }
}
