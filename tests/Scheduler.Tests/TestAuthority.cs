using Microsoft.Extensions.Logging.Abstractions;

using TheKrystalShip.KGSM;
using TheKrystalShip.Auth;
using TheKrystalShip.Auth.Access;
using TheKrystalShip.Auth.Cluster;
using TheKrystalShip.Auth.Users;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// A test's stand-in for the auth anchor and the node's replica: an authority store of its own, the node
/// reporting the engine's actions and this daemon's requirements — which is what creates and approves
/// the scheduler's service account — and the snapshot delivered into a replica file after every change.
/// </summary>
/// <remarks>
/// Real files rather than a substitute, so what is pinned is that the daemon evaluates the rows the anchor
/// writes, through the reader it runs with.
/// </remarks>
internal sealed class TestAuthority : IDisposable
{
    /// <summary>The node the daemon under test sits on.</summary>
    public const string Node = "walter";

    private static readonly TimeSpan Bound = TimeSpan.FromDays(1);

    /// <summary>What this daemon requires, as its build declares it.</summary>
    public static readonly string[] Required =
    [
        KgsmActions.ServerRead, KgsmActions.ServerBackupsCreate, KgsmActions.ServerBackupsManage,
        KgsmActions.ServerUpdate, KgsmActions.ServerRestart, KgsmActions.ServerAnnounce,
    ];

    private readonly string _dir = Directory.CreateTempSubdirectory("kgsm-sched-authority").FullName;
    private readonly SqliteAuthorityStore _anchor;
    private readonly SqliteAuthorityStore _replica;
    private readonly string _root;

    /// <param name="requires">What the node reports this daemon requires; every requirement by default.</param>
    public TestAuthority(IEnumerable<string>? requires = null)
    {
        ReplicaPath = Path.Combine(_dir, "users.db");
        _anchor = new SqliteAuthorityStore(new UserStoreOptions { Path = Path.Combine(_dir, "anchor.db") });
        _replica = new SqliteAuthorityStore(new UserStoreOptions { Path = ReplicaPath });

        DateTimeOffset now = DateTimeOffset.UtcNow;
        _root = Run(async () =>
        {
            var root = new KgsmIdentity("local", "root", "root", "root", null, []);
            KgsmUser account = (await new IdentityLinkService(_anchor)
                .ProvisionAsync(root, AccountOrigin.Admitted, UserStatus.Active, now)).User!;
            await _anchor.GrantOwnerLocallyAsync(account.Username, "local:test", now);

            await _anchor.RecordMemberReportAsync(Node, new MemberCatalogReport(false,
            [
                new ActionManifest(1, "kgsm", "test",
                    [.. Required.Select(a => new ManifestAction(a["kgsm:".Length..], a, "execute", "instance", null))],
                    []),
                new ActionManifest(1, "scheduler", "test", [],
                    [.. (requires ?? Required).Select(a => new ManifestRequirement(a, "instance", "test"))]),
            ], 1), now);

            await DeliverAsync(now);
            return account.UserId;
        });
    }

    /// <summary>The node's replica file.</summary>
    public string ReplicaPath { get; }

    /// <summary>A person's account, active and holding no role. Returns its id.</summary>
    public string Person(string username)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return Run(async () =>
        {
            var account = new KgsmUser(UserIds.NewUserId(), username, username, AccountOrigin.Admitted,
                UserStatus.Active, now, now);
            await _anchor.CreateAsync(account);
            await DeliverAsync(now);
            return account.UserId;
        });
    }

    /// <summary>Grant <paramref name="accountId"/> each of <paramref name="actions"/> at <paramref name="scope"/>.</summary>
    public void Grant(string accountId, AccessScope scope, params string[] actions)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Run(async () =>
        {
            string name = "grant-" + Guid.NewGuid().ToString("N")[..8];
            string permission = (await EditAsync(new CreatePermission(name), now)).CreatedId!;
            await EditAsync(new SetPermissionActions(permission, new HashSet<string>(actions, StringComparer.Ordinal)), now);
            string role = (await EditAsync(new CreateRole(name), now)).CreatedId!;
            await EditAsync(new SetRolePermissions(role, new HashSet<string> { permission }), now);
            await EditAsync(new Assign(accountId, role, scope), now);
            await DeliverAsync(now);
            return true;
        });
    }

    /// <summary>This daemon's access over the replica, on <paramref name="node"/>.</summary>
    public AutomationAccess Access(string? node = Node) =>
        new(new MemberAccess(new AuthorityReplicaFile(ReplicaPath, NullLogger<AuthorityReplicaFile>.Instance)),
            "scheduler", () => node);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private async Task<AuthorityWrite> EditAsync(AuthorityEdit edit, DateTimeOffset now) =>
        await _anchor.ApplyAsync(_root, edit, await _anchor.VersionAsync(), now);

    private async Task DeliverAsync(DateTimeOffset now) =>
        await _replica.ApplySnapshotAsync(await _anchor.ExportAsync(Bound, now), now);

    private static T Run<T>(Func<Task<T>> work) => work().GetAwaiter().GetResult();
}
