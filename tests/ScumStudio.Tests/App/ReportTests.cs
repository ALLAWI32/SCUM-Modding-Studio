using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using ScumStudio.App.Services;
using ScumStudio.App.ViewModels;
using ScumStudio.Core;

namespace ScumStudio.Tests.App;

/// <summary>
/// "Report a problem" (owner: problems come by Discord DM in any language; the app sends them to the owner's Discord in
/// English, as the app). The payload, the translation parsing, the webhook call with a fake network, the rate limit and
/// a build without a webhook.
/// </summary>
public sealed class ReportTests
{
    private const string Webhook = "https://discord.com/api/webhooks/1/abc";

    [Fact]
    public void ThePayloadIsTheAppWithAnEmbedAndTheOriginalText()
    {
        var at = new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);
        var payload = ReportService.BuildPayload(ReportCategory.Bug, "La porte ne s'ouvre pas.\nDeuxième ligne", ("The door does not open.\nSecond line", "fr"),
            [("Version", "0.2.7"), ("Log", "```\nline\n```")], at);
        Assert.Equal(CoreInfo.ProductName, payload["username"]!.GetValue<string>());
        Assert.Equal(ReportService.AvatarUrl, payload["avatar_url"]!.GetValue<string>());
        var embed = payload["embeds"]![0]!;
        Assert.Equal("Bug: The door does not open.", embed["title"]!.GetValue<string>());
        Assert.Equal("The door does not open.\nSecond line", embed["description"]!.GetValue<string>());
        var fields = embed["fields"]!.AsArray().Select(f => (f!["name"]!.GetValue<string>(), f["value"]!.GetValue<string>(), f["inline"]!.GetValue<bool>())).ToList();
        Assert.Equal(("Original (fr)", "La porte ne s'ouvre pas.\nDeuxième ligne", false), fields[0]);
        Assert.Equal(("Version", "0.2.7", true), fields[1]);
        Assert.Equal(("Log", "```\nline\n```", false), fields[2]);
        Assert.Equal("2026-10-06T12:00:00.0000000+00:00", embed["timestamp"]!.GetValue<string>());
        Assert.Empty(payload["allowed_mentions"]!["parse"]!.AsArray()); // a report can never ping anyone

        // Written in English already: no "Original" field.
        var english = ReportService.BuildPayload(ReportCategory.Idea, "Add a thing", ("Add a thing", "en"), [], at);
        Assert.Empty(english["embeds"]![0]!["fields"]!.AsArray());
    }

    [Fact]
    public void ATranslateAnswerIsRead()
    {
        using var document = JsonDocument.Parse("""[[["The door ","La porte ",null,null,10],["does not open.","ne s'ouvre pas.",null,null,3]],null,"fr",null,null,null,0.9]""");
        var (english, language) = ReportService.ParseTranslation(document.RootElement)!.Value;
        Assert.Equal("The door does not open.", english);
        Assert.Equal("fr", language);
        using var empty = JsonDocument.Parse("[]");
        Assert.Null(ReportService.ParseTranslation(empty.RootElement));
    }

    [Fact]
    public async Task AReportIsTranslatedPostedAndRateLimited()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var network = new FakeNetwork();
        var service = new ReportService(NullLogger.Instance, () => network, clock, Webhook);
        Assert.True(service.IsAvailable);

        var result = await service.SendAsync(ReportCategory.Question, "Wie kopiere ich ein Haus?", [("Version", "0.2.7")]);
        Assert.True(result.Sent);
        Assert.True(result.Translated);
        Assert.Equal(2, network.Requests.Count);
        Assert.StartsWith("https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl=en", network.Requests[0].Url, StringComparison.Ordinal);
        Assert.Equal(Webhook, network.Requests[1].Url);
        var posted = JsonNode.Parse(network.Requests[1].Body!)!;
        Assert.Equal("Question: How do I copy a house?", posted["embeds"]![0]!["title"]!.GetValue<string>());
        Assert.Contains(posted["embeds"]![0]!["fields"]!.AsArray(), f => f!["name"]!.GetValue<string>() == "Original (de)");

        // A second one right away waits (the address is in every copy of the app).
        var again = await service.SendAsync(ReportCategory.Bug, "Noch ein Problem hier", []);
        Assert.False(again.Sent);
        Assert.Contains("wait", again.Error, StringComparison.OrdinalIgnoreCase);
        clock.Now += TimeSpan.FromSeconds(61);
        Assert.True((await service.SendAsync(ReportCategory.Bug, "Noch ein Problem hier", [])).Sent);

        // The translation service down: the report goes as written.
        network.TranslateFails = true;
        clock.Now += TimeSpan.FromSeconds(61);
        var untranslated = await service.SendAsync(ReportCategory.Bug, "Noch ein Problem hier", []);
        Assert.True(untranslated.Sent);
        Assert.False(untranslated.Translated);
        Assert.Equal("Noch ein Problem hier", JsonNode.Parse(network.Requests[^1].Body!)!["embeds"]![0]!["description"]!.GetValue<string>());

        // Too short, and a build without the address.
        Assert.False((await service.SendAsync(ReportCategory.Bug, "short", [])).Sent);
        var none = new ReportService(NullLogger.Instance, () => network, clock, string.Empty);
        Assert.False(none.IsAvailable);
        Assert.False((await none.SendAsync(ReportCategory.Bug, "A long enough report text", [])).Sent);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void TheSupportCardOpensAndSendsOnlyARealReport()
    {
        using var ctx = AppTestContext.Create(inline: false);
        var (window, vm) = HeadlessUi.ShowMainWindow(ctx.Services, 1280, 800);
        try
        {
            Assert.Equal("https://www.paypal.me/ALLAWI34SGWTG121", MainWindowViewModel.PayPalUrl);
            vm.OpenSupportCommand.Execute(null);
            HeadlessUi.Pump();
            Assert.True(vm.IsSupportOpen);
            Assert.NotNull(vm.SelectedReportCategory);
            Assert.Equal(3, vm.ReportCategories.Count);
            Assert.False(vm.SendReportCommand.CanExecute(null));
            vm.ReportText = "A tree I deleted is still in the game.";
            Assert.Equal(vm.ReportText.Length + " / 1500", vm.ReportLengthText);
            Assert.Equal(ctx.Services.Reports.IsAvailable, vm.SendReportCommand.CanExecute(null));
            Assert.True(window.GetVisualDescendants().OfType<Avalonia.Controls.Button>().Single(b => b.Name == "SupportButton").IsVisible);
            HeadlessUi.SaveScreenshot(window, "support-card");
            vm.CloseSupportCommand.Execute(null);
            Assert.False(vm.IsSupportOpen);
        }
        finally
        {
            window.Close();
            vm.Dispose();
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Answers the translate call with a canned German → English answer and the webhook with 204.</summary>
    private sealed class FakeNetwork : HttpMessageHandler
    {
        public List<(string Url, string? Body)> Requests { get; } = [];

        public bool TranslateFails { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.ToString(), body));
            if (request.RequestUri.Host.Contains("translate", StringComparison.Ordinal))
            {
                return TranslateFails
                    ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent("""[[["How do I copy a house?","Wie kopiere ich ein Haus?",null,null,10]],null,"de"]"""),
                    };
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
