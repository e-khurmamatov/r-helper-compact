using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RHelper.Compact;
internal sealed class Controller : IDisposable
{
    Process process = null!;
    readonly SemaphoreSlim gate = new(1);
    readonly object logGate = new();
    readonly string logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"r-helper-compact","controller.log");
    long sequence;
    public UpdateInstaller.ProcessIdentity? Identity {get {try {return process.HasExited?null:UpdateInstaller.Identity(process);}catch(InvalidOperationException){return null;}}}
    public Controller()
    {
        Start();
    }
    void Log(string message)
    {
        try { lock(logGate) { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); if(File.Exists(logPath) && new FileInfo(logPath).Length>1024*1024) File.Move(logPath,logPath+".previous",true); File.AppendAllText(logPath,$"{DateTimeOffset.Now:O} {message}{Environment.NewLine}"); } }
        catch(IOException) { } catch(UnauthorizedAccessException) { }
    }
    void Start()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "rhelper-controller.exe");
        process = new Process { StartInfo = new ProcessStartInfo(path, "--winui-backend") {
            UseShellExecute=false, CreateNoWindow=true, RedirectStandardInput=true,
            RedirectStandardOutput=true, RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false), StandardOutputEncoding=Encoding.UTF8, StandardErrorEncoding=Encoding.UTF8 } };
        process.ErrorDataReceived+=(_,e)=> { if(e.Data is not null)Log(e.Data); };
        process.Start();
        Log("Controller started");
        process.BeginErrorReadLine();
    }
    async Task Reconnect()
    {
        if(!process.HasExited) {
            process.StandardInput.Close();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch(OperationCanceledException) { throw new IOException(L.T("Controller is still shutting down. Try reconnecting later.")); }
        }
        process.Dispose();
        Start();
    }
    public async Task<JsonElement> Send(string action, object? value = null)
    {
        await gate.WaitAsync();
        try {
            if(action=="reconnect") { await Reconnect();action="snapshot"; }
            if (process.HasExited) throw new IOException(L.T("Controller exited. Close and restart the application."));
            var id=++sequence;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new {id,action,value}));
            await process.StandardInput.FlushAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true) {
                var line=await process.StandardOutput.ReadLineAsync(timeout.Token);
                if (line is null) throw new IOException(L.T("Controller connection closed."));
                JsonDocument doc;
                try { doc=JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using(doc) {
                    var root=doc.RootElement;
                    if (!root.TryGetProperty("id",out var reply) || reply.GetInt64()!=id) continue;
                    if (root.TryGetProperty("error",out var error) && error.ValueKind==JsonValueKind.String)
                    { Log($"{action}: {error.GetString()}");throw new IOException(error.GetString()); }
                    return root.GetProperty("state").Clone();
                }
            }
        } finally { gate.Release(); }
    }
    public void Dispose()
    {
        // EOF lets the existing controller shut down normally; never terminate it forcibly.
        try { process.StandardInput.Close(); } catch (IOException) { }
        process.Dispose();
    }
}
