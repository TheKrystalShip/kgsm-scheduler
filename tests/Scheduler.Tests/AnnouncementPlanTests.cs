using TheKrystalShip.KGSM.Core.Models;

namespace TheKrystalShip.Kgsm.Scheduler.Tests;

/// <summary>
/// What a server says before a scheduled restart, and — mostly — what it declines to say.
/// </summary>
public class AnnouncementPlanTests
{
    // --- reading the configured lead times ------------------------------------------------

    [Fact]
    public void ParseLeadMinutes_ReadsThemLargestFirst()
    {
        Assert.Equal([15, 5, 1], AnnouncementPlan.ParseLeadMinutes("1,15,5"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseLeadMinutes_AnEmptyValueAnnouncesNothing(string? configured)
    {
        Assert.Empty(AnnouncementPlan.ParseLeadMinutes(configured));
    }

    [Fact]
    public void ParseLeadMinutes_DropsWhatCannotBeAMinute()
    {
        // Hand-edited configuration. One bad entry silencing an instance's other lead times would
        // be a worse answer than honouring the ones that parse.
        Assert.Equal([15, 5], AnnouncementPlan.ParseLeadMinutes("15, soon, 5, -3, 0"));
    }

    [Fact]
    public void ParseLeadMinutes_CollapsesDuplicates()
    {
        Assert.Equal([5], AnnouncementPlan.ParseLeadMinutes("5,5,5"));
    }

    // --- choosing the mark to speak -------------------------------------------------------

    [Fact]
    public void NextMark_SaysNothingWhileTheRestartIsFurtherOffThanEveryLead()
    {
        Assert.Null(AnnouncementPlan.NextMark([15, 5, 1], [], minutesUntilFire: 40, out var due));
        Assert.Empty(due);
    }

    [Fact]
    public void NextMark_SpeaksAMarkAsItComesDue()
    {
        Assert.Equal(15, AnnouncementPlan.NextMark([15, 5, 1], [], minutesUntilFire: 14.8, out _));
    }

    [Fact]
    public void NextMark_SaysNothingTwiceAboutTheSameMark()
    {
        Assert.Null(AnnouncementPlan.NextMark([15, 5, 1], [15], minutesUntilFire: 14.5, out var due));
        Assert.Empty(due);
    }

    [Fact]
    public void NextMark_MovesOnToTheNextMark()
    {
        Assert.Equal(5, AnnouncementPlan.NextMark([15, 5, 1], [15], minutesUntilFire: 4.9, out _));
    }

    [Fact]
    public void NextMark_SpeaksOnlyTheSmallestOfSeveralOverdueMarks()
    {
        // A daemon that was down arrives to find 15, 5 and 1 all passed. Saying "fifteen minutes"
        // when the restart is one minute away is the one thing this must never do.
        int? mark = AnnouncementPlan.NextMark([15, 5, 1], [], minutesUntilFire: 0.7, out var due);

        Assert.Equal(1, mark);
        Assert.Equal([15, 5, 1], due.OrderByDescending(m => m));
    }

    [Fact]
    public void NextMark_SpendsEveryOverdueMarkEvenThoughOnlyOneIsSpoken()
    {
        // The marks it passed over are gone, not queued: they describe distances that are no longer
        // true, and speaking them on later ticks would count the restart upward.
        AnnouncementPlan.NextMark([15, 5, 1], [], minutesUntilFire: 0.7, out var due);

        Assert.Null(AnnouncementPlan.NextMark([15, 5, 1], due, minutesUntilFire: 0.5, out _));
    }

    // --- resolving the message ------------------------------------------------------------

    [Fact]
    public void Resolve_SubstitutesTheLeadAndTheLabel()
    {
        var instance = new Instance { Name = "mc-01", DisplayName = "Survival" };

        Assert.Equal(
            "Survival restarts in 5 min",
            AnnouncementPlan.Resolve("{instance} restarts in {minutes} min", instance, 5));
    }

    [Fact]
    public void Resolve_LeavesTheLeadAloneWhenThereIsNoneToState()
    {
        // A cancellation describes no distance, so a template carrying {minutes} keeps it rather
        // than being handed a number that would be a fabrication.
        var instance = new Instance { Name = "mc-01", DisplayName = "Survival" };

        Assert.Equal(
            "Survival: restart cancelled ({minutes})",
            AnnouncementPlan.Resolve("{instance}: restart cancelled ({minutes})", instance, minutes: null));
    }

    [Fact]
    public void Resolve_LeavesTheGamesOwnPlaceholderAlone()
    {
        // {message} belongs to the engine's substitution into the blueprint template, which happens
        // after this one. Two steps, different placeholders, different owners.
        var instance = new Instance { Name = "mc-01", DisplayName = "Survival" };

        Assert.Equal("say {message} in 5", AnnouncementPlan.Resolve("say {message} in {minutes}", instance, 5));
    }

    // --- whether this instance can be announced to at all ---------------------------------

    [Fact]
    public void CanAnnounce_WhenTheGameDeclaresACommandAndTheInstanceDeclaresLeads()
    {
        Assert.True(AnnouncementPlan.CanAnnounce(new Instance
        {
            BroadcastCommand = "say {message}",
            AnnounceLeadMinutes = "15,5",
            AnnounceRestartMessage = "Restart in {minutes} min",
        }));
    }

    [Fact]
    public void CanAnnounce_IsFalseForAGameWithNoBroadcastCommand()
    {
        // Several games have no console command surface, and several more expose one only on a
        // channel the engine does not write to. The restart still happens, unannounced.
        Assert.False(AnnouncementPlan.CanAnnounce(new Instance
        {
            BroadcastCommand = "",
            AnnounceLeadMinutes = "15,5",
            AnnounceRestartMessage = "Restart in {minutes} min",
        }));
    }

    [Fact]
    public void CanAnnounce_IsFalseWithNoLeadTimes()
    {
        // The default. A server addressing the people on it is opt-in.
        Assert.False(AnnouncementPlan.CanAnnounce(new Instance
        {
            BroadcastCommand = "say {message}",
            AnnounceLeadMinutes = "",
            AnnounceRestartMessage = "Restart in {minutes} min",
        }));
    }

    [Fact]
    public void CanAnnounce_IsFalseWithNothingToSay()
    {
        Assert.False(AnnouncementPlan.CanAnnounce(new Instance
        {
            BroadcastCommand = "say {message}",
            AnnounceLeadMinutes = "15",
            AnnounceRestartMessage = "   ",
        }));
    }
}
