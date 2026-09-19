using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace RHelper.Compact;

internal static class LanguagePreference
{
    public static string[] Available => Assembly.GetExecutingAssembly().GetManifestResourceNames()
        .Where(n=>n.StartsWith("RHelper.Locales.",StringComparison.Ordinal)&&n.EndsWith(".json",StringComparison.Ordinal))
        .Select(n=>n["RHelper.Locales.".Length..^5]).OrderBy(n=>n,StringComparer.Ordinal).ToArray();
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"r-helper-compact","language.json");
    public static string Read(string path) {
        try {
            var language=JsonSerializer.Deserialize<string>(File.ReadAllText(path));
            return Array.IndexOf(Available,language)>=0?language!:"";
        } catch(Exception e) when(e is IOException or UnauthorizedAccessException or JsonException) { return ""; }
    }
    public static void Save(string path,string language) {
        if(language!=""&&Array.IndexOf(Available,language)<0)throw new ArgumentException("Unsupported language",nameof(language));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(language));File.Move(temp,path,true);
    }
    public static void Apply() {
        string language=Read(SettingsPath);
        if(language=="")return;
        var culture=CultureInfo.GetCultureInfo(language);
        CultureInfo.DefaultThreadCurrentUICulture=CultureInfo.CurrentUICulture=culture;
        CultureInfo.DefaultThreadCurrentCulture=CultureInfo.CurrentCulture=culture;
    }
}
