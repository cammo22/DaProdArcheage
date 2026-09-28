using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;

namespace DaProd.ServerPanel;

/// <summary>Avvia/ferma MySQL, Login e Game; prepara database e configurazioni.</summary>
public sealed class ServerManager(PanelSettings s, Action<string, string> log)
{
    readonly Dictionary<string, Process> _procs = new();

    static string ServerDir => Path.Combine(PanelSettings.Root, "server");
    static string MySqlData => Path.Combine(ServerDir, "mysql-data");
    static string LoginDir => Path.Combine(ServerDir, "bin", "login");
    static string GameDir => Path.Combine(ServerDir, "bin", "game");
    static string SqlDir => Path.Combine(ServerDir, "sql");

    public string ConnString(string db = "") =>
        $"Server=127.0.0.1;Port={s.MySqlPort};User ID=root;Database={db};SslMode=None;AllowUserVariables=true";

    public bool IsRunning(string name) => _procs.TryGetValue(name, out var p) && !p.HasExited;

    // ---------- Setup ----------

    public async Task FirstSetupAsync()
    {
        if (!Directory.Exists(MySqlData))
        {
            log("mysql", "Inizializzo il database MySQL (prima volta)...");
            await RunToEndAsync(Path.Combine(s.MySqlBinDir, "mysqld.exe"),
                $"--initialize-insecure --datadir=\"{MySqlData}\" --console", "mysql");
        }
        await StartMySqlAsync();

        await using (var c = new MySqlConnection(ConnString()))
        {
            await c.OpenAsync();
            foreach (var db in new[] { "aaemu_login", "aaemu_game" })
            {
                var exists = await new MySqlCommand($"SHOW DATABASES LIKE '{db}'", c).ExecuteScalarAsync() != null;
                if (exists) { log("mysql", $"{db} già presente, salto."); continue; }
                await new MySqlCommand($"CREATE DATABASE `{db}` CHARACTER SET utf8mb4", c).ExecuteNonQueryAsync();
                log("mysql", $"Importo {db}.sql ...");
                await RunToEndAsync(Path.Combine(s.MySqlBinDir, "mysql.exe"),
                    $"-uroot -h127.0.0.1 -P{s.MySqlPort} {db} -e \"source {Path.Combine(SqlDir, db + ".sql").Replace('\\', '/')}\"", "mysql");
            }
        }
        WriteConfigs();
        log("panel", "Setup completato.");
    }

    public void WriteConfigs()
    {
        var db = new JsonObject
        {
            ["Host"] = "127.0.0.1", ["Port"] = s.MySqlPort.ToString(), ["User"] = "root",
            ["Password"] = "", ["SslMode"] = "None"
        };
        JsonNode Db(string name) { var o = db.DeepClone(); o["Database"] = name; return o; }

        var login = new JsonObject
        {
            ["AutoAccount"] = s.AutoAccount,
            ["Network"] = new JsonObject { ["Host"] = "*", ["Port"] = s.LoginPort },
            ["Connections"] = new JsonObject { ["MySQLProvider"] = Db("aaemu_login") },
            ["GameServers"] = new JsonArray(new JsonObject
            {
                // Con Tailscale il launcher inoltra le porte su 127.0.0.1, quindi il client deve andare a localhost
                ["ID"] = 1, ["Name"] = s.ServerName, ["Host"] = s.UseTailscale ? "127.0.0.1" : s.PublicIp, ["Port"] = s.GamePort
            })
        };
        var game = new JsonObject
        {
            ["Network"] = new JsonObject { ["Host"] = "*", ["Port"] = s.GamePort },
            ["LoginNetwork"] = new JsonObject { ["Host"] = "127.0.0.1", ["Port"] = "1234" },
            ["Connections"] = new JsonObject { ["MySQLProvider"] = Db("aaemu_game") },
        };
        if (!string.IsNullOrWhiteSpace(s.GamePakDir))
            game["ClientData"] = new JsonObject { ["Sources"] = new JsonArray(s.GamePakDir) };

        var o = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(LoginDir, "Config.Local.json"), login.ToJsonString(o));
        File.WriteAllText(Path.Combine(GameDir, "Config.Local.json"), game.ToJsonString(o));
        log("panel", "Config.Local.json scritti per Login e Game.");
    }

    // ---------- Processi ----------

    public async Task StartMySqlAsync()
    {
        if (IsRunning("mysql")) return;
        Start("mysql", Path.Combine(s.MySqlBinDir, "mysqld.exe"),
            $"--datadir=\"{MySqlData}\" --port={s.MySqlPort} --bind-address=127.0.0.1 --console", s.MySqlBinDir);
        for (var i = 0; i < 30; i++)
        {
            try { await using var c = new MySqlConnection(ConnString()); await c.OpenAsync(); return; }
            catch { await Task.Delay(1000); }
        }
        throw new Exception("MySQL non risponde dopo 30 secondi.");
    }

    public async Task StartAllAsync()
    {
        await StartMySqlAsync();
        WriteConfigs();
        if (!IsRunning("login")) Start("login", Path.Combine(LoginDir, "AAEmu.Login.exe"), "", LoginDir);
        await Task.Delay(2000);
        if (!IsRunning("game")) Start("game", Path.Combine(GameDir, "AAEmu.Game.exe"), "", GameDir);
    }

    public void StopAll()
    {
        foreach (var n in new[] { "game", "login", "mysql" }) Stop(n);
    }

    public void Stop(string name)
    {
        if (!_procs.TryGetValue(name, out var p)) return;
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { }
        _procs.Remove(name);
        log(name, "Fermato.");
    }

    void Start(string name, string exe, string args, string cwd)
    {
        if (!File.Exists(exe)) { log(name, $"File mancante: {exe}"); return; }
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(exe, args)
            {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
            },
            EnableRaisingEvents = true
        };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log(name, e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log(name, e.Data); };
        p.Exited += (_, _) => log(name, "Processo terminato.");
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        _procs[name] = p;
        log(name, "Avviato.");
    }

    /// <summary>Invia un comando alla console del processo (es. comandi admin del Game server).</summary>
    public void SendConsole(string name, string line)
    {
        if (_procs.TryGetValue(name, out var p) && !p.HasExited) p.StandardInput.WriteLine(line);
        else log(name, "Processo non attivo, comando ignorato.");
    }

    async Task RunToEndAsync(string exe, string args, string tag)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log(tag, e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log(tag, e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync();
    }

    // ---------- Account ----------

    public static string HashPassword(string pwd) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pwd)));

    public async Task CreateAccountAsync(string user, string pwd)
    {
        await using var c = new MySqlConnection(ConnString("aaemu_login"));
        await c.OpenAsync();
        var cmd = new MySqlCommand("INSERT INTO users (username,password,email,last_login,last_ip,created_at,updated_at) VALUES (@u,@p,'',0,'',0,0)", c);
        cmd.Parameters.AddWithValue("@u", user); cmd.Parameters.AddWithValue("@p", HashPassword(pwd));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetPasswordAsync(long id, string pwd)
    {
        await using var c = new MySqlConnection(ConnString("aaemu_login"));
        await c.OpenAsync();
        var cmd = new MySqlCommand("UPDATE users SET password=@p WHERE id=@i", c);
        cmd.Parameters.AddWithValue("@p", HashPassword(pwd)); cmd.Parameters.AddWithValue("@i", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAccountAsync(long id)
    {
        await using var c = new MySqlConnection(ConnString("aaemu_login"));
        await c.OpenAsync();
        var cmd = new MySqlCommand("DELETE FROM users WHERE id=@i", c);
        cmd.Parameters.AddWithValue("@i", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<System.Data.DataTable> QueryAsync(string db, string sql)
    {
        await using var c = new MySqlConnection(ConnString(db));
        await c.OpenAsync();
        var t = new System.Data.DataTable();
        using var r = await new MySqlCommand(sql, c).ExecuteReaderAsync();
        t.Load(r);
        return t;
    }

    public async Task<int> ExecAsync(string db, string sql)
    {
        await using var c = new MySqlConnection(ConnString(db));
        await c.OpenAsync();
        return await new MySqlCommand(sql, c).ExecuteNonQueryAsync();
    }
}
