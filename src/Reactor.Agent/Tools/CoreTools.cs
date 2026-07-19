namespace Reactor.Agent.Tools;

/// <summary>
/// Factories for the built-in tools. Provider profiles assemble these into provider-aligned tool sets.
/// (Contract stub: implementation pending.)
/// </summary>
public static class CoreTools
{
    /// <summary><c>read_file</c>(file_path, offset?, limit?): line-numbered content ("NNN | text"); images returned as image data.</summary>
    public static RegisteredTool ReadFile() => throw new NotImplementedException();

    /// <summary><c>write_file</c>(file_path, content): writes the full file, creating parent directories.</summary>
    public static RegisteredTool WriteFile() => throw new NotImplementedException();

    /// <summary><c>edit_file</c>(file_path, old_string, new_string, replace_all?): exact search/replace (Claude Code style).</summary>
    public static RegisteredTool EditFile() => throw new NotImplementedException();

    /// <summary><c>edit_file</c>(file_path, old_string, new_string, expected_replacements?): gemini-cli style replace.</summary>
    public static RegisteredTool EditFileGemini() => throw new NotImplementedException();

    /// <summary><c>shell</c>(command, timeout_ms?, description?). Default timeout comes from the profile or session config.</summary>
    public static RegisteredTool Shell() => throw new NotImplementedException();

    /// <summary><c>grep</c>(pattern, path?, glob_filter?, case_insensitive?, max_results?[, output_mode?]).</summary>
    public static RegisteredTool Grep(bool includeOutputModes = false) => throw new NotImplementedException();

    /// <summary><c>glob</c>(pattern, path?): matching paths sorted newest first.</summary>
    public static RegisteredTool Glob() => throw new NotImplementedException();

    /// <summary><c>list_dir</c>(path?, depth?): directory listing.</summary>
    public static RegisteredTool ListDir() => throw new NotImplementedException();

    /// <summary><c>read_many_files</c>(paths): reads several files (paths or glob patterns) in one call.</summary>
    public static RegisteredTool ReadManyFiles() => throw new NotImplementedException();

    /// <summary><c>apply_patch</c>(patch): applies a v4a-format patch (add/delete/update/move).</summary>
    public static RegisteredTool ApplyPatch() => throw new NotImplementedException();

    /// <summary><c>web_fetch</c>(url, max_length?): fetches a URL and returns readable text.</summary>
    public static RegisteredTool WebFetch(HttpClient? httpClient = null) => throw new NotImplementedException();
}
