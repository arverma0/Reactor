using Reactor.Agent.Environments;

namespace Reactor.Agent.Tests.Environments;

public class EnvironmentVariablesTests
{
    private static readonly KeyValuePair<string, string>[] Source =
    [
        new("PATH", "/usr/bin"),
        new("HOME", "/home/me"),
        new("LANG", "en_US.UTF-8"),
        new("OPENAI_API_KEY", "sk-1"),
        new("github_token", "ghp"),
        new("DB_PASSWORD", "pw"),
        new("APP_SECRET", "s"),
        new("AWS_CREDENTIAL", "c"),
        new("TOKEN", "t"),
        new("TOKENIZER_PATH", "/tok"),
        new("SECRETARY", "bob"),
        new("EDITOR", "vim"),
        new("SystemRoot", @"C:\Windows"),
        new("ComSpec", @"C:\Windows\system32\cmd.exe"),
        new("PATHEXT", ".EXE"),
        new("USERPROFILE", @"C:\Users\me"),
    ];

    [Theory]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("anthropic_api_key", true)]
    [InlineData("GITHUB_TOKEN", true)]
    [InlineData("DB_PASSWORD", true)]
    [InlineData("CLIENT_SECRET", true)]
    [InlineData("AZURE_CREDENTIAL", true)]
    [InlineData("API_KEY", true)]
    [InlineData("token", true)]
    [InlineData("Password", true)]
    [InlineData("PATH", false)]
    [InlineData("TOKENIZER_PATH", false)]
    [InlineData("SECRETARY", false)]
    [InlineData("MY_TOKENS", false)]
    [InlineData("GOOGLE_APPLICATION_CREDENTIALS", false)]
    public void Sensitive_names_are_detected(string name, bool sensitive)
    {
        Assert.Equal(sensitive, EnvironmentVariables.IsSensitive(name));
    }

    [Fact]
    public void Filter_sensitive_drops_only_secrets()
    {
        var result = EnvironmentVariables.Build(EnvVarPolicy.FilterSensitive, Source, null, null, isWindows: false);

        Assert.Equal(
            new[] { "PATH", "HOME", "LANG", "TOKENIZER_PATH", "SECRETARY", "EDITOR", "SystemRoot", "ComSpec", "PATHEXT", "USERPROFILE" }.Order(),
            result.Keys.Order());
    }

    [Fact]
    public void Inherit_all_keeps_everything()
    {
        var result = EnvironmentVariables.Build(EnvVarPolicy.InheritAll, Source, null, null, isWindows: false);

        Assert.Equal(Source.Length, result.Count);
        Assert.Equal("sk-1", result["OPENAI_API_KEY"]);
    }

    [Fact]
    public void Inherit_core_keeps_core_variables_case_sensitively_on_unix()
    {
        var result = EnvironmentVariables.Build(EnvVarPolicy.InheritCore, Source, null, null, isWindows: false);

        Assert.Equal(new[] { "HOME", "LANG", "PATH" }, result.Keys.Order());
    }

    [Fact]
    public void Inherit_core_includes_windows_essentials_case_insensitively_on_windows()
    {
        KeyValuePair<string, string>[] source = [new("Path", @"C:\bin"), new("SystemRoot", @"C:\Windows"), new("windir", @"C:\Windows"), new("EDITOR", "code")];

        var result = EnvironmentVariables.Build(EnvVarPolicy.InheritCore, source, null, null, isWindows: true);

        Assert.Equal(new[] { "Path", "SystemRoot", "windir" }, result.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(@"C:\bin", result["PATH"]);
    }

    [Fact]
    public void Inherit_none_is_empty_on_unix_but_keeps_cmd_essentials_on_windows()
    {
        Assert.Empty(EnvironmentVariables.Build(EnvVarPolicy.InheritNone, Source, null, null, isWindows: false));

        var windows = EnvironmentVariables.Build(EnvVarPolicy.InheritNone, Source, null, null, isWindows: true);
        Assert.Equal(new[] { "ComSpec", "PATHEXT", "SystemRoot" }, windows.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Extra_and_per_call_variables_win_in_that_order()
    {
        var extra = new Dictionary<string, string> { ["OPENAI_API_KEY"] = "allowed", ["EDITOR"] = "nano", ["SHARED"] = "extra" };
        var perCall = new Dictionary<string, string> { ["SHARED"] = "call", ["NEW_ONE"] = "1" };

        var result = EnvironmentVariables.Build(EnvVarPolicy.FilterSensitive, Source, extra, perCall, isWindows: false);

        Assert.Equal("allowed", result["OPENAI_API_KEY"]);
        Assert.Equal("nano", result["EDITOR"]);
        Assert.Equal("call", result["SHARED"]);
        Assert.Equal("1", result["NEW_ONE"]);
    }

    [Fact]
    public void Windows_names_are_case_insensitive_when_overriding()
    {
        var result = EnvironmentVariables.Build(
            EnvVarPolicy.InheritAll, [new("Path", "a")], null, new Dictionary<string, string> { ["PATH"] = "b" }, isWindows: true);

        Assert.Single(result);
        Assert.Equal("b", result["path"]);
    }
}
