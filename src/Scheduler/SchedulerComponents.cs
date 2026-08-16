namespace TheKrystalShip.Kgsm.Scheduler;

/// <summary>
/// The parts of this daemon's job that can stop working while it keeps running.
/// </summary>
/// <remarks>
/// <para>
/// ⚠ <b>This leaf fails more quietly than any other in the ecosystem.</b> Everything it does is
/// something that was supposed to happen and did not: a restart that never fires produces no event, no
/// error and no absence anybody notices, because the only evidence would have been a server going down
/// and coming back. Nothing on this host can tell a scheduler that is idle from one that is broken.
/// </para>
/// <para>
/// Both components are therefore probed on the poll tick rather than discovered when a schedule
/// finally comes due — by then somebody is already waiting for a restart that will not happen.
/// </para>
/// </remarks>
internal static class SchedulerComponents
{
    /// <summary>
    /// The watchdog this daemon dispatches every scheduled restart through.
    /// </summary>
    /// <remarks>
    /// Unreachable means every scheduled restart fails. The schedules are still read, the times still
    /// come due, and the daemon still looks entirely healthy from outside.
    /// </remarks>
    public const string Watchdog = "watchdog";

    /// <summary>
    /// The engine, read for the schedules themselves.
    /// </summary>
    /// <remarks>
    /// Unreadable means this daemon knows of no schedules at all — which is indistinguishable, from
    /// every surface and from its own status socket, from a host where nobody has configured any.
    /// </remarks>
    public const string Kgsm = "kgsm";
}
