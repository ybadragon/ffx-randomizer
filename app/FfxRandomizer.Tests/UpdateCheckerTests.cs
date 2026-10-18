using System.Net;
using FfxRandomizer.Core.Updates;

namespace FfxRandomizer.Tests;

public class UpdateCheckerTests
{
    private static string Release(string tag, bool pre = false, bool draft = false, string? name = null) =>
        $$"""{"tag_name":"{{tag}}","name":{{(name is null ? "null" : $"\"{name}\"")}},"html_url":"https://github.com/r/releases/tag/{{tag}}","prerelease":{{(pre ? "true" : "false")}},"draft":{{(draft ? "true" : "false")}}}""";

    private static string List(params string[] releases) => "[" + string.Join(",", releases) + "]";

    [Fact]
    public void Finds_the_highest_newer_release_including_pre_releases()
    {
        var update = UpdateChecker.Newest(List(Release("v0.1.1", pre: true), Release("v0.3.0", pre: true, name: "Gear"), Release("v0.2.0")), "0.1.1");

        Assert.NotNull(update);
        Assert.Equal("0.3.0", update.Version);
        Assert.Equal("Gear", update.Name);
        Assert.True(update.IsPreRelease);
        Assert.EndsWith("/v0.3.0", update.PageUrl);
    }

    [Theory]
    [InlineData("0.1.1")]
    [InlineData("0.2.0")]
    public void Returns_nothing_when_up_to_date_or_ahead(string current) =>
        Assert.Null(UpdateChecker.Newest(List(Release("v0.1.1"), Release("v0.1.0")), current));

    [Fact]
    public void Ignores_drafts_and_unreadable_tags()
    {
        Assert.Null(UpdateChecker.Newest(List(Release("v9.0.0", draft: true), Release("nightly")), "0.1.1"));
    }

    [Fact]
    public void Compares_numerically_not_as_text()
    {
        Assert.Equal("0.10.0", UpdateChecker.Newest(List(Release("v0.9.0"), Release("v0.10.0")), "0.2.0")?.Version);
        Assert.Equal("0.1.2", UpdateChecker.Newest(List(Release("0.1.2-beta")), "0.1")?.Version);
    }

    [Fact]
    public async Task Network_failures_mean_no_update()
    {
        Assert.Null(await UpdateChecker.CheckAsync("0.1.1", new StubHandler(HttpStatusCode.InternalServerError, "")));
        Assert.Null(await UpdateChecker.CheckAsync("0.1.1", new StubHandler(HttpStatusCode.OK, "<html>not json")));
    }

    [Fact]
    public async Task Sends_a_user_agent_and_reads_the_listing()
    {
        var handler = new StubHandler(HttpStatusCode.OK, List(Release("v0.2.0")));
        var update = await UpdateChecker.CheckAsync("0.1.1", handler);

        Assert.Equal("0.2.0", update?.Version);
        Assert.Contains("FFX-Randomizer", handler.LastRequest!.Headers.UserAgent.ToString());
        Assert.Equal("/repos/ybadragon/ffx-randomizer/releases", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
