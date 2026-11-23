using System.Text.Json;
using Apparatus.Core.Assistant;

namespace Apparatus.Core.Tests;

/// <summary>The reference questions used to judge a real provider. Here only the file itself is checked: its expectations must be reachable.</summary>
public class AssistantEvalTests
{
    private sealed record Case(string Q, string Tool, string? Uri, string? Screen);

    private static List<Case> Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "assistant-eval.json")));
        return doc.RootElement.GetProperty("cases").EnumerateArray().Select(e => new Case(
            e.GetProperty("q").GetString()!,
            e.GetProperty("tool").GetString()!,
            e.TryGetProperty("uri", out var u) ? u.GetString() : null,
            e.TryGetProperty("screen", out var s) ? s.GetString() : null)).ToList();
    }

    [Fact]
    public void EveryExpectation_PointsToSomethingTheAssistantCanReallyDo()
    {
        var catalog = SettingsCatalog.LoadEmbedded();
        var tools = new[]
        {
            AssistantTools.OpenWindowsSetting, AssistantTools.OpenApparatusScreen, AssistantTools.OpenLauncher, AssistantTools.OpenUrl,
            AssistantTools.RunMyAction, AssistantTools.ProposeAction, AssistantTools.ProposeSnippet, "none",
        };
        var cases = Load();
        Assert.True(cases.Count >= 20);
        Assert.Equal(cases.Count, cases.Select(c => c.Q).Distinct().Count());

        foreach (var c in cases)
        {
            Assert.Contains(c.Tool, tools);
            if (c.Tool == AssistantTools.OpenWindowsSetting)
                Assert.True(catalog.Find(c.Uri) is not null, $"“{c.Q}”: o catálogo não tem {c.Uri}");
            if (c.Tool == AssistantTools.OpenApparatusScreen)
                Assert.Contains(AssistantTools.Screens, s => s.Id == c.Screen);
        }
    }
}
