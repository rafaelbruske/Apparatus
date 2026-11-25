using Apparatus.Core.About;
using Apparatus.Native;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Apparatus.App;

/// <summary>One line of the open source licenses list: the package, and the two texts the line shows.</summary>
public sealed record LicenseRow(LicensedPackage Package, string Title, string Subtitle);

/// <summary>The "Sobre" page: the version, the program's own license, and the licenses of the third-party components it carries.</summary>
public sealed partial class SettingsView
{
    private ThirdPartyLicenses? _thirdParty;

    private static string LicensesFolder => Path.Combine(AppContext.BaseDirectory, "licenses");

    private void LoadAbout()
    {
        var u = _services.Updates;
        string running = typeof(SettingsView).Assembly.GetName().Version?.ToString(3) ?? "?";
        AboutVersionText.Text = u.IsInstalled ? $"Versão {u.InstalledVersion}" : $"Versão {running} (executando de uma pasta de build)";

        try
        {
            _thirdParty = ThirdPartyLicenses.LoadEmbedded();
            var third = _thirdParty;
            LicenseList.ItemsSource = third.Packages.Select(p => new LicenseRow(p, p.Title, third.Subtitle(p))).ToList();
        }
        catch (Exception ex)
        {
            Apparatus.Core.AppLog.Error("The list of open source licenses could not be loaded", ex);
        }

        // The complete notices (the .NET runtime's and the Windows App SDK's) are files next to the program; a build run from source has none.
        OpenLicensesFolderButton.Visibility = Directory.Exists(LicensesFolder) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) OpenExternal(url);
    }

    private void OnOpenLicensesFolder(object sender, RoutedEventArgs e) => OpenExternal(LicensesFolder);

    private static void OpenExternal(string target)
    {
        try { ShellLauncher.Open(target); }
        catch (Exception ex) { Apparatus.Core.AppLog.Error($"Opening '{target}' failed", ex); }
    }

    private async void OnLicenseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LicenseRow row } || _thirdParty is null) return;
        var package = row.Package;

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        if (package.ProjectUrl is { } project)
            links.Children.Add(CreateLink(project.Contains("github.com", StringComparison.OrdinalIgnoreCase) ? "Repositório no GitHub" : "Site do projeto", project));
        if (package.NuGetUrl is { } nuget)
            links.Children.Add(CreateLink("Página no NuGet", nuget));

        var text = new TextBlock
        {
            Text = _thirdParty.FullText(package),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = row.Subtitle, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        if (links.Children.Count > 0) content.Children.Add(links);
        content.Children.Add(new ScrollViewer
        {
            MaxHeight = 360,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Padding = new Thickness(0, 0, 12, 0),
            Content = text,
        });

        var dialog = new ContentDialog
        {
            Title = row.Title,
            Content = content,
            CloseButtonText = "Fechar",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 760.0;
        await dialog.ShowAsync();
    }

    private HyperlinkButton CreateLink(string label, string url)
    {
        var button = new HyperlinkButton { Content = label, Tag = url, Padding = new Thickness(0) };
        button.Click += OnOpenLink;
        return button;
    }
}
