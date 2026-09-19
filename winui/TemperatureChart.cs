using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace RHelper.Compact;

internal sealed class TemperatureChart : StackPanel
{
    internal record Sample(long At,double? Cpu,double? Gpu);
    readonly Canvas plot=new() { Height=88,HorizontalAlignment=HorizontalAlignment.Stretch };
    readonly TextBlock message=new() { FontSize=11,Opacity=.65 };
    readonly List<Sample> samples=new();
    static readonly Windows.UI.Color CpuColor=Windows.UI.Color.FromArgb(255,221,122,31);
    static readonly Windows.UI.Color GpuColor=Windows.UI.Color.FromArgb(255,31,155,193);
    public TemperatureChart() {
        Spacing=3;
        var legend=new StackPanel { Orientation=Orientation.Horizontal,Spacing=12 };
        legend.Children.Add(new TextBlock { Text=L.T("Last 2 minutes"),FontSize=12,Opacity=.75 });
        legend.Children.Add(new TextBlock {Text="CPU",FontSize=12,Foreground=new SolidColorBrush(CpuColor)});
        legend.Children.Add(new TextBlock {Text="GPU",FontSize=12,Foreground=new SolidColorBrush(GpuColor)});
        Children.Add(legend);Children.Add(plot);
        var axis=new Grid();axis.ColumnDefinitions.Add(new ColumnDefinition());axis.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var ago=new TextBlock{Text=L.T("−2 min"),FontSize=11,Opacity=.65};
        var now=new TextBlock{Text=L.T("now · °C"),FontSize=11,Opacity=.65};Grid.SetColumn(now,1);axis.Children.Add(ago);axis.Children.Add(now);
        Children.Add(axis);Children.Add(message);
        plot.SizeChanged+=(_,_)=>Draw();
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(this,L.T("CPU and GPU temperature history"));
    }
    static double? Value(JsonElement p,string name)=>p.TryGetProperty(name,out var v)&&v.TryGetDoubleSafe(out var n)&&double.IsFinite(n)?n:null;
    public void Update(JsonElement state) {
        samples.Clear();
        if(state.TryGetProperty("temperature_history",out var history)&&history.ValueKind==JsonValueKind.Array)
            foreach(var p in history.EnumerateArray())
                if(p.TryGetProperty("at_ms",out var at)&&at.TryGetInt64(out var time))samples.Add(new(time,Value(p,"cpu"),Value(p,"gpu")));
        Draw();
    }
    internal static List<List<Point>> Segments(IEnumerable<Sample> source,bool cpu,long now,double width,double height,double min,double max) {
        var result=new List<List<Point>>();List<Point>? segment=null;long previous=0;
        foreach(var p in source.OrderBy(p=>p.At)) {
            if(p.At<now-120000 || p.At>now)continue;
            double? value=cpu?p.Cpu:p.Gpu;
            if(value is null || !double.IsFinite(value.Value)) {segment=null;previous=p.At;continue;}
            if(segment is null || p.At-previous>15000) {segment=new();result.Add(segment);}
            segment.Add(new Point((p.At-(now-120000))/120000.0*width,height-(value.Value-min)/(max-min)*height));previous=p.At;
        }
        return result;
    }
    internal static (double Min,double Max) CalculateScale(double[] values) {
        if(values.Length==0)return (20,100);
        // Use a shared axis with headroom and a minimum span to avoid exaggerating noise.
        double low=values.Min(),high=values.Max();
        double padding=Math.Max(2,(high-low)*.1);
        double center=(low+high)/2,span=Math.Max(10,high-low+2*padding);
        return (Math.Floor((center-span/2)/5)*5,Math.Ceiling((center+span/2)/5)*5);
    }
    public void Draw() {
        long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var recent=samples.Where(p=>p.At>=now-120000&&p.At<=now).ToArray();
        var values=recent.SelectMany(p=>new[]{p.Cpu,p.Gpu}).Where(v=>v.HasValue&&double.IsFinite(v.Value)).Select(v=>v!.Value).ToArray();
        message.Text=values.Length==0?L.T("Waiting for temperature readings…"):"";
        message.Visibility=values.Length==0?Visibility.Visible:Visibility.Collapsed;
        plot.Children.Clear();double width=Math.Max(1,plot.ActualWidth-30),height=70;
        var (min,max)=CalculateScale(values);
        for(int i=0;i<3;i++) {
            double y=i*height/2;
            var line=new Line { X1=28,X2=width+28,Y1=y+6,Y2=y+6,Stroke=new SolidColorBrush(Windows.UI.Color.FromArgb(50,128,128,128)),StrokeThickness=1 };
            plot.Children.Add(line);
            var label=new TextBlock { Text=(max-i*(max-min)/2).ToString("0.#"),FontSize=10,Opacity=.55 };
            Canvas.SetTop(label,Math.Max(0,y-1));plot.Children.Add(label);
        }
        foreach(bool cpu in new[]{true,false})foreach(var segment in Segments(recent,cpu,now,width,height,min,max)) {
            var brush=new SolidColorBrush(cpu?CpuColor:GpuColor);
            if(segment.Count==1) {
                var dot=new Ellipse {Width=4,Height=4,Fill=brush};Canvas.SetLeft(dot,segment[0].X+26);Canvas.SetTop(dot,segment[0].Y+4);plot.Children.Add(dot);
            } else {
                var line=new Polyline {Stroke=brush,StrokeThickness=2,StrokeLineJoin=PenLineJoin.Round};
                foreach(var p in segment)line.Points.Add(new Point(p.X+28,p.Y+6));plot.Children.Add(line);
            }
        }
    }
    internal static void SmokeTest() {
        var narrow=CalculateScale(new[]{50.0,55.0});
        if(narrow.Min!=45 || narrow.Max!=60)throw new InvalidOperationException("Small temperature changes need a readable scale");
        var flat=CalculateScale(new[]{50.0,50.01});
        if(flat.Max-flat.Min<10)throw new InvalidOperationException("Chart exaggerated temperature noise");
        var wide=CalculateScale(new[]{25.0,105.0});
        if(wide.Min>=25 || wide.Max<=105)throw new InvalidOperationException("Chart clipped temperature extremes");
        var data=new[]{new Sample(100000,50,40),new Sample(102000,null,41),new Sample(104000,52,42),new Sample(124000,55,43)};
        if(Segments(data,true,124000,120,70,20,100).Count!=3 || Segments(data,false,124000,120,70,20,100).Count!=2)throw new InvalidOperationException("Chart bridged a missing reading or sleep gap");
        if(Segments(data,true,300000,120,70,20,100).Count!=0)throw new InvalidOperationException("Expired chart samples retained");
    }
}
internal static class JsonNumberExtensions {
    public static bool TryGetDoubleSafe(this JsonElement value,out double number) { number=0;return value.ValueKind==JsonValueKind.Number&&value.TryGetDouble(out number); }
}
