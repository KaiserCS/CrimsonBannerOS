using RedStarOS.Services;
using Xunit;

namespace RedStarOS.Tests;

public class BrowserDataTests
{
    [Fact]
    public void Address_bar_text_becomes_a_url_or_a_search()
    {
        Assert.Equal("https://example.com", WebTab.Normalize("example.com").TrimEnd('/'));
        Assert.StartsWith("https://duckduckgo.com/?q=", WebTab.Normalize("how tall is everest", "https://duckduckgo.com/?q={0}"));
        Assert.Equal("http://localhost:8080", WebTab.Normalize("localhost:8080").TrimEnd('/'));
    }

    [Fact]
    public void Blocked_sites_match_subdomains()
    {
        BrowserData.Settings.BlockedSites = "example.com\nbad.org";
        Assert.True(BrowserData.IsBlockedSite("https://www.example.com/page"));
        Assert.True(BrowserData.IsBlockedSite("https://bad.org"));
        Assert.False(BrowserData.IsBlockedSite("https://notexample.com"));
        BrowserData.Settings.BlockedSites = "";
    }
}
