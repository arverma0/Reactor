using System.Collections;

namespace Reactor.Agent.Environments;

/// <summary>Builds child-process environments according to an <see cref="EnvVarPolicy"/>.</summary>
internal static class EnvironmentVariables
{
    private static readonly string[] SensitiveSuffixes = ["_API_KEY", "_SECRET", "_TOKEN", "_PASSWORD", "_CREDENTIAL"];

    private static readonly string[] SensitiveNames = ["API_KEY", "SECRET", "TOKEN", "PASSWORD", "CREDENTIAL"];

    /// <summary>Variables kept by <see cref="EnvVarPolicy.InheritCore"/> on every platform.</summary>
    public static IReadOnlyList<string> CoreNames { get; } =
    [
        "PATH", "HOME", "USER", "SHELL", "LANG", "LC_ALL", "LC_CTYPE", "TERM", "TMPDIR", "TEMP", "TMP",
        "GOPATH", "GOROOT", "CARGO_HOME", "RUSTUP_HOME", "NVM_DIR", "PYENV_ROOT", "VIRTUAL_ENV", "JAVA_HOME",
        "DOTNET_ROOT", "NODE_PATH",
    ];

    /// <summary>Additional variables kept by <see cref="EnvVarPolicy.InheritCore"/> on Windows.</summary>
    public static IReadOnlyList<string> WindowsCoreNames { get; } =
    [
        "SYSTEMROOT", "WINDIR", "COMSPEC", "PATHEXT", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "PROGRAMDATA",
        "PROGRAMFILES", "PROGRAMFILES(X86)", "SYSTEMDRIVE", "USERNAME", "USERDOMAIN", "HOMEDRIVE", "HOMEPATH",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS",
    ];

    /// <summary>Variables kept even by <see cref="EnvVarPolicy.InheritNone"/> on Windows so <c>cmd.exe</c> works.</summary>
    public static IReadOnlyList<string> WindowsEssentialNames { get; } = ["SYSTEMROOT", "COMSPEC", "PATHEXT"];

    /// <summary>
    /// True for names that look like secrets: <c>*_API_KEY</c>, <c>*_SECRET</c>, <c>*_TOKEN</c>, <c>*_PASSWORD</c>,
    /// <c>*_CREDENTIAL</c> (or exactly <c>API_KEY</c>, <c>SECRET</c>, ...), case-insensitively.
    /// </summary>
    public static bool IsSensitive(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var exact in SensitiveNames)
        {
            if (name.Equals(exact, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var suffix in SensitiveSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The current process environment.</summary>
    public static IEnumerable<KeyValuePair<string, string>> CurrentProcess()
    {
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                yield return new KeyValuePair<string, string>(key, value);
            }
        }
    }

    /// <summary>
    /// Computes the environment for a child process: filters <paramref name="inherited"/> by <paramref name="policy"/>, then
    /// applies <paramref name="extra"/> and finally <paramref name="perCall"/> (both always win over filtering).
    /// </summary>
    public static Dictionary<string, string> Build(
        EnvVarPolicy policy,
        IEnumerable<KeyValuePair<string, string>> inherited,
        IReadOnlyDictionary<string, string>? extra,
        IReadOnlyDictionary<string, string>? perCall,
        bool isWindows)
    {
        ArgumentNullException.ThrowIfNull(inherited);
        var comparer = isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new Dictionary<string, string>(comparer);
        foreach (var (name, value) in inherited)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var include = policy switch
            {
                EnvVarPolicy.InheritAll => true,
                EnvVarPolicy.FilterSensitive => !IsSensitive(name),
                EnvVarPolicy.InheritCore => IsCore(name, isWindows),
                EnvVarPolicy.InheritNone => isWindows && Contains(WindowsEssentialNames, name, isWindows),
                _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, "Unknown environment variable policy."),
            };

            if (include)
            {
                result[name] = value;
            }
        }

        Apply(result, extra);
        Apply(result, perCall);
        return result;
    }

    private static bool IsCore(string name, bool isWindows) =>
        Contains(CoreNames, name, isWindows) || (isWindows && Contains(WindowsCoreNames, name, isWindows));

    private static bool Contains(IReadOnlyList<string> names, string name, bool ignoreCase)
    {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        foreach (var candidate in names)
        {
            if (string.Equals(candidate, name, comparison))
            {
                return true;
            }
        }

        return false;
    }

    private static void Apply(Dictionary<string, string> target, IReadOnlyDictionary<string, string>? values)
    {
        if (values is null)
        {
            return;
        }

        foreach (var (name, value) in values)
        {
            if (!string.IsNullOrEmpty(name))
            {
                target[name] = value ?? "";
            }
        }
    }
}
