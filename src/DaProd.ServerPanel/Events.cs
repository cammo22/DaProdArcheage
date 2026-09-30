using System.Text.Json;
using System.Text.Json.Serialization;

namespace DaProd.ServerPanel;

public enum EventAction { News, Sql, ConsoleCommand, RestartServer, Backup }

/// <summary>Un evento programmato: una volta (When) o ogni giorno all'ora indicata.</summary>
public sealed class GameEvent
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Nuovo evento";
    public EventAction Action { get; set; } = EventAction.News;
    public DateTime When { get; set; } = DateTime.Now.AddHours(1);
    public bool Daily { get; set; }
    public string Payload { get; set; } = "";
    public DateTime? LastRun { get; set; }
    /// <summary>Mostra questo evento nel launcher tra i "prossimi eventi".</summary>
    public bool ShowInLauncher { get; set; } = true;

    public DateTime NextRun()
    {
        if (!Daily) return When;
        var t = DateTime.Today + When.TimeOfDay;
        return t < DateTime.Now && (LastRun ?? DateTime.MinValue) >= t ? t.AddDays(1) : t;
    }
    public override string ToString() => (Enabled ? "" : "(off) ") + Name;
}

/// <summary>Salva gli eventi in data\events.json e li esegue quando è il momento.</summary>
public sealed class EventScheduler
{
    static string FilePath => Path.Combine(PanelSettings.DataDir, "events.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

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

    /// <summary>Eventi in arrivo (per il launcher).</summary>
    public IEnumerable<object> Upcoming() =>
        Events.Where(e => e.Enabled && e.ShowInLauncher).Select(e => (e.Name, At: e.NextRun()))
            .Where(x => x.At > DateTime.Now.AddMinutes(-30)).OrderBy(x => x.At).Take(5)
            .Select(x => new { name = x.Name, at = x.At.ToString("s") });

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
