using System.Text.Json;
using Microsoft.Extensions.Logging;
using TheKrystalShip.Kgsm.Scheduler.Json;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// One restart that has been announced to the people on a server, and what has been said so far.
/// </summary>
/// <param name="FireAtUtc">The restart this was opened against. A moved target opens a new window.</param>
/// <param name="AnnouncedLeads">Lead marks already spent, so a resumed daemon does not repeat them.</param>
internal sealed record PendingAnnouncement(DateTimeOffset FireAtUtc, IReadOnlyList<int> AnnouncedLeads);

/// <summary>
/// Remembers, across a restart of this daemon, which servers have been told a restart is coming.
/// </summary>
/// <remarks>
/// <para>
/// A countdown outlives a tick and can outlive the process running it. Without this, a daemon that
/// restarts mid-window either repeats every announcement it already made or — worse — restarts a
/// server it had promised fifteen minutes' warning to, having forgotten it made the promise.
/// </para>
/// <para>
/// <b>An entry is a debt, not a schedule.</b> It exists only while something has been said and the
/// restart it was said about has not happened. The schedule itself is never stored here: it is
/// derived from the instance's own configuration on every tick, so this file can be deleted at any
/// time and the only thing lost is the memory of what was already announced.
/// </para>
/// </remarks>
internal sealed class PendingAnnouncementStore
{
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private Dictionary<string, PendingAnnouncement> _entries = new(StringComparer.Ordinal);

    public PendingAnnouncementStore(string stateDirectory, ILogger logger)
    {
        _path = Path.Combine(stateDirectory, "pending-announcements.json");
        _logger = logger;
        Load();
    }

    public PendingAnnouncement? Get(string instance)
    {
        lock (_lock) return _entries.GetValueOrDefault(instance);
    }

    public void Set(string instance, PendingAnnouncement entry)
    {
        lock (_lock)
        {
            _entries[instance] = entry;
            Save();
        }
    }

    public void Clear(string instance)
    {
        lock (_lock)
        {
            if (_entries.Remove(instance))
            {
                Save();
            }
        }
    }

    /// <summary>Every instance currently owed a restart it was told about.</summary>
    public IReadOnlyDictionary<string, PendingAnnouncement> Snapshot()
    {
        lock (_lock) return new Dictionary<string, PendingAnnouncement>(_entries, StringComparer.Ordinal);
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize(
                File.ReadAllText(_path), SchedulerJsonContext.Default.DictionaryStringPendingAnnouncement);

            if (loaded is not null)
            {
                _entries = new Dictionary<string, PendingAnnouncement>(loaded, StringComparer.Ordinal);
            }
        }
        catch (Exception ex)
        {
            // A file that cannot be read is treated as no memory of what was announced, which costs
            // at worst a repeated announcement. Refusing to start over it would take the whole
            // schedule down for a record that exists only to be polite about restarts.
            _logger.LogWarning(ex, "Could not read {Path}; continuing with no pending announcements", _path);
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // Write beside the target and rename, so a daemon killed mid-write leaves the previous
            // file whole rather than a truncated one that reads as no pending announcements.
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(
                _entries, SchedulerJsonContext.Default.DictionaryStringPendingAnnouncement));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write {Path}; announcements may repeat after a restart", _path);
        }
    }
}
