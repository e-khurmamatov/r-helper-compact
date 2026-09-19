using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;

namespace RHelper.Compact;

// Only presentation state. This component has no controller or device commands.
internal sealed class SectionOrganizer : StackPanel
{
    readonly StackPanel normal=new() { Spacing=8 };
    readonly ListView editor=new() { CanDragItems=true,CanReorderItems=true,AllowDrop=true,SelectionMode=ListViewSelectionMode.None,Visibility=Visibility.Collapsed };
    readonly ObservableCollection<StackPanel> rows=new();
    readonly Dictionary<string,(string Title,UIElement View)> sections=new();
    readonly Button edit=new() { Content=L.T("Change section order") };
    readonly Button cancel=new() { Content=L.T("Cancel"),Visibility=Visibility.Collapsed };
    readonly TextBlock hint=new() { Text=L.T("Drag sections by ⠿ or use the arrows."),TextWrapping=TextWrapping.Wrap,Opacity=.7,Visibility=Visibility.Collapsed };
    readonly Action starting;
    readonly Action<Exception> error;
    readonly string? path;
    string[] order=Array.Empty<string>();
    public bool Editing { get; private set; }
    public bool IsDragging { get; private set; }
    public bool IsEnabled { get=>edit.IsEnabled; set { edit.IsEnabled=editor.IsEnabled=value;if(!value&&Editing)Cancel(); } }
    public string[] Order => order.ToArray();

    public SectionOrganizer(string? path,Action starting,Action<Exception> error) {
        this.path=path;this.starting=starting;this.error=error;Spacing=8;
        var toolbar=new StackPanel { Orientation=Orientation.Horizontal,Spacing=8 };
        toolbar.Children.Add(edit);toolbar.Children.Add(cancel);
        Children.Add(toolbar);Children.Add(hint);Children.Add(normal);Children.Add(editor);
        editor.ItemsSource=rows;
        editor.ItemContainerStyle=new Style(typeof(ListViewItem));
        editor.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch));
        editor.ReorderMode=ListViewReorderMode.Enabled;
        editor.DragItemsStarting+=(_,_)=>IsDragging=true;
        editor.DragItemsCompleted+=(_,_)=>IsDragging=false;
        edit.Click+=(_,_)=> { if(Editing)Commit();else Begin(); };
        cancel.Click+=(_,_)=>Cancel();
    }
    public void Add(string id,string title,UIElement view) { sections.Add(id,(title,view)); }
    internal static string[] Normalize(IEnumerable<string>? saved,IEnumerable<string> available) {
        var defaults=available.ToArray();var known=defaults.ToHashSet(StringComparer.Ordinal);
        return (saved??Array.Empty<string>()).Where(x=>x is not null&&known.Contains(x)).Concat(defaults).Distinct(StringComparer.Ordinal).ToArray();
    }
    public void Initialize(IEnumerable<string>? defaults=null) {
        string[]? saved=null;
        try { if(path is not null&&File.Exists(path))saved=JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)); }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { error(e); }
        order=Normalize(saved,Normalize(defaults,sections.Keys));Arrange();
    }
    void Arrange() { normal.Children.Clear();foreach(var id in order)normal.Children.Add(sections[id].View); }
    public void Begin() {
        if(Editing)return;starting();rows.Clear();
        foreach(var id in order) {
            var row=new StackPanel { Tag=id,Orientation=Orientation.Horizontal,Spacing=8,Padding=new Thickness(4,8,4,8) };
            row.Children.Add(new TextBlock { Text="⠿",VerticalAlignment=VerticalAlignment.Center,FontSize=20 });
            var name=new TextBlock { Text=sections[id].Title,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,Width=180 };
            row.Children.Add(name);
            foreach(var delta in new[]{-1,1}) {
                var b=new Button { Content=delta<0?"↑":"↓" };
                AutomationProperties.SetName(b,sections[id].Title+(delta<0?L.T(" up"):L.T(" down")));
                b.Click+=(_,_)=>Move(id,delta);row.Children.Add(b);
            }
            rows.Add(row);
        }
        Editing=true;normal.Visibility=Visibility.Collapsed;editor.Visibility=hint.Visibility=cancel.Visibility=Visibility.Visible;edit.Content=L.T("Done");
    }
    public void Move(string id,int delta) {
        var row=rows.FirstOrDefault(x=>x.Tag as string==id);if(row is null)return;
        int from=rows.IndexOf(row),to=from+delta;if(to>=0&&to<rows.Count)rows.Move(from,to);
    }
    public void Commit() {
        if(!Editing)return;
        var next=Normalize(rows.Select(x=>(string)x.Tag),sections.Keys);
        try {
            if(path is not null) {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string temp=path+".tmp";
                File.WriteAllText(temp,JsonSerializer.Serialize(next));File.Move(temp,path,true);
            }
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException) { error(e);return; }
        order=next;Arrange();Finish();
    }
    public void Cancel() { if(Editing)Finish(); }
    void Finish() { Editing=false;editor.Visibility=hint.Visibility=cancel.Visibility=Visibility.Collapsed;normal.Visibility=Visibility.Visible;edit.Content=L.T("Change section order"); }
}
