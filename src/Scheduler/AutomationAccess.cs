using TheKrystalShip.KGSM.Auth.Access;
using TheKrystalShip.KGSM.Auth.Cluster;
using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>Whether an automation may do something now, and why not when it may not.</summary>
/// <param name="Allowed">Whether it may.</param>
/// <param name="Reason">Why not, in the words the status socket and the record carry. Null when allowed.</param>
internal readonly record struct AutomationVerdict(bool Allowed, string? Reason)
{
    public static AutomationVerdict Allow { get; } = new(true, null);

    public static AutomationVerdict Block(string reason) => new(false, reason);
}

/// <summary>
/// Whether this daemon may do something it was switched on to do: both its own service account and
/// the person who switched it on must hold every action it performs, at the server, at that moment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Author ∩ service, at every firing.</b> Writing a window or switching on a sweep never lends its
/// author this daemon's reach — a window restarts only what its author could restart by hand — and an
/// author who loses access, is disabled or is deleted stops what they switched on at the next firing.
/// Nobody recorded as the author stops it the same way.
/// </para>
/// <para>
/// <b>The service account is derived, never configured:</b> <c>svc:scheduler@&lt;node&gt;</c>, the node
/// being the member id the node on this machine writes into its host file. A setting naming it would
/// let whoever can edit this daemon's configuration point it at a more powerful account.
/// </para>
/// <para>
/// <b>"Cannot tell" blocks too.</b> A replica that cannot be read, a node that has not named itself, a
/// service account not yet created: each is a reason nothing runs, reported as itself. Running on a
/// guess is the one thing an automation must not do.
/// </para>
/// </remarks>
internal sealed class AutomationAccess(MemberAccess access, Func<string?> node)
{
    /// <summary>The component half of this daemon's service account.</summary>
    public const string Component = "scheduler";

    /// <summary>
    /// Whether this daemon may perform every one of <paramref name="actions"/> at the server
    /// <paramref name="name"/> for <paramref name="author"/>. A blank author is nobody.
    /// </summary>
    public async Task<AutomationVerdict> DecideAsync(
        IEnumerable<string> actions, string name, Instance instance, string? author, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(author))
            author = null;

        if (node() is not { Length: > 0 } nodeId)
        {
            return AutomationVerdict.Block(
                "this node has not said which it is, so the scheduler's own account cannot be found");
        }

        if (await access.EvaluatorAsync(ct).ConfigureAwait(false) is not { } evaluator)
        {
            return AutomationVerdict.Block(
                "this node's copy of the cluster's accounts could not be read"
                + (access.UnavailableReason is { } why ? $" ({why})" : string.Empty));
        }

        ServiceIdentity service = new(Component, nodeId);
        string? serviceAccount = evaluator.Snapshot.Accounts.Values
            .FirstOrDefault(a => a.Service == service)?.AccountId;
        if (serviceAccount is null)
        {
            return AutomationVerdict.Block(
                $"the scheduler has no account on {nodeId} yet; it is made when the node reports what the "
                + "scheduler requires");
        }

        // At the install, named by its nonce, so a grant on a server removed and installed again under
        // the same name does not carry across. A nonce that cannot be read is the node, where an
        // instance grant does not reach.
        AccessScope target = instance.InstallNonce is { Length: > 0 } nonce
            ? AccessScope.ForInstance(nodeId, name, nonce)
            : AccessScope.ForNode(nodeId);

        foreach (string action in actions)
        {
            AccessDecision decision = evaluator.AllowsAutomation(serviceAccount, author, action, target);
            if (!decision.Allowed)
                return AutomationVerdict.Block(Explain(decision, action, author, evaluator.Snapshot));
        }

        return AutomationVerdict.Allow;
    }

    private static string Explain(AccessDecision decision, string action, string? author, AuthoritySnapshot snapshot)
    {
        string who = author is not null && snapshot.Accounts.TryGetValue(author, out AccessAccount? account)
            ? account.Name
            : "the account that set it up, which no longer exists";

        return decision.Reason switch
        {
            DenyReason.NoAuthor =>
                "nobody is recorded as having set this up; saving it again makes whoever saves it its author",
            DenyReason.AuthorDenied => $"{who} set this up and may not {action} here any more",
            DenyReason.Stale => "this node's copy of the cluster's accounts is out of date",
            DenyReason.UnknownAction => $"nothing in this cluster declares {action}, so only an Owner holds it",
            DenyReason.ContractOutdated => "this scheduler is older than the cluster's accounts allow",
            _ => $"the scheduler's own account does not hold {action} here",
        };
    }
}
