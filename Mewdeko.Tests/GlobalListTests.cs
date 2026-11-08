using DataModel;
using Mewdeko.Modules.GlobalList.Common;

namespace Mewdeko.Tests;

/// <summary>
///     Tests for the pure rules behind the global list.
/// </summary>
[TestFixture]
public class GlobalListTests
{
    private static GlobalListEntry Entry(GlobalListStatus status)
    {
        return new GlobalListEntry
        {
            Id = 1, UserId = 42, Status = status
        };
    }

    /// <summary>
    ///     A community is blocked at 5 rejections only when the rate is over 60%.
    /// </summary>
    [TestCase(0, 0, false)]
    [TestCase(0, 4, false)]
    [TestCase(0, 5, true)]
    [TestCase(2, 3, false)]
    [TestCase(3, 5, true)]
    [TestCase(4, 6, false)]
    [TestCase(4, 7, true)]
    [TestCase(10, 5, false)]
    public void StandingBlocksOnlyOverTheRate(int approved, int rejected, bool blocked)
    {
        Assert.That(GlobalListRules.IsBlocked(approved, rejected), Is.EqualTo(blocked));
    }

    /// <summary>
    ///     The daily cap is 20 reports.
    /// </summary>
    [TestCase(0, false)]
    [TestCase(19, false)]
    [TestCase(20, true)]
    [TestCase(35, true)]
    public void DailyCapStopsAtTwenty(int used, bool over)
    {
        Assert.That(GlobalListRules.IsOverDailyCap(used), Is.EqualTo(over));
    }

    /// <summary>
    ///     A user without an open entry gets a new one.
    /// </summary>
    [Test]
    public void NoOpenEntryMakesANewOne()
    {
        Assert.That(GlobalListRules.Route(null, false), Is.EqualTo(ReportRoute.NewEntry));
    }

    /// <summary>
    ///     A second report on a pending entry is merged into it.
    /// </summary>
    [Test]
    public void ReportsOnAPendingEntryMerge()
    {
        Assert.That(GlobalListRules.Route(Entry(GlobalListStatus.Pending), false),
            Is.EqualTo(ReportRoute.MergeIntoPending));
    }

    /// <summary>
    ///     The same community cannot add a second report to one entry.
    /// </summary>
    [Test]
    public void ACommunityReportsAnEntryOnce()
    {
        Assert.That(GlobalListRules.Route(Entry(GlobalListStatus.Pending), true),
            Is.EqualTo(ReportRoute.AlreadyReported));
    }

    /// <summary>
    ///     A live entry takes no new reports.
    /// </summary>
    [Test]
    public void LiveEntriesTakeNoReports()
    {
        Assert.That(GlobalListRules.Route(Entry(GlobalListStatus.Live), false), Is.EqualTo(ReportRoute.AlreadyListed));
    }

    /// <summary>
    ///     A report needs a message link or a screenshot.
    /// </summary>
    [Test]
    public void EvidenceNeedsALinkOrAScreenshot()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GlobalListRules.HasEvidence([], 0), Is.False);
            Assert.That(GlobalListRules.HasEvidence(["https://fluxer.app/channels/1/2/3"], 0), Is.True);
            Assert.That(GlobalListRules.HasEvidence([], 1), Is.True);
        });
    }

    /// <summary>
    ///     Only message links count as links.
    /// </summary>
    [Test]
    public void OnlyMessageLinksAreKept()
    {
        var links = GlobalListRules.CleanLinks([
            "https://fluxer.app/channels/1/2/3", " https://fluxer.app/channels/1/2/3 ", "https://example.com",
            "not a link", "https://fluxer.app/channels/1/2"
        ]);
        Assert.That(links, Is.EqualTo(new[] { "https://fluxer.app/channels/1/2/3" }));
    }

    /// <summary>
    ///     Links in free text are found.
    /// </summary>
    [Test]
    public void LinksAreFoundInText()
    {
        var links = GlobalListRules.FindMessageLinks(
            "scam here https://fluxer.app/channels/1/2/3 and https://fluxer.app/channels/1/2/4.");
        Assert.That(links, Has.Count.EqualTo(2));
    }

    /// <summary>
    ///     The reporting community and a community that voted cannot vote.
    /// </summary>
    [Test]
    public void ReportersAndEarlierVotersCannotVote()
    {
        Assert.Multiple(() =>
        {
            Assert.That(GlobalListRules.CanVote([1UL], [], 2UL), Is.True);
            Assert.That(GlobalListRules.CanVote([1UL], [], 1UL), Is.False);
            Assert.That(GlobalListRules.CanVote([1UL], [2UL], 2UL), Is.False);
        });
    }

    /// <summary>
    ///     Owners and appointed reviewers can review, anyone else cannot.
    /// </summary>
    [TestCase(true, false, true)]
    [TestCase(false, true, true)]
    [TestCase(true, true, true)]
    [TestCase(false, false, false)]
    public void OnlyOwnersAndReviewersCanReview(bool owner, bool reviewer, bool allowed)
    {
        Assert.That(GlobalListRules.CanReview(owner, reviewer), Is.EqualTo(allowed));
    }
}
