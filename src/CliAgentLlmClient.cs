using System.Diagnostics;
using System.Text;
using Thortspace.Headless;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// The "local agent" provider: runs a CLI AI agent (grok, claude, gemini — anything with a
/// non-interactive print mode) as a subprocess and returns its stdout. Keyless: it rides whatever
/// account the CLI tool is logged into. <see cref="ILlmClient"/> is public in the SDK, so plugging in
/// a custom transport is just this class.
///
/// Configure:
///   THORTSPACE_LLM_PROVIDER = cli   (aliases: grok, claude, gemini — a sensible default command each)
///   THORTSPACE_LLM_CMD      = the command with its print-mode flag, e.g. "grok -p" or "claude -p".
///                             The prompt is appended as one final argument (quoting handled), so the
///                             print flag must come LAST when it TAKES the prompt as its value
///                             (grok's -p/--single, claude's -p/--print) — e.g. "grok -m grok-4.5 -p".
///                             Optional — defaults per the provider alias above.
///
/// Trade-offs vs a direct API / local-server provider: slower per call (each call boots the agent),
/// the agent may wrap the reply in prose or markdown (the pipeline's JSON extractor tolerates that),
/// and batch use of a subscription agent is subject to that tool's usage terms — fine at pilot scale.
/// </summary>
public sealed class CliAgentLlmClient : ILlmClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);   // agents are slow; be generous

    private readonly string _exe;
    private readonly List<string> _argsPrefix;

    public string Provider => "cli";
    public string Model { get; }

    public CliAgentLlmClient(string commandLine)
    {
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("THORTSPACE_LLM_CMD is empty");
        _exe = parts[0];
        _argsPrefix = parts.Skip(1).ToList();
        Model = Path.GetFileNameWithoutExtension(_exe);
    }

    /// <summary>Default print-mode command for a known agent alias, or null if unknown.</summary>
    public static string? DefaultCommandFor(string providerAlias) => providerAlias switch
    {
        "grok" => "grok -p",           // -p / --single: single-turn, prints to stdout, exits
        "claude" => "claude -p",       // -p / --print: non-interactive print mode
        "gemini" => "gemini -p",       // -p / --prompt
        _ => null,
    };

    public async Task<string> CompleteAsync(string systemPrompt, string userMessage)
    {
        // CLI agents take one prompt, not a system/user pair — concatenate with a clear seam.
        var prompt = systemPrompt + "\n\n---\n\n" + userMessage;

        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in _argsPrefix) psi.ArgumentList.Add(a);   // ArgumentList handles Windows quoting
        psi.ArgumentList.Add(prompt);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start '{_exe}'");
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(Timeout);
        try { await process.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"'{_exe}' produced no result within {Timeout.TotalMinutes:0} minutes");
        }

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"'{_exe}' exited {process.ExitCode}: {stderr.ToString().Trim()}".Trim());
        return stdout.ToString();
    }
}
