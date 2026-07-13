using System.Text.Json;
using Thortspace.Headless;

namespace ThortspaceKnowledgeNetwork;

/// <summary>
/// The LLM as a STATELESS function: prompt in, one strict-JSON object out. This is the whole of how the
/// generator "recruits" the model — no agent loop, no conversation, no tool-calling. The pattern is
/// "LLM proposes, code disposes": every reply is parsed, validated and bounded by deterministic C#
/// before anything touches a sphere.
///
/// The client itself comes from Thortspace.Headless — the same provider-agnostic <see cref="ILlmClient"/>
/// the app's AI features use (Gemini / Claude / any OpenAI-compatible endpoint). Bring your own key:
///   THORTSPACE_LLM_PROVIDER   (default "google")
///   THORTSPACE_LLM_KEY        (or GEMINI_API_KEY / GOOGLE_API_KEY / ANTHROPIC_API_KEY / OPENAI_API_KEY)
///   THORTSPACE_LLM_MODEL      (optional; the factory picks a sensible default per provider)
/// </summary>
public sealed class LlmJson
{
    private readonly ILlmClient _client;
    private LlmJson(ILlmClient client) => _client = client;

    public string Describe => $"{_client.Provider}/{_client.Model}";

    public static LlmJson? CreateFromEnvironment()
    {
        var provider = Environment.GetEnvironmentVariable("THORTSPACE_LLM_PROVIDER") ?? "google";
        var key = Environment.GetEnvironmentVariable("THORTSPACE_LLM_KEY")
                  ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                  ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                  ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
                  ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key)) return null;
        var model = Environment.GetEnvironmentVariable("THORTSPACE_LLM_MODEL");
        return new LlmJson(LlmClientFactory.Create(provider, key, model));
    }

    /// <summary>
    /// One completion, expected to be a single JSON object. Markdown fences and any chatter around the
    /// object are tolerated (we take the outermost {...}); a malformed reply gets ONE corrective retry.
    /// Returns null only when both attempts fail — callers fall back or skip.
    /// </summary>
    public async Task<JsonDocument?> CompleteJsonAsync(string systemPrompt, string userMessage)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            string reply;
            try { reply = await _client.CompleteAsync(systemPrompt, userMessage); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  LLM call failed ({ex.Message}); " + (attempt == 0 ? "retrying…" : "giving up."));
                continue;
            }

            var json = ExtractJsonObject(reply);
            if (json != null)
            {
                try { return JsonDocument.Parse(json); }
                catch (JsonException) { /* fall through to retry */ }
            }
            if (attempt == 0)
                userMessage += "\n\nYour previous reply was not a single valid JSON object. " +
                               "Reply again with ONLY the JSON object — no prose, no markdown fences.";
        }
        return null;
    }

    /// <summary>The outermost {...} of a reply (models love to wrap JSON in ```fences``` or a sentence).</summary>
    private static string? ExtractJsonObject(string reply)
    {
        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        return start >= 0 && end > start ? reply[start..(end + 1)] : null;
    }

    // ---- small safe readers shared by the validators (missing/mistyped fields never throw) ----

    public static string Str(JsonElement e, string prop, string fallback = "") =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback : fallback;

    public static IEnumerable<JsonElement> Arr(JsonElement e, string prop)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var item in v.EnumerateArray()) yield return item;
    }
}
