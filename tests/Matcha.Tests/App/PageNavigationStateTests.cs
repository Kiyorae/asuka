using Matcha.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Matcha.Tests.App;

[TestClass]
public sealed class PageNavigationStateTests
{
    [TestMethod]
    public void DirectionsFollowVisibleSidebarOrderInBothDirections()
    {
        string[] pages = ["messages", "people", "account", "logs", "settings"];
        for (var from = 0; from < pages.Length; from++)
        {
            for (var to = 0; to < pages.Length; to++)
            {
                Assert.AreEqual(Math.Sign(to - from), PageNavigationState.GetDirection(pages[from], pages[to]),
                    $"{pages[from]} -> {pages[to]}");
            }
        }
    }

    [TestMethod]
    public void ClickingTheCurrentPageDoesNotCreateAnAnimationOrRevision()
    {
        var navigation = new PageNavigationState();
        navigation.Request("messages");
        Assert.IsFalse(navigation.HasPendingPage);
        Assert.AreEqual(0, navigation.Revision);
        Assert.AreEqual(new PageNavigationChange("messages", "messages", 0), navigation.CommitRequested());
    }

    [TestMethod]
    public void RequestsDuringExitCoalesceToTheLastSelectedPage()
    {
        var navigation = new PageNavigationState();
        navigation.Request("people");
        navigation.Request("logs");
        navigation.Request("settings");
        Assert.AreEqual("messages", navigation.CurrentPage);
        Assert.AreEqual(new PageNavigationChange("messages", "settings", 1), navigation.CommitRequested());
        Assert.IsFalse(navigation.HasPendingPage);
    }

    [TestMethod]
    public void ReversingBeforeTheExitFinishesKeepsTheVisiblePage()
    {
        var navigation = new PageNavigationState();
        navigation.Request("settings");
        navigation.Request("messages");
        Assert.IsFalse(navigation.HasPendingPage);
        Assert.AreEqual(new PageNavigationChange("messages", "messages", 0), navigation.CommitRequested());
        Assert.AreEqual(2, navigation.Revision);
    }

    [TestMethod]
    public void AnEntranceInterruptedByAnotherRequestUsesTheCommittedPageAsItsSource()
    {
        var navigation = new PageNavigationState();
        navigation.Request("logs");
        navigation.CommitRequested();
        navigation.Request("settings");
        navigation.Request("people");
        Assert.AreEqual("logs", navigation.CurrentPage);
        Assert.AreEqual(new PageNavigationChange("logs", "people", -1), navigation.CommitRequested());
    }

    [TestMethod]
    public void ReturningToTheEnteringPageCancelsAnObsoletePendingDestination()
    {
        var navigation = new PageNavigationState();
        navigation.Request("people");
        navigation.CommitRequested();
        navigation.Request("settings");
        navigation.Request("people");
        Assert.IsFalse(navigation.HasPendingPage);
        Assert.AreEqual("people", navigation.RequestedPage);
    }

    [TestMethod]
    public void RepeatedRequestsDoNotDuplicateAQueuedTransition()
    {
        var navigation = new PageNavigationState();
        navigation.Request("account");
        navigation.Request("account");
        Assert.AreEqual(1, navigation.Revision);
        Assert.AreEqual(new PageNavigationChange("messages", "account", 1), navigation.CommitRequested());
        navigation.Request("account");
        Assert.IsFalse(navigation.HasPendingPage);
        Assert.AreEqual(1, navigation.Revision);
    }

    [TestMethod]
    public void NavigationRevisionRecordsEarlierNavigationEvenAfterReturningToMessages()
    {
        var navigation = new PageNavigationState();
        navigation.Request("settings");
        navigation.CommitRequested();
        navigation.Request("messages");
        navigation.CommitRequested();
        Assert.AreEqual("messages", navigation.CurrentPage);
        Assert.AreEqual(2, navigation.Revision);
    }

    [TestMethod]
    public void UnknownPageFallsBackToMessagesConsistently()
    {
        var navigation = new PageNavigationState();
        navigation.Request("settings");
        navigation.CommitRequested();
        navigation.Request("missing");
        Assert.AreEqual(new PageNavigationChange("settings", "messages", -1), navigation.CommitRequested());
    }
}
