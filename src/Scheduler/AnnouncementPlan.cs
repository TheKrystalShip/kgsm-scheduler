using System.Globalization;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// Reads an instance's announcement configuration and decides what, if anything, to say
/// about a restart that is coming.
/// </summary>
/// <remarks>
/// Pure: it takes the configuration and a distance in time and answers with text. Whether a
/// game can carry that text, and whether anybody is there to read it, are separate questions
/// answered elsewhere.
/// </remarks>
internal static class AnnouncementPlan
{
    /// <summary>
    /// Reads the lead times an instance announces at, largest first.
    /// </summary>
    /// <remarks>
    /// Anything that is not a positive whole number of minutes is dropped rather than failing the
    /// list: this is hand-edited configuration, and one bad entry silencing an instance's other
    /// lead times would be a worse answer than honouring the ones that parse. Duplicates collapse,
    /// because announcing the same distance twice says nothing the first did not.
    /// </remarks>
    public static IReadOnlyList<int> ParseLeadMinutes(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return [];
        }

        var leads = new SortedSet<int>();

        foreach (string part in configured.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minutes) && minutes > 0)
            {
                leads.Add(minutes);
            }
        }

        return leads.Reverse().ToArray();
    }

    /// <summary>
    /// Picks the lead mark to speak now, out of those not yet spoken.
    /// </summary>
    /// <param name="leads">Every configured lead, in any order.</param>
    /// <param name="alreadyAnnounced">Marks already spoken for this restart.</param>
    /// <param name="minutesUntilFire">How far off the restart is.</param>
    /// <param name="due">
    /// When this returns <see langword="true"/>, every mark that has come due — all of which are
    /// spent by this announcement, whether or not they are the one spoken.
    /// </param>
    /// <returns>
    /// The mark to say out loud, or <see langword="null"/> when nothing is due.
    /// </returns>
    /// <remarks>
    /// ⚠ <b>Several marks can fall due at once</b> — a daemon that was down, or a tick that ran
    /// long, arrives to find 15, 5 and 1 all passed. Speaking all three would tell players the
    /// server restarts in fifteen minutes when it restarts in one. The <em>smallest</em> due mark
    /// is the only true statement of the three, so it is the one spoken and the rest are marked
    /// spent without being said.
    /// </remarks>
    public static int? NextMark(
        IReadOnlyList<int> leads,
        IReadOnlyCollection<int> alreadyAnnounced,
        double minutesUntilFire,
        out IReadOnlyList<int> due)
    {
        var pending = leads
            .Where(m => !alreadyAnnounced.Contains(m))
            .Where(m => minutesUntilFire <= m)
            .ToArray();

        due = pending;

        return pending.Length == 0 ? null : pending.Min();
    }

    /// <summary>
    /// Resolves an announcement message against the restart it describes.
    /// </summary>
    /// <remarks>
    /// <c>{minutes}</c> is the lead being announced and <c>{instance}</c> is the label a person
    /// reads the server by. The result is what the engine then substitutes into the game's own
    /// broadcast template — a separate step with a different placeholder, which is why a message
    /// containing <c>{message}</c> needs no special handling here.
    /// </remarks>
    public static string Resolve(string template, Instance instance, int? minutes)
    {
        string resolved = template.Replace("{instance}", instance.DisplayName, StringComparison.Ordinal);

        return minutes is null
            ? resolved
            : resolved.Replace("{minutes}", minutes.Value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether this instance can be announced to at all — it declares lead times, a message, and
    /// its game declares a console broadcast command.
    /// </summary>
    /// <remarks>
    /// A game with no <see cref="Instance.BroadcastCommand"/> is not a misconfiguration: several
    /// have no console command surface, and several more expose one only on a channel the engine
    /// does not write to. The restart still happens; it simply happens unannounced.
    /// </remarks>
    public static bool CanAnnounce(Instance instance) =>
        BroadcastCommand.IsSupported(instance.BroadcastCommand)
        && ParseLeadMinutes(instance.AnnounceLeadMinutes).Count > 0
        && !string.IsNullOrWhiteSpace(instance.AnnounceRestartMessage);
}
