namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>What an engine failure says, in the one line a record can carry.</summary>
internal static class EngineDetail
{
    /// <summary>
    /// The failure as one line.
    /// </summary>
    /// <remarks>
    /// kgsm writes several lines of context on a refusal, and both sockets serve one NDJSON line per
    /// connection, so the last line — the one naming what actually went wrong — is what travels.
    /// </remarks>
    internal static string? Summarize(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;

        string? last = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();

        return string.IsNullOrWhiteSpace(last) ? null : last;
    }
}
