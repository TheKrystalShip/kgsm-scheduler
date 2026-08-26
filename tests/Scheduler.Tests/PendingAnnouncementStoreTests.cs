using Microsoft.Extensions.Logging.Abstractions;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// The record of what a server has already been told, which has to outlive the process that
/// told it.
/// </summary>
public sealed class PendingAnnouncementStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("kgsm-sched-pending-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private PendingAnnouncementStore NewStore() =>
        new(_dir, NullLogger.Instance);

    [Fact]
    public void AnEmptyStoreOwesNothing()
    {
        Assert.Null(NewStore().Get("mc-01"));
    }

    [Fact]
    public void WhatWasAnnouncedSurvivesANewStoreOverTheSameDirectory()
    {
        // The whole point: a daemon that restarts mid-countdown must not repeat the warnings it
        // already gave, nor forget that it gave them.
        var fireAt = new DateTimeOffset(2026, 8, 26, 4, 0, 0, TimeSpan.Zero);
        NewStore().Set("mc-01", new PendingAnnouncement(fireAt, [15, 5]));

        var entry = NewStore().Get("mc-01");

        Assert.NotNull(entry);
        Assert.Equal(fireAt, entry.FireAtUtc);
        Assert.Equal([15, 5], entry.AnnouncedLeads);
    }

    [Fact]
    public void ClearingSettlesTheDebt()
    {
        var store = NewStore();
        store.Set("mc-01", new PendingAnnouncement(DateTimeOffset.UtcNow, [15]));
        store.Clear("mc-01");

        Assert.Null(store.Get("mc-01"));
        Assert.Null(NewStore().Get("mc-01"));
    }

    [Fact]
    public void ClearingSomethingNeverRecordedIsANoOp()
    {
        var store = NewStore();
        store.Clear("never-heard-of-it");

        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void OneInstancesRecordDoesNotDisturbAnothers()
    {
        var store = NewStore();
        store.Set("mc-01", new PendingAnnouncement(DateTimeOffset.UtcNow, [15]));
        store.Set("factorio", new PendingAnnouncement(DateTimeOffset.UtcNow, [5, 1]));
        store.Clear("mc-01");

        Assert.Null(store.Get("mc-01"));
        Assert.Equal([5, 1], store.Get("factorio")!.AnnouncedLeads);
    }

    [Fact]
    public void AnUnreadableFileReadsAsNothingAnnouncedRatherThanFailing()
    {
        // This record exists only to be polite about restarts. Refusing to start over a corrupt one
        // would take the whole schedule down; the cost of continuing is at worst a repeated warning.
        File.WriteAllText(Path.Combine(_dir, "pending-announcements.json"), "{ not json");

        var store = NewStore();

        Assert.Empty(store.Snapshot());
        Assert.Null(store.Get("mc-01"));
    }

    [Fact]
    public void AStoreThatCouldNotReadStillWritesCleanly()
    {
        File.WriteAllText(Path.Combine(_dir, "pending-announcements.json"), "{ not json");

        var store = NewStore();
        store.Set("mc-01", new PendingAnnouncement(DateTimeOffset.UtcNow, [15]));

        Assert.Equal([15], NewStore().Get("mc-01")!.AnnouncedLeads);
    }
}
