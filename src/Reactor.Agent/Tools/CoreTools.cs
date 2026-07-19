using Reactor.Agent.Tools.ApplyPatch;

namespace Reactor.Agent.Tools;

/// <summary>
/// Factories for the built-in tools. Provider profiles assemble these into provider-aligned tool sets. Every tool performs
/// all I/O through <see cref="ToolContext.Environment"/> (so it works in any execution environment), resolves paths with
/// <see cref="IExecutionEnvironment.ResolvePath"/>, reports failures by throwing exceptions with actionable messages, and
/// returns its full output (the session applies truncation).
/// </summary>
public static class CoreTools
{
    /// <summary><c>read_file</c>(file_path, offset?, limit?): line-numbered content ("NNN | text"); images returned as image data.</summary>
    public static RegisteredTool ReadFile() => ReadFileTool.Create();

    /// <summary><c>write_file</c>(file_path, content): writes the full file, creating parent directories.</summary>
    public static RegisteredTool WriteFile() => WriteFileTool.Create();

    /// <summary><c>edit_file</c>(file_path, old_string, new_string, replace_all?): exact search/replace (Claude Code style).</summary>
    public static RegisteredTool EditFile() => EditFileTools.CreateClaude();

    /// <summary><c>edit_file</c>(file_path, old_string, new_string, expected_replacements?): gemini-cli style replace.</summary>
    public static RegisteredTool EditFileGemini() => EditFileTools.CreateGemini();

    /// <summary><c>shell</c>(command, timeout_ms?, description?). Default timeout comes from the profile or session config.</summary>
    public static RegisteredTool Shell() => ShellTool.Create();

    /// <summary><c>grep</c>(pattern, path?, glob_filter?, case_insensitive?, max_results?[, output_mode?, context?]).</summary>
    /// <param name="includeOutputModes">Adds the <c>output_mode</c> and <c>context</c> parameters (Claude Code style).</param>
    public static RegisteredTool Grep(bool includeOutputModes = false) => GrepTool.Create(includeOutputModes);

    /// <summary><c>glob</c>(pattern, path?): matching paths sorted newest first.</summary>
    public static RegisteredTool Glob() => GlobTool.Create();

    /// <summary><c>list_dir</c>(path?, depth?): directory listing.</summary>
    public static RegisteredTool ListDir() => ListDirTool.Create();

    /// <summary><c>read_many_files</c>(paths): reads several files (paths or glob patterns) in one call.</summary>
    public static RegisteredTool ReadManyFiles() => ReadManyFilesTool.Create();

    /// <summary><c>apply_patch</c>(patch): applies a v4a-format patch (add/delete/update/move).</summary>
    public static RegisteredTool ApplyPatch() => ApplyPatchTool.Create();

    /// <summary><c>web_fetch</c>(url, max_length?): fetches a URL and returns readable text.</summary>
    /// <param name="httpClient">Client to use; a shared client is used when null.</param>
    public static RegisteredTool WebFetch(HttpClient? httpClient = null) => WebFetchTool.Create(httpClient);
}
