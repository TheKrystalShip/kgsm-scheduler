using System.Globalization;
using TheKrystalShip.KGSM.Core.Models;
using TheKrystalShip.KGSM.Core.Scheduling;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// Reads an instance's announcement configuration and decides what, if anything, to say
/// about a maintenance window that is coming.
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
    /// Keeps the leads a window is far enough apart to honour, largest first.
    /// </summary>
    /// <param name="leads">Every configured lead, largest first.</param>
    /// <param name="period">The span between one fire of the window and the next.</param>
    /// <param name="dropped">The leads this window cannot honour, largest first.</param>
    /// <remarks>
    /// ⚠ <b>A lead at or above the window's own period is a false statement waiting to be made.</b>
    /// <see cref="NextMark"/> speaks the smallest mark that has come due, which is the only true
    /// one of several — but that holds because marks come due in descending order, and they only do
    /// so while the period exceeds the largest lead. On a ten-minute window with leads 15, 5 and 1,
    /// the first tick after a fire already has 15 due (the next fire is under fifteen minutes away
    /// from the moment the last one happened), so the server would be told "in 15 minutes" nine
    /// minutes before it happens, every single time. Such a lead is dropped, and the drop is
    /// reported rather than being a silent narrowing of what an operator asked for.
    /// </remarks>
    public static IReadOnlyList<int> ApplicableLeads(
        IReadOnlyList<int> leads, TimeSpan period, out IReadOnlyList<int> dropped)
    {
        if (period <= TimeSpan.Zero)
        {
            dropped = [];
            return leads;
        }

        double minutes = period.TotalMinutes;
        int[] kept = leads.Where(m => m < minutes).ToArray();
        dropped = leads.Where(m => m >= minutes).ToArray();
        return kept;
    }

    /// <summary>
    /// Picks the lead mark to speak now, out of those not yet spoken.
    /// </summary>
    /// <param name="leads">Every applicable lead, in any order.</param>
    /// <param name="alreadyAnnounced">Marks already spoken for this fire.</param>
    /// <param name="minutesUntilFire">How far off the window is.</param>
    /// <param name="due">
    /// When this returns a mark, every mark that has come due — all of which are spent by this
    /// announcement, whether or not they are the one spoken.
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
    /// What a window is about to do to the people on the server, in the words <c>{reason}</c>
    /// resolves to.
    /// </summary>
    /// <param name="tasks">The window's tasks that are both disruptive and permitted to run here.</param>
    /// <returns>The phrase, or <see langword="null"/> for a window nobody needs warning about.</returns>
    /// <remarks>
    /// An update implies the restart that makes it the running build, so a window carrying both is
    /// one sentence rather than two. A window with nothing disruptive left in it is never
    /// announced — there is no true sentence to say about a nightly archive that interrupts nobody.
    /// </remarks>
    public static string? Reason(IReadOnlyCollection<MaintenanceTask> tasks)
    {
        if (tasks.Contains(MaintenanceTask.Update)) return "updating and restarting";
        if (tasks.Contains(MaintenanceTask.Restart)) return "restarting";
        return null;
    }

    /// <summary>
    /// Resolves an announcement message against the window it describes.
    /// </summary>
    /// <remarks>
    /// <c>{minutes}</c> is the lead being announced, <c>{reason}</c> is what the window is about to
    /// do, and <c>{instance}</c> is the label a person reads the server by. The result is what the
    /// engine then substitutes into the game's own broadcast template — a separate step with a
    /// different placeholder, which is why a message containing <c>{message}</c> needs no special
    /// handling here.
    /// <para>
    /// A token with nothing true to put in it is left standing rather than filled with a guess. A
    /// cancellation describes no distance, and a template that asks for one gets the placeholder
    /// back, not a number nothing measured.
    /// </para>
    /// </remarks>
    public static string Resolve(string template, Instance instance, int? minutes, string? reason = null)
    {
        string resolved = template.Replace("{instance}", instance.DisplayName, StringComparison.Ordinal);

        if (reason is not null)
        {
            resolved = resolved.Replace("{reason}", reason, StringComparison.Ordinal);
        }

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
    /// does not write to. The maintenance still happens; it simply happens unannounced.
    /// </remarks>
    public static bool CanAnnounce(Instance instance) =>
        BroadcastCommand.IsSupported(instance.BroadcastCommand)
        && ParseLeadMinutes(instance.AnnounceLeadMinutes).Count > 0
        && !string.IsNullOrWhiteSpace(instance.AnnounceMaintenanceMessage);
}
