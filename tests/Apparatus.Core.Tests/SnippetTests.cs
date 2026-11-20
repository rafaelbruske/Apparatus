using System.Globalization;
using Apparatus.Core.Search;
using Apparatus.Core.Snippets;

namespace Apparatus.Core.Tests;

public class SnippetTemplateTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 14, 5, 9);   // a Friday

    private static SnippetContext Ctx(string? clipboard = null) =>
        new(Now, clipboard, "rafael", "PC-01", CultureInfo.GetCultureInfo("pt-BR"));

    private static string Render(string template, string? clipboard = null, Dictionary<string, string>? fields = null) =>
        SnippetTemplate.Parse(template).Render(Ctx(clipboard), fields).Text;

    [Theory]
    [InlineData("{date}", "20/11/2026")]
    [InlineData("{time}", "14:05")]
    [InlineData("{datetime}", "20/11/2026 14:05")]
    [InlineData("{date:yyyy-MM-dd}", "2026-11-20")]
    [InlineData("{time:HH:mm:ss}", "14:05:09")]
    [InlineData("{date+7d}", "27/11/2026")]
    [InlineData("{date-1d:dd/MM}", "19/11")]
    [InlineData("{date+1w:dd/MM/yyyy}", "27/11/2026")]
    [InlineData("{date+1m:dd/MM/yyyy}", "20/12/2026")]
    [InlineData("{date+1y:yyyy}", "2027")]
    [InlineData("{time+2h}", "16:05")]
    [InlineData("{time-30min}", "13:35")]
    [InlineData("{date:dddd}", "sexta-feira")]
    [InlineData("{date:MMMM}", "novembro")]
    [InlineData("{DATE}", "20/11/2026")]
    public void DatesAndTimes(string template, string expected) => Assert.Equal(expected, Render(template));

    [Fact]
    public void SimpleVariables()
    {
        Assert.Equal("copiado!", Render("{clipboard}", "copiado!"));
        Assert.Equal("", Render("{clipboard}"));
        Assert.Equal("rafael@PC-01", Render("{user}@{computer}"));
        Assert.Matches(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", Render("{uuid}"));
    }

    [Fact]
    public void Clipboard_NewlinesAreNormalised() =>
        Assert.Equal("a\nb\nc", Render("{clipboard}", "a\r\nb\rc"));

    [Fact]
    public void EscapedBraces_AreLiteral() =>
        Assert.Equal("{date} e }", Render("{{date}} e }}"));

    [Theory]
    [InlineData("{nope}")]
    [InlineData("{field:}")]
    [InlineData("{choice:X=}")]
    [InlineData("{date:")]
    [InlineData("texto { solto")]
    public void UnknownOrBroken_StayAsTyped(string template) => Assert.Equal(template, Render(template));

    [Fact]
    public void BadDateFormat_DoesNotThrow() =>
        Assert.Equal("{date}", Render("{date:%}"));   // a lone '%' is an invalid .NET format string

    [Fact]
    public void Fields_AreCollected_InOrder_AndDeduplicated()
    {
        var t = SnippetTemplate.Parse("Olá {field:Nome}, {field:Assunto=Reunião} — {field:nome} / {choice:Tom=formal|informal}");
        Assert.Equal(new[] { "Nome", "Assunto", "Tom" }, t.Fields.Select(f => f.Label).ToArray());
        Assert.Equal("Reunião", t.Fields[1].Default);
        Assert.True(t.Fields[2].IsChoice);
        Assert.Equal(new[] { "formal", "informal" }, t.Fields[2].Options.ToArray());
        Assert.Equal("formal", t.Fields[2].Default);
    }

    [Fact]
    public void Fields_AreFilled_CaseInsensitively_OrFallBackToDefault()
    {
        var t = SnippetTemplate.Parse("{field:Nome} / {field:Assunto=Reunião} / {field:nome}");
        var r = t.Render(Ctx(), new Dictionary<string, string> { ["NOME"] = "Ana" });
        Assert.Equal("Ana / Reunião / Ana", r.Text);

        // No values at all: fields without a default render empty, the others use their default.
        Assert.Equal(" / Reunião", SnippetTemplate.Parse("{field:Nome} / {field:Assunto=Reunião}").Render(Ctx()).Text);
    }

    [Fact]
    public void Cursor_ReportsStepsFromTheEnd()
    {
        Assert.Equal(0, SnippetTemplate.Parse("sem cursor").Render(Ctx()).CursorFromEnd);
        Assert.Equal(0, SnippetTemplate.Parse("fim{cursor}").Render(Ctx()).CursorFromEnd);
        Assert.Equal(4, SnippetTemplate.Parse("a{cursor}bcde").Render(Ctx()).CursorFromEnd);

        // Line breaks count as one caret step, accents (combining marks) too.
        var r = SnippetTemplate.Parse("Olá,\n\n{cursor}\n\nAbraço").Render(Ctx());
        Assert.Equal("Olá,\n\n\n\nAbraço", r.Text);
        Assert.Equal(1 + 1 + 6, r.CursorFromEnd);   // "\n" "\n" "Abraço"
        Assert.Equal(2, SnippetTemplate.Parse("{cursor}éx").Render(Ctx()).CursorFromEnd);   // é (e + combining acute) + x
    }

    [Fact]
    public void OnlyFirstCursorCounts() =>
        Assert.Equal(3, SnippetTemplate.Parse("a{cursor}b{cursor}cd").Render(Ctx()).CursorFromEnd);

    [Fact]
    public void Newlines_AreNormalisedToLf() =>
        Assert.Equal("a\nb\nc", Render("a\r\nb\rc"));

    [Fact]
    public void UsesClipboard_IsDetected()
    {
        Assert.True(SnippetTemplate.Parse("x {clipboard}").UsesClipboard);
        Assert.False(SnippetTemplate.Parse("x {date}").UsesClipboard);
    }
}

public class TriggerBufferTests
{
    private static Snippet S(string trigger, bool auto = true) => new() { Id = trigger.GetHashCode(), Name = trigger, Trigger = trigger, AutoExpand = auto };

    private static Snippet? Type(TriggerBuffer b, string text)
    {
        Snippet? hit = null;
        foreach (char c in text) hit = b.Append(c.ToString()) ?? hit;
        return hit;
    }

    [Fact]
    public void Fires_WhenTheLastCharOfTheTriggerIsTyped()
    {
        var b = new TriggerBuffer(new[] { S(";email") });
        Assert.Null(Type(b, ";emai"));
        Assert.Equal(";email", b.Append("l")!.Trigger);
    }

    [Fact]
    public void Fires_InsideSurroundingText_ForSymbolTriggers()
    {
        var b = new TriggerBuffer(new[] { S(";tel") });
        Assert.Equal(";tel", Type(b, "meu número é;tel")!.Trigger);
    }

    [Fact]
    public void WordTriggers_NeedAWordBoundary()
    {
        var b = new TriggerBuffer(new[] { S("btw") });
        Assert.Null(Type(b, "abtw"));
        b.Reset();
        Assert.NotNull(Type(b, "ok btw"));
        b.Reset();
        Assert.NotNull(Type(b, "btw"));           // start of the buffer counts as a boundary
    }

    [Fact]
    public void Backspace_UndoesTyping()
    {
        var b = new TriggerBuffer(new[] { S(";email") });
        Type(b, ";emaX");          // typo...
        b.Backspace();             // ...corrected
        Assert.Null(b.Append("i"));
        Assert.Equal(";email", b.Append("l")!.Trigger);
    }

    [Fact]
    public void Reset_ForgetsPartialTriggers()
    {
        var b = new TriggerBuffer(new[] { S(";email") });
        Type(b, ";em");
        b.Reset();
        Assert.Null(Type(b, "ail"));
    }

    [Fact]
    public void ClearsAfterAMatch_SoItDoesNotFireTwice()
    {
        var b = new TriggerBuffer(new[] { S(";a1") });
        Assert.NotNull(Type(b, ";a1"));
        Assert.Equal("", b.Current);
    }

    [Fact]
    public void IgnoresDisabledAndTriggerlessSnippets()
    {
        var b = new TriggerBuffer(new[] { S(";off", auto: false), S("") });
        Assert.Null(Type(b, ";off"));
    }

    [Fact]
    public void Triggers_AreCaseSensitive()
    {
        var b = new TriggerBuffer(new[] { S(";Email") });
        Assert.Null(Type(b, ";email"));
        Assert.NotNull(Type(b, ";Email"));
    }

    [Fact]
    public void LongTyping_KeepsOnlyTheTail()
    {
        var b = new TriggerBuffer(new[] { S(";x1") });
        Type(b, new string('a', 500));
        Assert.True(b.Current.Length <= 64);
        Assert.NotNull(Type(b, ";x1"));
    }

    [Fact]
    public void SetSnippets_ReplacesTheTriggers()
    {
        var b = new TriggerBuffer(new[] { S(";old") });
        b.SetSnippets(new[] { S(";new") });
        Assert.Null(Type(b, ";old"));
        Assert.NotNull(Type(b, ";new"));
    }
}

public class SnippetStoreTests
{
    private static Snippet New(string name, string trigger = "", string content = "texto") =>
        new() { Name = name, Trigger = trigger, Content = content };

    [Fact]
    public void CreateEditDelete_RoundTrip()
    {
        using var store = new SnippetStore(":memory:");
        int changes = 0;
        store.Changed += (_, _) => changes++;

        var saved = store.Save(new Snippet
        {
            Name = "Assinatura", Trigger = ";ass", Content = "Att,\r\nRafael", Category = "Trabalho",
            Tags = new() { "email", " formal ", "email" }, Favorite = true, Mode = InsertMode.Paste,
        });
        Assert.True(saved.Id > 0);
        Assert.Equal("Att,\nRafael", saved.Content);                       // newlines normalised
        Assert.Equal(new[] { "email", "formal" }, saved.Tags.ToArray());   // trimmed, deduplicated
        Assert.True(saved.Favorite);
        Assert.Equal(InsertMode.Paste, saved.Mode);

        saved.Name = "Assinatura formal";
        saved.Content = "Atenciosamente";
        var edited = store.Save(saved);
        Assert.Equal(saved.Id, edited.Id);
        Assert.Equal("Assinatura formal", edited.Name);
        Assert.Single(store.All);

        store.Delete(saved.Id);
        Assert.Empty(store.All);
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Persists_AcrossReopen()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "s.db");
        using (var store = new SnippetStore(path)) store.Save(New("Persistente", ";pers", "olá"));
        using var again = new SnippetStore(path);
        Assert.Equal("olá", again.All.Single().Content);
    }

    [Theory]
    [InlineData("", "Dê um nome")]
    [InlineData("   ", "Dê um nome")]
    public void Name_IsRequired(string name, string message)
    {
        using var store = new SnippetStore(":memory:");
        var ex = Assert.Throws<SnippetValidationException>(() => store.Save(New(name)));
        Assert.Contains(message, ex.Message);
    }

    [Theory]
    [InlineData("a", "ao menos")]
    [InlineData("com espaço", "espaços")]
    [InlineData("tab\tchar", "espaços")]
    public void Trigger_FormatRules(string trigger, string message)
    {
        using var store = new SnippetStore(":memory:");
        Assert.Contains(message, store.ValidateTrigger(trigger));
        Assert.Throws<SnippetValidationException>(() => store.Save(New("X", trigger)));
    }

    [Fact]
    public void Trigger_TooLong_IsRejected()
    {
        using var store = new SnippetStore(":memory:");
        Assert.NotNull(store.ValidateTrigger(";" + new string('x', 40)));
    }

    [Fact]
    public void Trigger_DuplicatesAndPrefixConflicts_AreRejected()
    {
        using var store = new SnippetStore(":memory:");
        store.Save(New("Email", ";email"));

        Assert.Contains("já é usado", store.ValidateTrigger(";email"));
        Assert.Contains("começo deste gatilho", store.ValidateTrigger(";email2"));   // ";email" would fire first
        Assert.Contains("impediria", store.ValidateTrigger(";em"));                  // would fire before ";email"
        Assert.Null(store.ValidateTrigger(";end"));
        Assert.Null(store.ValidateTrigger(""));
    }

    [Fact]
    public void EditingASnippet_DoesNotConflictWithItself()
    {
        using var store = new SnippetStore(":memory:");
        var s = store.Save(New("Email", ";email"));
        s.Content = "novo";
        Assert.Equal("novo", store.Save(s).Content);
        Assert.Null(store.ValidateTrigger(";email", s.Id));
    }

    [Fact]
    public void MultipleSnippets_WithoutTrigger_AreAllowed()
    {
        using var store = new SnippetStore(":memory:");
        store.Save(New("A"));
        store.Save(New("B"));
        Assert.Equal(2, store.All.Count);
    }

    [Fact]
    public void ContentSearch_IsAccentInsensitive_AndPrefixBased()
    {
        using var store = new SnippetStore(":memory:");
        store.Save(New("Cobrança", content: "Prezado cliente, identificamos um pagamento em atraso."));
        store.Save(New("Agradecimento", content: "Muito obrigado pela compra!"));

        Assert.Equal("Cobrança", store.SearchContent("pagamento").Single().Name);
        Assert.Equal("Cobrança", store.SearchContent("ATRASÓ").Single().Name);          // case and accents ignored
        Assert.Equal("Cobrança", store.SearchContent("cobranca").Single().Name);      // matches the name without accent
        Assert.Equal("Agradecimento", store.SearchContent("obrig").Single().Name);   // prefix
        Assert.Empty(store.SearchContent("inexistente"));
        Assert.Empty(store.SearchContent("\"* OR ("));                                  // operators are neutralised
    }

    [Fact]
    public void ContentSearch_FollowsEditsAndDeletes()
    {
        using var store = new SnippetStore(":memory:");
        var s = store.Save(New("X", content: "alfa beta"));
        Assert.Single(store.SearchContent("alfa"));
        s.Content = "gama";
        store.Save(s);
        Assert.Empty(store.SearchContent("alfa"));
        Assert.Single(store.SearchContent("gama"));
        store.Delete(s.Id);
        Assert.Empty(store.SearchContent("gama"));
    }

    [Fact]
    public void RecordUse_UpdatesStatistics_WithoutRaisingChanged()
    {
        using var store = new SnippetStore(":memory:");
        var s = store.Save(New("Usado"));
        bool raised = false;
        store.Changed += (_, _) => raised = true;
        store.RecordUse(s.Id);
        store.RecordUse(s.Id);
        Assert.Equal(2, store.Get(s.Id)!.UseCount);
        Assert.NotNull(store.Get(s.Id)!.LastUsedUtc);
        Assert.False(raised);
    }

    [Fact]
    public void Categories_AreDistinctAndSorted()
    {
        using var store = new SnippetStore(":memory:");
        store.Save(new Snippet { Name = "1", Content = "x", Category = "Trabalho" });
        store.Save(new Snippet { Name = "2", Content = "x", Category = "trabalho" });
        store.Save(new Snippet { Name = "3", Content = "x", Category = "Casa" });
        store.Save(new Snippet { Name = "4", Content = "x" });
        Assert.Equal(new[] { "Casa", "Trabalho" }, store.Categories.ToArray());
    }

    [Fact]
    public void ExportImport_RoundTrips_AndReportsConflicts()
    {
        using var source = new SnippetStore(":memory:");
        source.Save(new Snippet { Name = "Um", Trigger = ";um", Content = "1", Category = "C", Tags = new() { "t" }, Favorite = true });
        source.Save(new Snippet { Name = "Dois", Trigger = ";dois", Content = "2" });
        string json = source.ExportJson();

        using var target = new SnippetStore(":memory:");
        target.Save(new Snippet { Name = "Outro", Trigger = ";dois", Content = "ocupa o gatilho" });
        var result = target.ImportJson(json);

        Assert.Equal(1, result.Imported);
        Assert.Single(result.Skipped);
        Assert.Contains("Dois", result.Skipped[0]);
        var um = target.All.Single(s => s.Name == "Um");
        Assert.Equal(";um", um.Trigger);
        Assert.True(um.Favorite);
        Assert.Equal(new[] { "t" }, um.Tags.ToArray());

        // Importing again updates by name instead of duplicating.
        var again = target.ImportJson(json);
        Assert.Equal(0, again.Imported);
        Assert.Equal(1, again.Updated);
        Assert.Equal(2, target.All.Count);
    }

    [Fact]
    public void Import_RejectsGarbage()
    {
        using var store = new SnippetStore(":memory:");
        Assert.Throws<SnippetValidationException>(() => store.ImportJson("isso não é json"));
        Assert.Throws<SnippetValidationException>(() => store.ImportJson("{}"));
    }

    [Fact]
    public void SeedExamples_RunOnlyOnce()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "s.db");
        using (var store = new SnippetStore(path))
        {
            store.SeedExamplesOnce();
            Assert.Equal(4, store.All.Count);
            Assert.All(store.All, s => Assert.Null(store.ValidateTrigger(s.Trigger, s.Id)));
            foreach (var s in store.All.ToList()) store.Delete(s.Id);
        }
        using var again = new SnippetStore(path);
        again.SeedExamplesOnce();
        Assert.Empty(again.All);   // the user deleted them: they must not come back
    }
}

public class SnippetsProviderTests
{
    private static async Task<IReadOnlyList<SearchResult>> Query(SnippetsProvider p, string q) =>
        await p.QueryAsync(SearchContext.Create(q), CancellationToken.None);

    [Fact]
    public async Task FindsByNameTriggerCategoryTagAndContent()
    {
        using var store = new SnippetStore(":memory:");
        store.Save(new Snippet { Name = "Assinatura profissional", Trigger = ";ass", Content = "Atenciosamente, Rafael", Category = "Trabalho", Tags = new() { "email" } });
        store.Save(new Snippet { Name = "Chave Pix", Trigger = ";pix", Content = "chave@exemplo.com" });
        var p = new SnippetsProvider(store);

        Assert.Equal("Assinatura profissional", (await Query(p, "assinatura"))[0].Title);
        Assert.Equal("Assinatura profissional", (await Query(p, ";ass"))[0].Title);
        Assert.Equal("Chave Pix", (await Query(p, ";pix"))[0].Title);
        Assert.Contains(await Query(p, "trabalho"), r => r.Title == "Assinatura profissional");
        Assert.Contains(await Query(p, "atenciosamente"), r => r.Title == "Assinatura profissional");   // content only
        Assert.Empty(await Query(p, "zzzz"));
    }

    [Fact]
    public async Task Result_CarriesTheIdAndAPreview()
    {
        using var store = new SnippetStore(":memory:");
        var s = store.Save(new Snippet { Name = "Saudação", Trigger = ";oi", Content = "Olá!\nTudo bem?" });
        var r = (await Query(new SnippetsProvider(store), "saudacao")).Single();
        Assert.Equal(ResultKind.Snippet, r.Kind);
        Assert.Equal(s.Id.ToString(), r.Payload);
        Assert.Equal($"snippet:{s.Id}", r.Key);
        Assert.Contains(";oi", r.Subtitle);
        Assert.Contains("Olá! Tudo bem?", r.Subtitle);
    }

    [Fact]
    public async Task Favorites_RankFirst()
    {
        using var store = new SnippetStore(":memory:");
        store.Save(new Snippet { Name = "Resposta padrão A", Content = "x" });
        store.Save(new Snippet { Name = "Resposta padrão B", Content = "x", Favorite = true });
        var results = await Query(new SnippetsProvider(store), "resposta");
        Assert.Equal("Resposta padrão B", results.OrderByDescending(r => r.Score).First().Title);
    }
}
