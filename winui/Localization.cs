using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;

namespace RHelper.Compact;

internal static class L
{
    static readonly Dictionary<string,string> english=Read("en")??new();
    static readonly Dictionary<string,string> translated;
    public static string Language { get; }
    static L() {
        translated=english;Language="en";
        for(var culture=CultureInfo.CurrentUICulture;culture!=CultureInfo.InvariantCulture;culture=culture.Parent) {
            var found=Read(culture.Name);
            if(found is null)continue;
            translated=found;Language=culture.Name;break;
        }
    }
    static Dictionary<string,string>? Read(string culture) {
        var assembly=Assembly.GetExecutingAssembly();
        using var stream=assembly.GetManifestResourceStream("RHelper.Locales."+culture+".json");
        if(stream is null)return null;
        try { return JsonSerializer.Deserialize<Dictionary<string,string>>(stream); }
        catch(JsonException) { return null; }
    }
    public static string T(string text) => translated.TryGetValue(text,out var value)&&!string.IsNullOrWhiteSpace(value)?value:english.GetValueOrDefault(text,text);
    public static string F(string text,params object?[] args) {
        try { return string.Format(CultureInfo.CurrentCulture,T(text),args); }
        catch(FormatException) { return string.Format(CultureInfo.CurrentCulture,english.GetValueOrDefault(text,text),args); }
    }
}
