namespace Matcha.App;

/// <summary>Tracks the visible page separately from the latest navigation intent.</summary>
internal sealed class PageNavigationState
{
    public string CurrentPage { get; private set; } = "messages";
    public string RequestedPage { get; private set; } = "messages";
    public int Revision { get; private set; }
    public bool HasPendingPage => CurrentPage != RequestedPage;

    public void Request(string page)
    {
        page = Normalize(page);
        if (RequestedPage == page) return;
        RequestedPage = page;
        Revision++;
    }

    public PageNavigationChange CommitRequested()
    {
        var change = new PageNavigationChange(CurrentPage, RequestedPage, GetDirection(CurrentPage, RequestedPage));
        CurrentPage = RequestedPage;
        return change;
    }

    public static string Normalize(string page) => page is "people" or "account" or "logs" or "settings" ? page : "messages";

    public static int GetDirection(string from, string to) => Math.Sign(Rank(to) - Rank(from));

    // Visual order in the left pane. The profile row sits above Activity logs and Settings.
    private static int Rank(string page) => Normalize(page) switch
    {
        "people" => 1,
        "account" => 2,
        "logs" => 3,
        "settings" => 4,
        _ => 0,
    };
}

internal readonly record struct PageNavigationChange(string From, string To, int Direction);
