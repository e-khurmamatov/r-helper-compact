using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Forms = System.Windows.Forms;

namespace RHelper.Compact;
internal sealed class MainWindow : Window
{
    readonly Grid shell=new();
    readonly DeviceIssuePanel unsupportedPanel=new() { Visibility=Visibility.Collapsed };
    readonly ContentControl applicationHost=new() { HorizontalContentAlignment=HorizontalAlignment.Stretch };
    readonly UpdatePanel updates;
    bool Supported=>S("support_status") is "supported" or "experimental";
    readonly ScrollViewer scroll=new() { HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    readonly Forms.NativeWindow trayOwner=new();
    readonly StackPanel content=new() { Spacing=8, Padding=new Thickness(12) };
    readonly TextBlock status=new() { Text=L.T("Connecting to controller…"), TextWrapping=TextWrapping.Wrap };
    readonly System.Collections.Generic.HashSet<Slider> dirtySliders=new();
    readonly TextBlock fanReading=new() { Text=L.T("Actual speed: — RPM") };
    readonly TextBlock telemetry=new() { FontSize=15, TextWrapping=TextWrapping.Wrap };
    readonly TemperatureChart temperatureChart=new();
    readonly TextBlock readingAge=new() {FontSize=11,Opacity=.65,TextWrapping=TextWrapping.Wrap};
    bool readingsConnected=true;
    long lastReplyTicks=Stopwatch.GetTimestamp();
    readonly TextBlock model=new() { Opacity=.7 };
    readonly InfoBar errors=new() { IsClosable=true, Severity=InfoBarSeverity.Error };
    readonly SectionOrganizer organizer;
    readonly List<ContentControl> hardwareSections=new();
    readonly TextBlock padStatus=new() { Text=L.T("Checking connection…"),Opacity=.65,FontSize=12,VerticalAlignment=VerticalAlignment.Center };
    readonly Expander padSection=new() { HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch };
    readonly System.Drawing.Icon appIcon;
    readonly ContentControl hardwareHost=new() { IsEnabled=false, HorizontalContentAlignment=HorizontalAlignment.Stretch };
    readonly ContentControl padHost=new() { IsEnabled=false, HorizontalContentAlignment=HorizontalAlignment.Stretch };
    readonly ComboBox padMode=new(),padLight=new();
    readonly ToggleSwitch padFollow=new();
    readonly Slider padBrightness=new() { Minimum=0,Maximum=15,StepFrequency=1 };
    readonly Button padEffectButton;
    readonly Dictionary<string,LatestSliderCommand> padCommands=new();
    readonly HashSet<string> dirtyPadNumbers=new();
    readonly LatestSliderCommand padBrightnessCommand;
    bool padEffectDirty;
    readonly Button applyCap;
    readonly LatestSliderCommand rpmCommand,brightnessCommand;
    readonly Button reconnect;
    readonly ComboBox lightingOwner=new() { Header=L.T("Lighting control"),ItemsSource=new[]{"R-Helper Compact","OpenRGB"},SelectedIndex=0,IsEnabled=false };
    readonly ComboBox keyboardEffect=new() { Header=L.T("Standard effect"),ItemsSource=new[]{L.T("Off"),L.T("Solid color"),L.T("Wave"),L.T("Breathing"),L.T("Spectrum"),L.T("Reactive"),L.T("Starlight"),L.T("Two-color breathing")},SelectedIndex=1 };
    readonly ComboBox waveDirection=new() { Header=L.T("Wave direction"),ItemsSource=new[]{L.T("Left"),L.T("Right")},SelectedIndex=1 };
    readonly ColorPicker keyboardColor=new() { IsAlphaEnabled=false,IsMoreButtonVisible=false,Color=Windows.UI.Color.FromArgb(255,0,255,0) };
    readonly Expander keyboardColorSection=new() { Header=L.T("Color"),HorizontalAlignment=HorizontalAlignment.Stretch };
    readonly ColorPicker keyboardColor2=new() { IsAlphaEnabled=false,IsMoreButtonVisible=false,Color=Windows.UI.Color.FromArgb(255,0,128,255) };
    readonly Expander keyboardColor2Section=new() { HorizontalAlignment=HorizontalAlignment.Stretch };
    readonly ComboBox reactiveDuration=new() { Header=L.T("Fade duration"),ItemsSource=new[]{"1","2","3","4"},SelectedIndex=1 };
    readonly TextBlock starlightHint=new() { Text=L.T("Starlight uses a fixed green effect on this model."),TextWrapping=TextWrapping.Wrap,Opacity=.7 };
    readonly ContentControl lightingHost=new() { IsEnabled=false,HorizontalContentAlignment=HorizontalAlignment.Stretch };
    readonly TextBlock lightingHint=new() { TextWrapping=TextWrapping.Wrap,Opacity=.7 };
    readonly Button applyEffect;
    bool padColorInitialized;
    readonly ColorPicker padColor=new() { IsAlphaEnabled=false,IsMoreButtonVisible=false };
    readonly System.Collections.Generic.Dictionary<string,NumberBox> padNumbers=new();
    readonly ComboBox perf=new(),cpu=new(),gpu=new(),fan=new(),logo=new(),battery=new();
    readonly Slider rpm=new() { Minimum=2000,Maximum=5500,StepFrequency=100,Value=4000 };
    readonly Slider brightness=new() { Minimum=0,Maximum=15,StepFrequency=1 };
    readonly Slider capRpm=new() { Minimum=2000,Maximum=5500,StepFrequency=100,Value=4000 };
    readonly ToggleSwitch lights=new(),startup=new(),profiles=new(),cap=new();
    readonly TextBlock profileInfo=new(),info=new() { TextWrapping=TextWrapping.Wrap };
    readonly Forms.NotifyIcon tray;
    readonly Controller? controller;
    readonly DispatcherTimer timer=new() { Interval=TimeSpan.FromSeconds(1) };
    readonly nint hwnd;
    bool syncing=true, busy, quitting,preparingUpdate;
    string lastConnectionError="";
    int focusEpoch;
    readonly System.Threading.SemaphoreSlim requestGate=new(1);
    JsonElement state;
    readonly bool smoke;
    readonly string themePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"r-helper-compact","ui.json");
    public MainWindow(bool smoke)
    {
        this.smoke=smoke;
        organizer=new SectionOrganizer(smoke?null:Path.Combine(Path.GetDirectoryName(themePath)!,"sections.json"),CancelSliderEdits,e=>ShowError(L.T("Could not save section order: ")+e.Message));
        padBrightnessCommand=new((value,valid)=>Send("pad_brightness",value,valid),()=>!quitting&&Supported&&B("pad_connected")&&B("pad_lighting"));
        rpmCommand=new((value,valid)=>Send("fan_rpm",value,valid),()=>!quitting&&B("ready")&&Choice(fan)=="Manual");
        brightnessCommand=new((value,valid)=>Send("brightness",value,valid),()=>!quitting&&B("ready")&&!B("lighting_external")&&lightingOwner.SelectedIndex==0);
        Title="R-Helper Compact";
        hwnd=WinRT.Interop.WindowNative.GetWindowHandle(this);
        unsupportedPanel.WindowHandle=hwnd;
        appIcon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        SendMessage(hwnd,0x80,0,appIcon.Handle);SendMessage(hwnd,0x80,1,appIcon.Handle);
        var presenter=(OverlappedPresenter)AppWindow.Presenter;
        presenter.SetBorderAndTitleBar(true,true);
        presenter.IsResizable=false; presenter.IsMaximizable=false; presenter.IsMinimizable=false;
        // A hidden owner keeps the panel out of the taskbar while retaining the normal
        // Windows caption and close button (WS_EX_TOOLWINDOW uses a miniature caption).
        trayOwner.CreateHandle(new Forms.CreateParams { Caption="R-Helper Compact tray owner",Style=unchecked((int)0x80000000),ExStyle=0x80 });
        SetWindowLongPtr(hwnd,-8,trayOwner.Handle);
        var exStyle=GetWindowLongPtr(hwnd,-20).ToInt64() & ~0x40080L;
        SetWindowLongPtr(hwnd,-20,new nint(exStyle));
        // Let Windows draw the caption, shadow and rounded corners.
        var style=GetWindowLongPtr(hwnd,-16).ToInt64() & ~0x00030000L;
        SetWindowLongPtr(hwnd,-16,new nint(style));
        SetWindowPos(hwnd,0,0,0,0,0,0x37); // Refresh cached non-client styles without moving/activating.
        AppWindow.MoveAndResize(new RectInt32(-32000,-32000,500,760));
        HidePopup();
        shell.ActualThemeChanged+=(_,_)=>ApplyBackground();
        content.Children.Add(model);content.Children.Add(unsupportedPanel);content.Children.Add(telemetry);content.Children.Add(readingAge);content.Children.Add(temperatureChart);content.Children.Add(errors);
        reconnect=ActionButton(L.T("Reconnect"),async()=> { CancelSliderEdits();await Send("reconnect"); });content.Children.Add(reconnect);
        content.Children.Add(organizer);
        perf.PlaceholderText=L.T("Mode unavailable");cpu.Visibility=gpu.Visibility=Visibility.Collapsed;
        perf.Header=L.T("Mode");cpu.Header="CPU · Custom";gpu.Header="GPU · Custom";
        AddHardware("performance",L.T("Performance"),Card(L.T("Performance"),perf,cpu,gpu));
        perf.SelectionChanged+=async(_,_)=>await Changed("performance",Choice(perf));
        cpu.SelectionChanged+=async(_,_)=>await Changed("cpu",Choice(cpu));
        gpu.SelectionChanged+=async(_,_)=>await Changed("gpu",Choice(gpu));
        fan.Header=L.T("Fan control");AddChoice(fan,"Auto");AddChoice(fan,"Manual");
        fan.SelectionChanged+=async(_,_)=> { if(!syncing && Choice(fan) is string s) { rpmCommand.Cancel();dirtySliders.Remove(rpm);await Send(s=="Auto"?"fan_auto":"fan_rpm",(int)rpm.Value); } };
        rpm.Header=L.T("Speed, RPM");
        cap.Header=L.T("Software RPM limit in Auto");cap.Toggled+=async(_,_)=>await Changed("cap",cap.IsOn);
        capRpm.Header=L.T("Limit, RPM");
        applyCap=ActionButton(L.T("Save limit"),async()=>await Send("cap_rpm",(int)capRpm.Value));
        AddHardware("cooling",L.T("Cooling"),Card(L.T("Cooling"),fan,fanReading,rpm,new Expander { Header=L.T("Advanced"),Content=Stack(cap,capRpm,applyCap) }));
        brightness.Header=L.T("Keyboard brightness");
        logo.Header=L.T("Logo");logo.PlaceholderText=L.T("Not read"); foreach(var value in new[]{"Off","Static","Breathing"})AddChoice(logo,value);
        logo.SelectionChanged+=async(_,_)=>await Changed("logo",Choice(logo));
        lights.Header=L.T("Keep backlight on while idle");lights.Toggled+=async(_,_)=>await Changed("lights",lights.IsOn);
        lightingOwner.SelectionChanged+=async(_,_)=> { if(!syncing) { brightnessCommand.Cancel();dirtySliders.Remove(brightness);await Send("lighting_external",lightingOwner.SelectedIndex==1); } };
        applyEffect=ActionButton(L.T("Apply effect"),async()=> {
            object command=KeyboardEffectCommand();
            await Send("keyboard_effect",command);
        });
        keyboardColorSection.Content=keyboardColor;keyboardColorSection.Header=ColorPreview(keyboardColor);
        keyboardColor2Section.Content=keyboardColor2;
        var secondColorHeader=ColorPreview(keyboardColor2);
        secondColorHeader.Children.Add(new TextBlock { Text=L.T("Second color"),FontSize=12,Opacity=.65,VerticalAlignment=VerticalAlignment.Center });
        keyboardColor2Section.Header=secondColorHeader;
        void EffectOptions() {
            keyboardColorSection.Visibility=keyboardEffect.SelectedIndex is 1 or 3 or 5 or 7?Visibility.Visible:Visibility.Collapsed;
            keyboardColor2Section.Visibility=keyboardEffect.SelectedIndex==7?Visibility.Visible:Visibility.Collapsed;
            reactiveDuration.Visibility=keyboardEffect.SelectedIndex==5?Visibility.Visible:Visibility.Collapsed;
            starlightHint.Visibility=keyboardEffect.SelectedIndex==6?Visibility.Visible:Visibility.Collapsed;
            waveDirection.Visibility=keyboardEffect.SelectedIndex==2?Visibility.Visible:Visibility.Collapsed;
        }
        keyboardEffect.SelectionChanged+=(_,_)=>EffectOptions();EffectOptions();
        lightingHost.Content=Stack(brightness,Pair(keyboardEffect,logo),
            keyboardColorSection,keyboardColor2Section,waveDirection,reactiveDuration,starlightHint,applyEffect,
            Fold(L.T("Lighting behavior"),lights,new TextBlock { Text=L.T("Enables keyboard driver mode for persistent lighting. It may affect Fn keys depending on firmware. Brightness is unchanged. Disable this if Fn keys stop working."),TextWrapping=TextWrapping.Wrap,Opacity=.7 }));
        organizer.Add("lighting",L.T("Lighting"),Card(L.T("Lighting"),lightingOwner,lightingHint,lightingHost));
        battery.Header=L.T("Charge limit");foreach(var value in new[]{"Disable","Percent50","Percent55","Percent60","Percent65","Percent70","Percent75","Percent80"}) AddChoice(battery,value);
        battery.SelectionChanged+=async(_,_)=>await Changed("battery",Choice(battery));
        AddHardware("battery",L.T("Battery and charging"),Fold(L.T("Battery and charging"),battery,new TextBlock { Text=L.T("Off allows charging to 100%. Support depends on the model; the selected limit does not confirm that charging has stopped."),TextWrapping=TextWrapping.Wrap,Opacity=.65 }));
        profiles.Header=L.T("Switch profiles automatically on AC / battery");profiles.Toggled+=async(_,_)=>await Changed("auto_profiles",profiles.IsOn);
        AddHardware("profiles",L.T("AC and battery profiles"),Fold(L.T("AC and battery profiles"),profileInfo,profiles,ActionButton(L.T("Save current settings for AC"),async()=>await Send("save_ac")),ActionButton(L.T("Save current settings for battery"),async()=>await Send("save_battery"))));
        padMode.Header=L.T("Fan");padMode.HorizontalAlignment=HorizontalAlignment.Stretch; foreach(var v in new[]{"off","manual","auto"})AddChoice(padMode,v);
        padMode.SelectionChanged+=async(_,_)=> { if(!syncing) { foreach(var c in padCommands.Values)c.Cancel();dirtyPadNumbers.Clear();await Changed("pad_mode",Choice(padMode)); } };
        padFollow.Header=L.T("Follow laptop fans");padFollow.Toggled+=async(_,_)=>await Changed("pad_follow",padFollow.IsOn);
        var padSettings=Stack(padMode,padFollow);
        foreach(var setting in new[]{("pad_manual",L.T("Manual RPM"),500,3200),("pad_min",L.T("Minimum RPM"),500,3200),("pad_max",L.T("Maximum RPM"),500,3200),("pad_off",L.T("Turn off below °C"),30,85),("pad_full",L.T("Full speed at °C"),35,100)}) {
            string key=setting.Item1;
            var box=new NumberBox { Header=setting.Item2,Minimum=setting.Item3,Maximum=setting.Item4,Value=setting.Item3,SmallChange=key.Contains("rpm")||key is "pad_manual" or "pad_min" or "pad_max"?100:1,SpinButtonPlacementMode=NumberBoxSpinButtonPlacementMode.Compact };
            padNumbers.Add(key,box);
            var command=new LatestSliderCommand((value,valid)=>Send(key,value,valid),()=>!quitting&&Supported&&B("pad_connected"),()=>Task.Delay(300));
            padCommands.Add(key,command);
            box.ValueChanged+=async(_,_)=> {
                if(syncing||!padHost.IsEnabled)return;
                dirtyPadNumbers.Add(key);
                if(!double.IsFinite(box.Value)||box.Value!=Math.Truncate(box.Value)) { command.Cancel();return; }
                await command.Change((int)box.Value);
            };
            box.LostFocus+=(_,_)=> { if(syncing)return;if(!double.IsFinite(box.Value)||box.Value!=Math.Truncate(box.Value)) { command.Cancel();dirtyPadNumbers.Remove(key);syncing=true;box.Value=N(key,box.Minimum);syncing=false; } };
        }
        padSettings.Children.Add(padNumbers["pad_manual"]);
        padSettings.Children.Add(Fold(L.T("Automatic cooling"),Pair(padNumbers["pad_min"],padNumbers["pad_max"]),Pair(padNumbers["pad_off"],padNumbers["pad_full"])));
        padLight.Header=L.T("Standard effect");foreach(var v in new[]{"Off","Static","Breathing"})AddChoice(padLight,v);
        var padColorSection=new Expander { Header=ColorPreview(padColor),Content=padColor,HorizontalAlignment=HorizontalAlignment.Stretch };
        padLight.SelectionChanged+=(_,_)=> { if(!syncing)padEffectDirty=true;padColorSection.Visibility=Choice(padLight)=="Off"?Visibility.Collapsed:Visibility.Visible; };
        padColor.ColorChanged+=(_,_)=> { if(!syncing)padEffectDirty=true; };
        padBrightness.Header=L.T("Pad brightness");
        padEffectButton=ActionButton(L.T("Apply effect"),async()=>await Send("pad_effect",new {mode=Choice(padLight)??"Off",color=new int[]{padColor.Color.R,padColor.Color.G,padColor.Color.B}}));
        padSettings.Children.Add(Card(L.T("Lighting"),padBrightness,padLight,padColorSection,padEffectButton));
        padHost.Content=padSettings;
        var padHeader=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
        padHeader.Children.Add(new TextBlock { Text="Cooling Pad",VerticalAlignment=VerticalAlignment.Center });padHeader.Children.Add(padStatus);
        padSection.Header=padHeader;padSection.Content=padHost;organizer.Add("pad","Cooling Pad",padSection);
        startup.Header=L.T("Run at Windows sign-in");startup.Toggled+=async(_,_)=>await Changed("startup",startup.IsOn);
        var theme=new ComboBox { Header=L.T("Theme"),ItemsSource=new[]{L.T("System"),L.T("Light"),L.T("Dark")},SelectedIndex=0 };
        try { using var saved=JsonDocument.Parse(File.ReadAllText(themePath)); theme.SelectedIndex=saved.RootElement.GetProperty("dark").GetBoolean()?2:1; } catch(Exception e) when(e is IOException or JsonException or InvalidOperationException or KeyNotFoundException) { }
        shell.RequestedTheme=theme.SelectedIndex==2?ElementTheme.Dark:theme.SelectedIndex==1?ElementTheme.Light:ElementTheme.Default;
        theme.SelectionChanged+=(_,_)=> { shell.RequestedTheme=theme.SelectedIndex==2?ElementTheme.Dark:theme.SelectedIndex==1?ElementTheme.Light:ElementTheme.Default; try {Directory.CreateDirectory(Path.GetDirectoryName(themePath)!);if(theme.SelectedIndex==0)File.Delete(themePath);else File.WriteAllText(themePath,JsonSerializer.Serialize(new{dark=theme.SelectedIndex==2}));}catch(Exception e){ShowError(e.Message);} };
        updates=new UpdatePanel(smoke,async(package,hash,token)=> {
            preparingUpdate=true;CancelSliderEdits();timer.Stop();reconnect.IsEnabled=false;
            try {
                await requestGate.WaitAsync(token);
                try {
                    hardwareHost.IsEnabled=padHost.IsEnabled=lightingHost.IsEnabled=lightingOwner.IsEnabled=startup.IsEnabled=false;
                    await UpdateInstaller.Launch(package,hash,controller?.Identity,token);
                    Quit();
                }finally {requestGate.Release();}
            }finally {
                preparingUpdate=false;
                if(!quitting) {reconnect.IsEnabled=true;Apply();if(AppWindow.IsVisible)timer.Start();}
            }
        });
        startup.IsEnabled=false;
        var languageCodes=new[]{""}.Concat(LanguagePreference.Available).ToArray();
        var language=new ComboBox { Header=L.T("Language"),ItemsSource=languageCodes.Select(code=>code==""?L.T("System"):System.Globalization.CultureInfo.GetCultureInfo(code).NativeName).ToArray(),SelectedIndex=Array.IndexOf(languageCodes,smoke?"":LanguagePreference.Read(LanguagePreference.SettingsPath)) };
        var languageHint=new TextBlock { Text=L.T("Language changes take effect after restarting the application."),TextWrapping=TextWrapping.Wrap,Opacity=.7 };
        int savedLanguage=language.SelectedIndex;
        language.SelectionChanged+=(_,_)=> {
            if(language.SelectedIndex<0||language.SelectedIndex==savedLanguage)return;
            try { if(!smoke)LanguagePreference.Save(LanguagePreference.SettingsPath,languageCodes[language.SelectedIndex]);savedLanguage=language.SelectedIndex; }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) { language.SelectedIndex=savedLanguage;ShowError(e.Message); }
        };
        applicationHost.Content=Card(L.T("Application"),updates,Fold(L.T("Preferences"),startup,theme,language,languageHint));organizer.Add("app",L.T("Application"),applicationHost);
        organizer.Add("about",L.T("About device"),new Expander { Header=L.T("About device"),Content=info,HorizontalAlignment=HorizontalAlignment.Stretch });
        organizer.Initialize(new[]{"performance","cooling","battery","profiles","lighting","pad","app","about"});
        content.Children.Add(status);
        content.Children.Add(ActionButton(L.T("Quit application"),()=> { Quit(); return Task.CompletedTask; }));
        scroll.Content=content;shell.Children.Add(scroll);shell.Language=L.Language;Content=shell;
        tray=new Forms.NotifyIcon { Text="R-Helper Compact",Icon=appIcon,Visible=!smoke };
        tray.MouseClick+=(_,e)=>{ if(e.Button==Forms.MouseButtons.Left)DispatcherQueue.TryEnqueue(ShowPopup); };
        tray.ContextMenuStrip=new Forms.ContextMenuStrip();
        tray.ContextMenuStrip.Opening+=async(_,_)=>{ BuildMenu();await Send("snapshot");BuildMenu(); };
        Activated+=async(_,e)=> {
            ApplyBackground();
            var epoch=++focusEpoch;
            if(smoke || e.WindowActivationState!=WindowActivationState.Deactivated)return;
            do {
                await Task.Delay(120);
                if(epoch!=focusEpoch || quitting)return;
                if(organizer.IsDragging)continue;
                GetWindowThreadProcessId(GetForegroundWindow(),out var pid);
                if(pid!=(uint)Environment.ProcessId) { HidePopup();return; }
            } while(AppWindow.IsVisible);
        };
        AppWindow.Closing+=(_,e)=>{ if(!quitting) { e.Cancel=true;HidePopup(); } };
        content.KeyDown+=(_,e)=>{ if(e.Key==Windows.System.VirtualKey.Escape) { if(organizer.Editing)organizer.Cancel();else HidePopup(); } };
        if(!smoke) { try {controller=new Controller();}catch(Exception e){ShowError(e.Message);} }
        else { status.Text=L.T("WinUI 3 test · controller not started"); model.Text=L.T("Device not connected");telemetry.Text="CPU —    GPU —    FAN —"; }
        LabelSlider(rpm,L.T("Selected"),v=>$"{v:0} RPM");
        LabelSlider(capRpm,L.T("Limit"),v=>$"{v:0} RPM");
        LabelSlider(brightness,L.T("Keyboard brightness"),v=>L.F("{0:0}% · step {1:0}/15",v/15*100,v));
        LabelSlider(padBrightness,L.T("Pad brightness"),v=>L.F("{0:0}% · step {1:0}/15",v/15*100,v));
        rpm.ValueChanged+=async(_,_)=> { if(!syncing && rpm.IsEnabled && hardwareHost.IsEnabled)await rpmCommand.Change((int)rpm.Value); };
        brightness.ValueChanged+=async(_,_)=> { if(!syncing && brightness.IsEnabled && lightingHost.IsEnabled)await brightnessCommand.Change((int)brightness.Value); };
        padBrightness.ValueChanged+=async(_,_)=> { if(!syncing && padBrightness.IsEnabled && padHost.IsEnabled)await padBrightnessCommand.Change((int)padBrightness.Value); };
        ApplyBackground();
        syncing=false;
        if(smoke)CheckSmokeState();
        if(smoke) {
            long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var samples=Enumerable.Range(0,61).Select(i=>new {at_ms=now-120000+i*2000,cpu=(double?)(67-8*i/60.0+Math.Sin(i*.25)),gpu=(double?)(52-5*i/60.0+Math.Cos(i*.2))});
            using var graph=JsonDocument.Parse(JsonSerializer.Serialize(new {temperature_history=samples}));
            temperatureChart.Update(graph.RootElement);
        }
        timer.Tick+=async(_,_)=> { if(smoke) { timer.Stop(); await CaptureSmoke(); Quit(); } else { RefreshReadings();temperatureChart.Draw();if(!busy && AppWindow.IsVisible)await Send("snapshot"); } };
        if(smoke)timer.Start();
        if(!smoke)_=Send("snapshot");
    }
    void AddHardware(string id,string title,UIElement view) {
        var gate=new ContentControl { Content=view,IsEnabled=false,HorizontalContentAlignment=HorizontalAlignment.Stretch };
        gate.SetBinding(ContentControl.IsEnabledProperty,new Microsoft.UI.Xaml.Data.Binding { Source=hardwareHost,Path=new PropertyPath("IsEnabled"),Mode=Microsoft.UI.Xaml.Data.BindingMode.OneWay });
        hardwareSections.Add(gate);organizer.Add(id,title,gate);
    }
    void LabelSlider(Slider slider,string label,Func<double,string> format) {
        void Update() => slider.Header=$"{label}: {format(slider.Value)}";
        slider.ValueChanged+=(_,_)=> { Update();if(!syncing)dirtySliders.Add(slider); };
        Update();
    }
    void CheckSmokeState() {
        DeviceIssuePanel.SmokeTest();
        // Synthetic values only in the explicit controller-free smoke test.
        using var sample=JsonDocument.Parse("""{"support_status":"supported","logo":"Off","model":"UI test · synthetic data","cpu_temp":59.050018310546875,"gpu_temp":49.0,"actual_rpm":3200,"fan_rpm":4000,"fan_auto":false,"mode":"Balanced","modes":["Balanced"],"brightness":89,"ready":false,"pad_connected":false}""");
        state=sample.RootElement.Clone();Apply();
        using(var auto=JsonDocument.Parse("""{"support_status":"supported","fan_auto":true}""")) {
            state=auto.RootElement.Clone();Apply();
            if(fanReading.Text!=L.F("Actual speed: {0} RPM · requested: {1} RPM","—","AUTO"))throw new InvalidOperationException("AUTO RPM label failed");
        }
        var colorTest=new ColorPicker();var preview=ColorPreview(colorTest);
        colorTest.Color=Windows.UI.Color.FromArgb(255,18,52,86);
        if(((TextBlock)preview.Children[1]).Text!=L.F("Color: #{0:X2}{1:X2}{2:X2}",18,52,86) || ((SolidColorBrush)((Border)preview.Children[0]).Background).Color!=colorTest.Color)throw new InvalidOperationException("Selected color preview failed");
        state=sample.RootElement.Clone();Apply();
        if(Temperature("cpu_temp")!=59.05.ToString("F2") || Temperature("gpu_temp")!=49.0.ToString("F2"))throw new InvalidOperationException("Temperature formatting failed");
        if(!rpm.Header.ToString()!.Contains("4000") || !fanReading.Text.Contains("3200"))throw new InvalidOperationException("RPM labels failed");
        rpm.Value=4500;Apply();
        if(rpm.Value!=4500)throw new InvalidOperationException("Polling overwrote pending slider edit");
        if(Choice(logo)!="Off")throw new InvalidOperationException("Logo Off was not selected");
        if((L.Language=="ru" && L.T("Off")=="Off") || (L.Language=="en" && L.T("Off")!="Off"))throw new InvalidOperationException("Embedded language resource failed");
        var missingTranslation="untranslated-smoke-key";
        if(L.T(missingTranslation)!=missingTranslation)throw new InvalidOperationException("Missing translation fallback failed");
        if(hardwareHost.IsEnabled)throw new InvalidOperationException("Unavailable device controls enabled");
        using(var external=JsonDocument.Parse("""{"support_status":"supported","ready":true,"lighting_external":true,"keyboard_effect_supported":true}""")) {
            state=external.RootElement.Clone();Apply();
            if(logo.SelectedItem is not null)throw new InvalidOperationException("Unknown logo retained old value");
            if(lightingHost.IsEnabled || !hardwareHost.IsEnabled || !lightingOwner.IsEnabled || lightingOwner.SelectedIndex!=1)throw new InvalidOperationException("External lighting ownership failed");
        }
        using(var internalLighting=JsonDocument.Parse("""{"support_status":"supported","ready":true,"lighting_external":false,"keyboard_effect_supported":true}""")) {
            state=internalLighting.RootElement.Clone();Apply();
            if(!lightingHost.IsEnabled || !applyEffect.IsEnabled)throw new InvalidOperationException("Internal lighting controls failed");
        }
        keyboardColor.Color=Windows.UI.Color.FromArgb(255,18,52,86);
        keyboardColor2.Color=Windows.UI.Color.FromArgb(255,171,205,239);
        foreach(var (index,effect) in new[]{(5,"reactive"),(6,"starlight"),(7,"breathing_dual")}) {
            keyboardEffect.SelectedIndex=index;
            reactiveDuration.SelectedIndex=3;
            using var command=JsonDocument.Parse(JsonSerializer.Serialize(KeyboardEffectCommand()));
            var value=command.RootElement;
            if(value.GetProperty("effect").GetString()!=effect)throw new InvalidOperationException("Effect selection failed");
            if((keyboardColor2Section.Visibility==Visibility.Visible)!=(index==7) ||
               (reactiveDuration.Visibility==Visibility.Visible)!=(index==5) ||
               (starlightHint.Visibility==Visibility.Visible)!=(index==6) ||
               (keyboardColorSection.Visibility==Visibility.Visible)!=(index!=6))throw new InvalidOperationException("Effect options failed");
            if(index==5 && value.GetProperty("speed").GetInt32()!=4)throw new InvalidOperationException("Reactive duration failed");
            if(index==7 && value.GetProperty("color2")[2].GetInt32()!=239)throw new InvalidOperationException("Second color failed");
            Apply();
            if(keyboardEffect.SelectedIndex!=index || keyboardColor2.Color.B!=239)throw new InvalidOperationException("Polling overwrote effect edit");
        }
        keyboardEffect.SelectedIndex=1;
        using(var unknown=JsonDocument.Parse("""{"support_status":"unsupported","ready":true,"pad_connected":true,"support_report":{"sku":"RZ09-9999","hid":["1532:FFFF"],"reason":"Synthetic unsupported model"}}""")) {
            state=unknown.RootElement.Clone();Apply();
            if(hardwareHost.IsEnabled||padHost.IsEnabled||lightingHost.IsEnabled||lightingOwner.IsEnabled||startup.IsEnabled||!applicationHost.IsEnabled||unsupportedPanel.Visibility!=Visibility.Visible)throw new InvalidOperationException("Unsupported device controls enabled or updates disabled");
        }
        using(var hostile=JsonDocument.Parse("""{"sku":"RZ09-0510-SERIAL_SECRET","hid":["1532:02B7 interface=0 usage=0001:0006","1532:02B7 interface=0 usage=0001:0006 SERIAL_SECRET"],"reason":"C:\\Users\\PRIVATE_SECRET token=SECRET","serial":"SERIAL_SECRET"}""")) {
            unsupportedPanel.Update(hostile.RootElement,"PRIVATE_SECRET");
            if(unsupportedPanel.ReportText.Contains("SECRET")||!unsupportedPanel.ReportText.Contains("RZ09-0510")||!unsupportedPanel.ReportText.Contains("1532:02B7 interface=0 usage=0001:0006"))
                throw new InvalidOperationException("Diagnostic privacy allowlist failed");
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"synthetic-diagnostics.txt"),unsupportedPanel.ReportText);
            if(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"synthetic-diagnostics.txt"))!=unsupportedPanel.ReportText)
                throw new InvalidOperationException("Diagnostic export round trip failed");
        }
        if(DeviceIssuePanel.ValidateRepository("") is not null || DeviceIssuePanel.ValidateRepository("https://example.com/a/b") is not null || DeviceIssuePanel.ValidateRepository("https://github.com/a/b")!="https://github.com/a/b")throw new InvalidOperationException("Issue repository validation failed");
        state=sample.RootElement.Clone();Apply();
        long style=GetWindowLongPtr(hwnd,-16).ToInt64();
        if((style&0x00C00000L)==0 || (style&0x00030000L)!=0)throw new InvalidOperationException("Native caption style failed");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-checks.txt"),"PASS: OpenRGB disables laptop lighting while retaining other controls; internal effects enabled for supported PID; native caption without minimize/maximize; F2 CPU/GPU; selected and actual RPM; pending slider survives polling; unavailable controls disabled. Synthetic data; no hardware controller.");
    }
    object KeyboardEffectCommand() {
        var color=new int[]{keyboardColor.Color.R,keyboardColor.Color.G,keyboardColor.Color.B};
        var color2=new int[]{keyboardColor2.Color.R,keyboardColor2.Color.G,keyboardColor2.Color.B};
        return keyboardEffect.SelectedIndex switch {
            0=>new {effect="off"},1=>new {effect="static",color},
            2=>new {effect="wave",direction=waveDirection.SelectedIndex==0?"left":"right"},
            3=>new {effect="breathing",color},4=>new {effect="spectrum"},
            5=>new {effect="reactive",color,speed=reactiveDuration.SelectedIndex+1},
            6=>new {effect="starlight"},7=>new {effect="breathing_dual",color,color2},
            _=>throw new InvalidOperationException("No keyboard effect selected")
        };
    }
    void ApplyBackground() {
        bool dark=shell.ActualTheme==ElementTheme.Dark;
        var color=dark?Windows.UI.Color.FromArgb(255,32,32,32):Windows.UI.Color.FromArgb(255,243,243,243);
        var brush=new SolidColorBrush(color);
        shell.Background=scroll.Background=content.Background=brush;
        int darkValue=dark?1:0, corners=2, border=unchecked((int)0xFFFFFFFF);
        int caption=color.R | (color.G<<8) | (color.B<<16), text=dark?0x00FFFFFF:0x00000000;
        _=DwmSetWindowAttribute(hwnd,20,ref darkValue,4);
        _=DwmSetWindowAttribute(hwnd,33,ref corners,4);
        _=DwmSetWindowAttribute(hwnd,34,ref border,4);
        _=DwmSetWindowAttribute(hwnd,35,ref caption,4);
        _=DwmSetWindowAttribute(hwnd,36,ref text,4);
    }
    static StackPanel Stack(params UIElement[] children) { var p=new StackPanel { Spacing=6 };foreach(var c in children)p.Children.Add(c);return p; }
    static StackPanel ColorPreview(ColorPicker picker) {
        var swatch=new Border { Width=20,Height=20,CornerRadius=new CornerRadius(4),BorderThickness=new Thickness(1),BorderBrush=new SolidColorBrush(Microsoft.UI.Colors.Gray) };
        var text=new TextBlock { VerticalAlignment=VerticalAlignment.Center };
        void Update() { var c=picker.Color;swatch.Background=new SolidColorBrush(c);text.Text=L.F("Color: #{0:X2}{1:X2}{2:X2}",c.R,c.G,c.B); }
        picker.ColorChanged+=(_,_)=>Update();Update();
        var row=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8 };row.Children.Add(swatch);row.Children.Add(text);return row;
    }
    static Expander Fold(string title,params UIElement[] children)=>new() { Header=title,Content=Stack(children),HorizontalAlignment=HorizontalAlignment.Stretch,HorizontalContentAlignment=HorizontalAlignment.Stretch };
    static Grid Pair(FrameworkElement left,FrameworkElement right) {
        var grid=new Grid { ColumnSpacing=10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(left);grid.Children.Add(right);Grid.SetColumn(right,1);
        if(left is Control a)a.HorizontalAlignment=HorizontalAlignment.Stretch;
        if(right is Control b)b.HorizontalAlignment=HorizontalAlignment.Stretch;
        return grid;
    }
    static Border Card(string title,params UIElement[] children)
    {
        var panel=Stack(new TextBlock { Text=title,FontSize=17,FontWeight=Microsoft.UI.Text.FontWeights.SemiBold });
        foreach(var c in children) { if(c is Control control)control.HorizontalAlignment=HorizontalAlignment.Stretch;panel.Children.Add(c); }
        var border=(Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Background='{ThemeResource CardBackgroundFillColorDefaultBrush}' BorderBrush='{ThemeResource CardStrokeColorDefaultBrush}' BorderThickness='1' CornerRadius='8' Padding='10'/>");
        border.Child=panel; return border;
    }
    Button ActionButton(string label,Func<Task> action) { var b=new Button { Content=label };b.Click+=async(_,_)=>{ await action(); };return b; }
    Task Changed(string action,object? value)=>syncing||value is null?Task.CompletedTask:Send(action,value);
    void CancelSliderEdits() { rpmCommand.Cancel();brightnessCommand.Cancel();padBrightnessCommand.Cancel();foreach(var c in padCommands.Values)c.Cancel();dirtyPadNumbers.Clear();dirtySliders.Remove(rpm);dirtySliders.Remove(brightness);dirtySliders.Remove(padBrightness); }
    async Task Send(string action,object? value=null,Func<bool>? stillCurrent=null)
    {
        if(preparingUpdate)return;
        if(controller is null || quitting || (busy && action=="snapshot"))return;
        await requestGate.WaitAsync();
        if(quitting || preparingUpdate || (stillCurrent is not null && !stillCurrent())) { requestGate.Release();return; }
        busy=true; if(action!="snapshot" && action!="visibility" && stillCurrent is null) { hardwareHost.IsEnabled=false;padHost.IsEnabled=false;lightingHost.IsEnabled=false;lightingOwner.IsEnabled=false;startup.IsEnabled=false; }
        try {
            state=await controller.Send(action,value);
            if(action=="reconnect")state=await controller.Send("visibility",AppWindow.IsVisible);
            lastReplyTicks=Stopwatch.GetTimestamp();readingsConnected=true;
            if(action=="fan_rpm" && (stillCurrent is null || stillCurrent()))dirtySliders.Remove(rpm);
            if(action=="cap_rpm")dirtySliders.Remove(capRpm);
            if(action=="brightness" && (stillCurrent is null || stillCurrent()))dirtySliders.Remove(brightness);
            if(action=="pad_brightness" && (stillCurrent is null || stillCurrent()))dirtySliders.Remove(padBrightness);
            if(padCommands.ContainsKey(action) && (stillCurrent is null || stillCurrent()))dirtyPadNumbers.Remove(action);
            if(action=="pad_effect")padEffectDirty=false;
            Apply();
        }
        catch(Exception e) { readingsConnected=false; readingAge.Text=L.T("Readings unavailable");telemetry.Text="CPU — °C    GPU — °C\nFAN — RPM"; ShowError(e.Message,action!="snapshot" && action!="visibility" && stillCurrent is null); CancelSliderEdits();hardwareHost.IsEnabled=false;lightingHost.IsEnabled=false;lightingOwner.IsEnabled=false; }
        finally { busy=false;startup.IsEnabled=Supported;requestGate.Release(); }
    }
    void ShowError(string message,bool bringToFront=true) { errors.Message=L.T(message);errors.IsOpen=true;if(bringToFront && !smoke && Content is not null)ShowPopup(); }
    string Temperature(string name)=>readingsConnected&&!Stale("thermal_age_ms")&&state.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)&&double.IsFinite(n)?n.ToString("F2",System.Globalization.CultureInfo.CurrentCulture):"—";
    string S(string name)=>state.TryGetProperty(name,out var v)&&v.ValueKind!=JsonValueKind.Null?v.ToString():"—";
    bool B(string name)=>state.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.True;
    double N(string name,double fallback)=>state.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetDouble(out var n)?n:fallback;
    string[] A(string name)=>state.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Array?v.EnumerateArray().Select(x=>x.ToString()).ToArray():Array.Empty<string>();
    static string? Choice(ComboBox box)=>(box.SelectedItem as ComboBoxItem)?.Tag as string;
    static string ChoiceLabel(string value)=>value.StartsWith("Percent",StringComparison.Ordinal)?value[7..]+"%":value=="Disable"?L.T("100% (off)"):L.T(value is "auto"?"Auto":value is "manual"?"Manual":value is "off"?"Off":value);
    static void AddChoice(ComboBox box,string value)=>box.Items.Add(new ComboBoxItem { Tag=value,Content=ChoiceLabel(value) });
    static void SelectChoice(ComboBox box,string? value)=>box.SelectedItem=box.Items.Cast<ComboBoxItem>().FirstOrDefault(x=>x.Tag as string==value);
    void Options(ComboBox box,string[] values,string current) { if(!box.Items.Cast<ComboBoxItem>().Select(x=>(string)x.Tag).SequenceEqual(values)){box.Items.Clear();foreach(var v in values)AddChoice(box,v);}SelectChoice(box,current); }
    void Apply()
    {
        syncing=true;
        try {
            bool unsupported=S("support_status")=="unsupported";
            bool showSupport=unsupported||S("support_status")=="experimental";
            organizer.IsEnabled=Supported;
            padStatus.Text=B("pad_connected")?L.T("Connected"):L.T("Not connected");
            unsupportedPanel.Visibility=showSupport?Visibility.Visible:Visibility.Collapsed;
            startup.IsEnabled=Supported;
            if(showSupport && state.TryGetProperty("support_report",out var report))unsupportedPanel.Update(report,S("model"),B("ready"),S("connection_error")!="—");
            model.Text=L.T(S("model"));
            if(S("model")=="Razer Blade (unregistered model)"&&state.TryGetProperty("support_report",out var identityReport)&&identityReport.TryGetProperty("sku",out var chassis)&&chassis.ValueKind==JsonValueKind.String)
                model.Text+=" · "+DeviceIssuePanel.SafeSku(chassis.GetString()??"");
            model.TextWrapping=TextWrapping.Wrap;
            RefreshReadings();temperatureChart.Update(state);
            telemetry.Visibility=readingAge.Visibility=temperatureChart.Visibility=unsupported?Visibility.Collapsed:Visibility.Visible;
            if(!unsupported&&string.IsNullOrEmpty(readingAge.Text))readingAge.Visibility=Visibility.Collapsed;
            Options(perf,A("modes"),S("mode"));Options(cpu,A("cpu_options"),S("cpu_boost"));Options(gpu,A("gpu_options"),S("gpu_boost"));
            cpu.IsEnabled=gpu.IsEnabled=S("mode")=="Custom";
            cpu.Visibility=gpu.Visibility=S("mode")=="Custom"?Visibility.Visible:Visibility.Collapsed;
            SelectChoice(fan,S("mode")=="—"?null:B("fan_auto")?"Auto":"Manual");
            // Polling never moves controls while the user is editing them.
            if(!dirtySliders.Contains(rpm) && rpm.FocusState==FocusState.Unfocused)rpm.Value=N("fan_rpm",4000);
            if(!dirtySliders.Contains(brightness) && brightness.FocusState==FocusState.Unfocused) { int[] levels={0,13,28,43,59,74,89,105,120,133,148,163,179,194,209,225};brightness.Value=Array.FindIndex(levels,x=>x>=N("brightness",0)); }
            if(!dirtySliders.Contains(capRpm) && capRpm.FocusState==FocusState.Unfocused)capRpm.Value=N("cap_rpm",4000);
            cap.IsOn=B("cap_enabled");SelectChoice(logo,S("logo"));SelectChoice(battery,S("battery"));lights.IsOn=B("lights");startup.IsOn=B("startup");profiles.IsOn=B("auto_profiles");
            profileInfo.Text=L.F("AC: {0} · Battery: {1}",S("ac_profile"),S("battery_profile"));
            info.Text=$"{S("cpu_name")}\n{string.Join(", ",A("gpus"))}\nRAM: {S("ram")} GB\nHID: {S("hid_pid")}\n{L.T("Features")}: {string.Join(", ",A("features"))}\n{L.T("Log")}: %APPDATA%\\r-helper-compact\\controller.log\nCooling Pad: {(B("pad_connected")?S("pad_rpm")+" RPM":L.T("Not connected"))}";
            status.Text=Supported&&B("ready")?L.T("HID connected · controls available"):L.T(S("connection_error"))!="—"?L.T("HID unavailable: ")+L.T(S("connection_error")):L.T("Connecting to HID… Model name is provided by Windows.");
            if(unsupported)errors.IsOpen=false;
            else if(L.T(S("connection_error"))!="—") { errors.Message=L.T(S("connection_error"));errors.IsOpen=true; }
            else if(B("ready") && errors.Message==lastConnectionError)errors.IsOpen=false;
            lastConnectionError=L.T(S("connection_error"));
            reconnect.Visibility=B("ready")?Visibility.Collapsed:Visibility.Visible;
            if(!B("ready")) { rpmCommand.Cancel();brightnessCommand.Cancel(); }
            hardwareHost.IsEnabled=Supported&&B("ready");padHost.IsEnabled=Supported&&B("pad_connected");
            lightingOwner.IsEnabled=Supported;
            lightingOwner.SelectedIndex=B("lighting_external")?1:0;
            lightingHost.IsEnabled=Supported&&B("ready")&&!B("lighting_external");
            lightingHost.Visibility=B("lighting_external")?Visibility.Collapsed:Visibility.Visible;
            keyboardEffect.IsEnabled=keyboardColor.IsEnabled=keyboardColor2.IsEnabled=reactiveDuration.IsEnabled=waveDirection.IsEnabled=applyEffect.IsEnabled=B("keyboard_effect_supported");
            lightingHint.Text=B("lighting_external")?L.T("Configure effects in OpenRGB. R-Helper Compact leaves laptop lighting unchanged, including in profiles."):B("keyboard_effect_supported")?L.T("Apply effects with the button. Brightness applies automatically. Close OpenRGB before using these controls."):L.T("Direct effects are currently available for Blade 14 (2022). Brightness and logo support depend on the device.");
            var features=A("features");
            perf.IsEnabled=features.Contains("perf");
            fan.IsEnabled=cap.IsEnabled=capRpm.IsEnabled=applyCap.IsEnabled=features.Contains("fan");
            rpm.IsEnabled=features.Contains("fan")&&!B("fan_auto");
            rpm.Visibility=B("fan_auto")?Visibility.Collapsed:Visibility.Visible;
            brightness.IsEnabled=features.Contains("kbd-backlight");
            logo.IsEnabled=features.Contains("lid-logo");lights.IsEnabled=features.Contains("lights-always-on");
            battery.IsEnabled=features.Contains("battery-care");
            SelectChoice(padMode,S("pad_mode"));if(!padEffectDirty)SelectChoice(padLight,S("pad_light_mode"));padFollow.IsOn=B("pad_follow");
            padNumbers["pad_manual"].Visibility=S("pad_mode")=="manual"?Visibility.Visible:Visibility.Collapsed;
            padLight.IsEnabled=padBrightness.IsEnabled=padColor.IsEnabled=padEffectButton.IsEnabled=B("pad_lighting");
            if(!B("pad_connected")) { padColorInitialized=false;padEffectDirty=false;padBrightnessCommand.Cancel();foreach(var c in padCommands.Values)c.Cancel();dirtyPadNumbers.Clear(); }
            if(!padColorInitialized && B("pad_connected") && state.TryGetProperty("pad_color",out var padColorValue)) {
                var rgb=padColorValue.EnumerateArray().Select(x=>x.GetByte()).ToArray();
                if(rgb.Length==3) { padColor.Color=Windows.UI.Color.FromArgb(255,rgb[0],rgb[1],rgb[2]);padColorInitialized=true; }
            }
            foreach(var pair in padNumbers)if(!dirtyPadNumbers.Contains(pair.Key)&&pair.Value.FocusState==FocusState.Unfocused)pair.Value.Value=N(pair.Key,pair.Value.Minimum);
            if(!dirtySliders.Contains(padBrightness) && padBrightness.FocusState==FocusState.Unfocused)padBrightness.Value=N("pad_brightness",0);
        } finally { syncing=false; }
    }
    void BuildMenu()
    {
        var menu=tray.ContextMenuStrip!;menu.Items.Clear();
        menu.Items.Add(L.T("Open"),null,(_,_)=>DispatcherQueue.TryEnqueue(ShowPopup));
        var modes=new Forms.ToolStripMenuItem(L.T("Performance"));
        foreach(var mode in state.ValueKind==JsonValueKind.Object?A("modes"):Array.Empty<string>()) { var item=new Forms.ToolStripMenuItem(ChoiceLabel(mode)){Checked=S("mode")==mode};item.Click+=async(_,_)=>await Send("performance",mode);modes.DropDownItems.Add(item); }
        modes.Enabled=state.ValueKind==JsonValueKind.Object&&Supported&&B("ready");menu.Items.Add(modes);
        var cooling=new Forms.ToolStripMenuItem(L.T("Cooling")) { Enabled=modes.Enabled };
        cooling.DropDownItems.Add(L.T("Auto"),null,async(_,_)=>await Send("fan_auto"));
        foreach(var speed in new[]{3000,4000,5000,5500})cooling.DropDownItems.Add($"{speed} RPM",null,async(_,_)=>await Send("fan_rpm",speed));
        menu.Items.Add(cooling);
        var run=new Forms.ToolStripMenuItem(L.T("Run at Windows sign-in")) { Checked=state.ValueKind==JsonValueKind.Object&&B("startup"),Enabled=controller is not null&&Supported };
        run.Click+=async(_,_)=>await Send("startup",!run.Checked);menu.Items.Add(run);
        menu.Items.Add(new Forms.ToolStripSeparator());menu.Items.Add(L.T("Quit"),null,(_,_)=>Quit());
    }
    double ReadingAge(string property) {
        if(state.ValueKind!=JsonValueKind.Object || !state.TryGetProperty(property,out var age))return 0;
        return age.ValueKind==JsonValueKind.Number?age.GetDouble()+Stopwatch.GetElapsedTime(lastReplyTicks).TotalMilliseconds:double.PositiveInfinity;
    }
    bool Stale(string property)=>ReadingAge(property)>5000;
    void RefreshReadings() {
        if(state.ValueKind!=JsonValueKind.Object)return;
        var actual=!readingsConnected||Stale("device_age_ms")?"—":S("actual_rpm");
        telemetry.Text=$"CPU {Temperature("cpu_temp")} °C    GPU {Temperature("gpu_temp")} °C\nFAN {actual} RPM    {(B("ac")?L.T("On AC"):L.T("On battery"))}";
        fanReading.Text=L.F("Actual speed: {0} RPM · requested: {1} RPM",actual,B("fan_auto")?"AUTO":S("fan_rpm"));
        double age=ReadingAge("thermal_age_ms");
        readingAge.Text=!readingsConnected?L.T("Readings unavailable"):double.IsFinite(age)?(age>5000?L.F("Temperature reading is {0:0} s old · refreshing…",age/1000):""):L.T("Waiting for fresh readings…");
        readingAge.Visibility=string.IsNullOrEmpty(readingAge.Text)?Visibility.Collapsed:Visibility.Visible;
    }
    void HidePopup() {
        AppWindow.Hide();if(smoke)return;timer.Stop();
        _=Send("visibility",false,()=>!AppWindow.IsVisible);
    }
    async Task RefreshVisiblePanel() {
        await Send("visibility",true,()=>AppWindow.IsVisible);
        await Task.Delay(250);
        if(AppWindow.IsVisible)await Send("snapshot");
    }
    public void ShowPopup()
    {
        GetCursorPos(out var point);
        var display=DisplayArea.GetFromPoint(new PointInt32(point.X,point.Y),DisplayAreaFallback.Primary);
        var area=display.WorkArea;AppWindow.Move(new PointInt32(area.X+8,area.Y+8));var scale=GetDpiForWindow(hwnd)/96.0;
        int w=Math.Min((int)(500*scale),area.Width-16),h=Math.Min((int)(760*scale),area.Height-16);
        int x=Math.Clamp(point.X-w/2,area.X+8,area.X+area.Width-w-8);
        int y=Math.Clamp(point.Y-h-12,area.Y+8,area.Y+area.Height-h-8);
        AppWindow.MoveAndResize(new RectInt32(x,y,w,h));AppWindow.Show();Activate();SetForegroundWindow(hwnd);
        if(!smoke) {RefreshReadings();temperatureChart.Draw();timer.Start();_=RefreshVisiblePanel();}
    }
    async Task CaptureSmoke()
    {
        try {
            await SliderCommandSmoke.Run();
            TemperatureChart.SmokeTest();
            await UpdateSmoke.Run();
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"smoke-checks.txt"),"\nPASS: update version/channel selection, trusted assets, checksum verification and cancellation; fake HTTP only, no installer launched.");
            var savedReadings=state;
            using(var old=JsonDocument.Parse("""{"cpu_temp":50,"gpu_temp":40,"thermal_age_ms":10000,"device_age_ms":10000,"actual_rpm":3000}""")) {
                state=old.RootElement.Clone();RefreshReadings();
                if(Temperature("cpu_temp")!="—" || telemetry.Text.Contains("3000"))throw new InvalidOperationException("Stale readings shown as current");
            }
            state=savedReadings;RefreshReadings();
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"smoke-checks.txt"),"\nPASS: chart breaks missing/sleep gaps and expires old data; stale CPU/RPM hidden.");

            SectionOrderSmoke();
            if(SendMessage(hwnd,0x7F,0,0)==0 || SendMessage(hwnd,0x7F,1,0)==0)throw new InvalidOperationException("Window icons are missing");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"smoke-checks.txt"),"\nPASS: latest slider values coalesced; last in-flight edit retained; cancellation/unavailable/queued ownership checks prevent writes. Fake transport only.");
            foreach(var theme in new[]{ElementTheme.Light,ElementTheme.Dark}) {
                shell.RequestedTheme=theme;
                ApplyBackground();
                foreach(var bottom in new[]{false,true}) {
                    scroll.ChangeView(null,bottom?scroll.ScrollableHeight:0,null,true);
                    await Task.Delay(180);
                    CaptureFrame($"winui-smoke-{theme.ToString().ToLowerInvariant()}-{(bottom?"bottom":"top")}.png");
                }
            }
            var savedState=state;
            using(var layoutSample=JsonDocument.Parse("""{"support_status":"supported","ready":true,"model":"UI test · synthetic data","pad_connected":false}""")) {
                state=layoutSample.RootElement.Clone();Apply();
                if(hardwareSections.Any(x=>!x.IsEnabled))throw new InvalidOperationException("Supported hardware sections were not enabled");
                var before=organizer.Order;
                organizer.Begin();organizer.Move("pad",-1);
                scroll.ChangeView(null,0,null,true);await Task.Delay(200);
                CaptureFrame("winui-smoke-order.png");
                organizer.Cancel();
                if(!organizer.Order.SequenceEqual(before))throw new InvalidOperationException("Cancel changed section order");
                organizer.Begin();organizer.Move("pad",-1);organizer.Commit();
                if(organizer.Order.SequenceEqual(before))throw new InvalidOperationException("Section move was not applied");
                organizer.Begin();organizer.Move("pad",1);organizer.Commit();
                if(!organizer.Order.SequenceEqual(before))throw new InvalidOperationException("Section move restoration failed");
                if(padStatus.Text!=L.T("Not connected")||padHost.IsEnabled)throw new InvalidOperationException("Disconnected pad status failed");
            }
            foreach(var external in new[]{false,true}) {
                using var lightingSample=JsonDocument.Parse(JsonSerializer.Serialize(new {support_status="supported",model="UI test · synthetic data",logo="Off",ready=true,lighting_external=external,keyboard_effect_supported=true,features=new[]{"kbd-backlight","lid-logo","lights-always-on"}}));
                state=lightingSample.RootElement.Clone();Apply();
                lightingOwner.StartBringIntoView(new BringIntoViewOptions { AnimationDesired=false,VerticalAlignmentRatio=0 });
                await Task.Delay(180);
                CaptureFrame(external?"winui-smoke-openrgb.png":"winui-smoke-effects.png");
                if(!external) {
                    keyboardEffect.SelectedIndex=7;
                    await Task.Delay(180);
                    CaptureFrame("winui-smoke-two-color.png");
                    keyboardEffect.SelectedIndex=1;
                }
            }
            using(var padSample=JsonDocument.Parse("""{"support_status":"supported","model":"UI test · synthetic data","ready":true,"fan_auto":true,"pad_connected":true,"pad_lighting":true,"pad_mode":"auto","pad_light_mode":"Static","pad_color":[18,180,90],"pad_brightness":128,"pad_min":800,"pad_max":2500,"pad_off":40,"pad_full":75}""")) {
                state=padSample.RootElement.Clone();Apply();
                padSection.IsExpanded=true;
                if(padHost.Content is StackPanel padStack)foreach(var child in padStack.Children)if(child is Expander exp)exp.IsExpanded=true;
                await Task.Delay(200);content.UpdateLayout();
                padHost.StartBringIntoView(new BringIntoViewOptions { AnimationDesired=false,VerticalAlignmentRatio=0 });
                await Task.Delay(250);CaptureFrame("winui-smoke-pad.png");
            }
            using(var unknown=JsonDocument.Parse("""{"support_status":"unsupported","model":"Unknown model · synthetic data","support_report":{"sku":"RZ09-9999","hid":["1532:FFFF interface=0 usage=1"],"reason":"Unsupported model. Controls are disabled."}}""")) {
                state=unknown.RootElement.Clone();Apply();scroll.ChangeView(null,0,null,true);
                await Task.Delay(250);CaptureFrame("winui-smoke-unsupported.png");
            }
            foreach(var scenario in new[]{"verified","known-experimental","unknown-razer","non-razer","razer-no-hid"}) {
                bool enabled=scenario is "verified" or "known-experimental" or "unknown-razer";
                bool experimental=scenario is "known-experimental" or "unknown-razer";
                string name=scenario=="verified"?"Razer Blade 14 (2022)":scenario=="known-experimental"?"Razer Blade 16 (2023)":scenario=="non-razer"?"Not a Razer Blade laptop":"Razer Blade (unregistered model)";
                Title="R-Helper Compact · "+L.T("Synthetic preview");
                string reason=enabled?(experimental?"Experimental support: this laptop has not been verified with Compact. Some controls may not work.":"User-confirmed registry profile."):
                    scenario=="non-razer"?"This computer is not identified as a Razer Blade laptop. Controls are disabled.":"Razer laptop detected, but its Blade HID controller could not be identified. Controls are disabled.";
                using var flow=JsonDocument.Parse(JsonSerializer.Serialize(new {
                    support_status=experimental?"experimental":enabled?"supported":"unsupported",ready=enabled,model=name,
                    support_report=new {supported=enabled,experimental,identity=scenario=="non-razer"?"non_razer":"razer",machine=new {model="Razer Blade 16",model_source="windows_family",bios_version="1.09",ec_version=(string?)null},model=name,sku=scenario=="verified"?"RZ09-0427":scenario=="known-experimental"?"RZ09-0483":scenario=="non-razer"?"":"RZ09-9999",hid=enabled?new[]{"1532:FFFF interface=0 usage=0001:0006"}:Array.Empty<string>(),reason},
                    connection_error=enabled?null:reason,cpu_temp=enabled?(double?)57:null,gpu_temp=enabled?(double?)44:null,actual_rpm=enabled?(int?)3000:null,
                    modes=new[]{"Balanced","Silent","Custom"},mode="Balanced",fan_auto=true,fan_rpm=3000,
                    features=enabled?new[]{"perf","fan","kbd-backlight"}:Array.Empty<string>(),pad_connected=false,ac=true
                }));
                state=flow.RootElement.Clone();Apply();unsupportedPanel.ExpandReport(false);
                if(hardwareHost.IsEnabled!=enabled||startup.IsEnabled!=enabled||unsupportedPanel.Visibility!=(scenario=="verified"?Visibility.Collapsed:Visibility.Visible))
                    throw new InvalidOperationException("Support flow availability failed: "+scenario);
                shell.RequestedTheme=ElementTheme.Light;ApplyBackground();
                scroll.ChangeView(null,0,null,true);await Task.Delay(500);
                CaptureFrame("winui-flow-"+scenario+".png");
                if(scenario=="unknown-razer") {
                    unsupportedPanel.ExpandReport(true);await Task.Delay(200);
                    unsupportedPanel.StartBringIntoView(new BringIntoViewOptions {AnimationDesired=false,VerticalAlignmentRatio=0});
                    await Task.Delay(200);CaptureFrame("winui-flow-diagnostics.png");
                }
            }
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"smoke-checks.txt"),"\nPASS: verified, known experimental, unregistered Razer, non-Razer and missing Blade HID UI flows; diagnostic autofill, missing metadata, preserved user corrections, privacy allowlist and text-file round trip. Synthetic only; save picker not automated.");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"smoke-checks.txt"),"\nPASS: unsupported controls blocked even with ready/pad flags; issue URL validation; AUTO RPM; live color swatch and HEX. Synthetic Cooling Pad and unsupported screenshots captured.");
            state=savedState;Apply();
            await Task.Delay(100);
            if(hardwareSections.Any(x=>x.IsEnabled))throw new InvalidOperationException("Moved hardware section bypasses unavailable-device gate");
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"smoke-checks.txt"),"\nPASS: window icons assigned; section move/cancel/save/restore and corrupt/duplicate/unknown IDs; moved sections retain availability gates; Cooling Pad connection status. Native drag gesture needs manual verification.");
            scroll.ChangeView(null,0,null,true);
            await Task.Delay(100);
            var bitmap=new RenderTargetBitmap();
            await bitmap.RenderAsync((UIElement)Content);
            var pixels=await bitmap.GetPixelsAsync();
            var path=Path.Combine(AppContext.BaseDirectory,"winui-smoke.png");
            using var stream=File.Create(path).AsRandomAccessStream();
            var encoder=await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,(uint)bitmap.PixelWidth,(uint)bitmap.PixelHeight,96,96,pixels.ToArray());
            await encoder.FlushAsync();
        } catch(Exception e) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"smoke-error.txt"),e.ToString()); }
    }
    void CaptureFrame(string name) {
        if(!GetWindowRect(hwnd,out var rect))throw new InvalidOperationException("Window rectangle unavailable");
        using var bitmap=new System.Drawing.Bitmap(rect.Right-rect.Left,rect.Bottom-rect.Top);
        using(var graphics=System.Drawing.Graphics.FromImage(bitmap)) {
            var dc=graphics.GetHdc();
            try { if(!PrintWindow(hwnd,dc,2))throw new InvalidOperationException("Window capture failed"); }
            finally { graphics.ReleaseHdc(dc); }
        }
        bitmap.Save(Path.Combine(AppContext.BaseDirectory,name),System.Drawing.Imaging.ImageFormat.Png);
    }
    void SectionOrderSmoke() {
        if(!SectionOrganizer.Normalize(new[]{"b","b","missing"},new[]{"a","b","c"}).SequenceEqual(new[]{"b","a","c"}))throw new InvalidOperationException("Order normalization failed");
        string path=Path.Combine(AppContext.BaseDirectory,"smoke-sections.json");
        File.WriteAllText(path,"[\"b\",\"b\",\"missing\"]");
        int errors=0;var first=new SectionOrganizer(path,()=>{},_=>errors++);
        var preserved=new Expander { Header="A",IsExpanded=true,Content=new TextBox { Text="Pending edit" } };
        first.Add("a","A",preserved);first.Add("b","B",new TextBlock());first.Initialize();
        first.Begin();first.Move("a",-1);first.Commit();
        if(!preserved.IsExpanded||((TextBox)preserved.Content).Text!="Pending edit")throw new InvalidOperationException("Reorder reset section controls");
        var second=new SectionOrganizer(path,()=>{},_=>errors++);
        second.Add("a","A",new TextBlock());second.Add("b","B",new TextBlock());second.Add("c","C",new TextBlock());second.Initialize();
        if(!second.Order.SequenceEqual(new[]{"a","b","c"})||errors!=0)throw new InvalidOperationException("Saved order did not survive restart/new section");
        File.WriteAllText(path,"invalid json");
        var broken=new SectionOrganizer(path,()=>{},_=>errors++);broken.Add("a","A",new TextBlock());broken.Initialize();
        if(errors!=1||broken.Order.Length!=1||File.ReadAllText(path)!="invalid json")throw new InvalidOperationException("Corrupt order file handling failed");
        File.Delete(path);
    }
    void Quit() { if(quitting)return;quitting=true;updates.Dispose();CancelSliderEdits();timer.Stop();tray.Visible=false;tray.Dispose();controller?.Dispose();Close();trayOwner.DestroyHandle();appIcon.Dispose();Application.Current.Exit(); }
    [StructLayout(LayoutKind.Sequential)] struct WindowRect { public int Left,Top,Right,Bottom; }
    [DllImport("user32.dll",EntryPoint="SendMessageW")] static extern nint SendMessage(nint window,uint message,nint wparam,nint lparam);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window,out WindowRect rect);
    [DllImport("user32.dll")] static extern bool PrintWindow(nint window,nint dc,uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint window,int attribute,ref int value,int size);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern nint SetWindowLongPtr(nint window,int index,nint value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint window,nint insertAfter,int x,int y,int width,int height,uint flags);
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X,Y; }
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window,out uint process);
}
