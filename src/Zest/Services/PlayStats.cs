using System.Text.Json;

namespace Zest.Services;

public sealed class GameStats
{
    public double Seconds { get; set; }
    public int Launches { get; set; }
    public DateTime? LastPlayed { get; set; }
}

/// <summary>Play time per game file, recorded by Zest while the emulator process is running.</summary>
public sealed class PlayStats
{
    private readonly Dictionary<string, GameStats> _byPath;
    private readonly Lock _lock = new();

    private PlayStats(Dictionary<string, GameStats> byPath) => _byPath = byPath;

    public static PlayStats Load()
    {
        try
        {
            if (File.Exists(AppPaths.StatsFile))
                return new PlayStats(JsonSerializer.Deserialize<Dictionary<string, GameStats>>(File.ReadAllText(AppPaths.StatsFile)) ?? []);
        }
        catch (JsonException)
        {
        }
        return new PlayStats([]);
    }

    public GameStats? Get(string path)
    {
        lock (_lock)
            return _byPath.GetValueOrDefault(path);
    }

    public GameStats RecordLaunch(string path)
    {
        lock (_lock)
        {
            if (!_byPath.TryGetValue(path, out var s))
                _byPath[path] = s = new GameStats();
            s.Launches++;
            s.LastPlayed = DateTime.Now;
            Save();
            return s;
        }
    }

    public void AddTime(string path, TimeSpan played)
    {
        lock (_lock)
        {
            if (!_byPath.TryGetValue(path, out var s))
                _byPath[path] = s = new GameStats();
            s.Seconds += played.TotalSeconds;
            s.LastPlayed = DateTime.Now;
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.StatsFile)!);
        File.WriteAllText(AppPaths.StatsFile, JsonSerializer.Serialize(_byPath, new JsonSerializerOptions { WriteIndented = true }));
    }
}
