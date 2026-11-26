using Apparatus.Core.Settings;

namespace Apparatus.Core.Tests;

public class UpdateSourcesTests
{
    [Theory]
    [InlineData("https://github.com/rafaelbruske/Apparatus")]
    [InlineData("https://github.com/rafaelbruske/Apparatus/")]
    [InlineData("https://github.com/rafaelbruske/Apparatus.git")]
    [InlineData("  https://GitHub.com/someone/fork  ")]
    public void IsGithubRepository_AcceptsTheAddressOfARepository(string source) =>
        Assert.True(UpdateSources.IsGithubRepository(source));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"D:\releases")]                                                       // a folder
    [InlineData("https://example.com/apparatus")]                                       // another web address
    [InlineData("http://github.com/rafaelbruske/Apparatus")]                           // not https
    [InlineData("https://github.com/rafaelbruske")]                                     // an account, not a repository
    [InlineData("https://github.com/rafaelbruske/Apparatus/releases/latest/download")]  // files of a release: a plain web address
    [InlineData("https://github.com/rafaelbruske/Apparatus?tab=readme")]
    [InlineData("https://github.com.evil.example/rafaelbruske/Apparatus")]
    [InlineData("https://gist.github.com/rafaelbruske/Apparatus")]
    public void IsGithubRepository_RejectsAnythingElse(string? source) =>
        Assert.False(UpdateSources.IsGithubRepository(source));

    [Fact]
    public void TheOfficialRepositoryIsARepositoryAddress() =>
        Assert.True(UpdateSources.IsGithubRepository(UpdateSources.OfficialGithubRepository));
}
