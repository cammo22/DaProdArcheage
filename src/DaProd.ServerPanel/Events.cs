using System.Net;
using System.Text;
using System.Text.Json;

namespace DaProd.ServerPanel;

public enum EventAction { News, Sql, ConsoleCommand, RestartServer }

/// <summary>Un evento programmato: una volta (Date) o ogni giorno all'ora indicata.</summary>
public sealed class GameEvent
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Nuovo evento";
    public EventAction Action { get; set; } = EventAction.News;
    public DateTime When { get; set; } = DateTime.Now.AddHours(1);
    public bool Daily { get; set; }
    public string Payload { get; set; } = "";
    public DateTime? LastRun { get; set; }
}

/// <summary>Salva gli eventi in data\events.json e li esegue quando è il momento.</summary>
public sealed class EventScheduler
{
    static string FilePath => Path.Combine(PanelSettings.DataDir, "events.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public List<GameEvent> Events { get; private set; } = [];
    readonly System.Threading.Timer _timer;
    readonly Func<GameEvent, Task> _run;

    public EventScheduler(Func<GameEvent, Task> run)
    {
        _run = run;
        try { if (File.Exists(FilePath)) Events = JsonSerializer.Deserialize<List<GameEvent>>(File.ReadAllText(FilePath), Opts) ?? []; } catch { }
        _timer = new System.Threading.Timer(_ => Tick(), null, 10_000, 30_000);
    }

    public void Save()
    {
        Directory.CreateDirectory(PanelSettings.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Events, Opts));
    }

    void Tick()
    {
        var now = DateTime.Now;
        foreach (var e in Events.ToList())
        {
            if (!e.Enabled) continue;
            var due = e.Daily ? now.Date + e.When.TimeOfDay : e.When;
            if (now < due || (e.LastRun.HasValue && e.LastRun.Value >= due)) continue;
            e.LastRun = now;
            if (!e.Daily) e.Enabled = false;
            _ = _run(e);
        }
        Save();
    }
}

/// <summary>Mini API HTTP letta dal launcher: http://IP:porta/launcher.json</summary>
public sealed class LauncherApi(PanelSettings s, Action<string, string> log)
{
    HttpListener? _l;

    public void Start()
    {
        try
        {
            _l = new HttpListener();
            _l.Prefixes.Add($"http://+:{s.LauncherApiPort}/");
            _l.Start();
        }
        catch
        {
            // Senza permessi admin "+" non è consentito: ripiego su localhost.
            _l = new HttpListener();
            _l.Prefixes.Add($"http://localhost:{s.LauncherApiPort}/");
            _l.Start();
            log("api", $"API solo su localhost. Per gli amici esegui una volta come admin: netsh http add urlacl url=http://+:{s.LauncherApiPort}/ user=Everyone");
        }
        log("api", $"API launcher attiva sulla porta {s.LauncherApiPort}.");
        _ = Task.Run(Loop);
    }

    async Task Loop()
    {
        while (_l is { IsListening: true })
        {
            try
            {
                var ctx = await _l.GetContextAsync();
                var body = JsonSerializer.Serialize(new
                {
                    name = s.ServerName, ip = s.PublicIp, port = s.LoginPort,
                    news = s.News, clientUrl = s.ClientDownloadUrl, launchArgs = s.LaunchArgs
                });
                var b = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(b);
                ctx.Response.Close();
            }
            catch { }
        }
    }
}
