using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CopilotFs.Core;

public sealed record Failure(string Code, string Message, int? OperationIndex = null, int? ChangeIndex = null, string? Path = null);
public sealed record FileContent(string Path, string Content);
public sealed record SkippedFile(string Path, string Reason);
public sealed record ReadContext(string Repository, string Cwd);
public sealed record MutationResult(int OperationIndex, string Type, string Path, int? Replacements = null);
public sealed record Response(bool Ok, string? MutationState = null, Failure? Error = null,
    ReadContext? Context = null, IReadOnlyList<FileContent>? Files = null,
    IReadOnlyList<SkippedFile>? Skipped = null, IReadOnlyList<MutationResult>? Results = null,
    IReadOnlyList<string>? Paths = null)
{
    public int Version => 1;
    public string ToJson() => JsonSerializer.Serialize(this, Protocol.JsonOptions);
}

internal sealed class BridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int? OperationIndex { get; set; }
    public int? ChangeIndex { get; set; }
    public string? TargetPath { get; set; }
    public Failure Failure => new(Code, Message, OperationIndex, ChangeIndex, TargetPath);
}

internal sealed record Change(string Old, string New);
internal sealed record Operation(string Type, string? Path, List<Change>? Changes = null, string? Content = null);

internal static class Protocol
{
    internal const int FileLimit = 1024 * 1024;
    internal const int BatchLimit = 10 * 1024 * 1024;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    internal static List<Operation> Parse(string input)
    {
        if (Encoding.UTF8.GetByteCount(input) > BatchLimit) throw new BridgeException("limit_exceeded", "Request exceeds 10 MiB.");
        var json = input.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = json.IndexOf('\n');
            if (newline < 0 || !json.EndsWith("```", StringComparison.Ordinal)) throw Invalid("Expected one JSON code block.");
            var header = json[..newline].TrimEnd();
            if (header is not ("```" or "```json")) throw Invalid("Only a JSON code block is supported.");
            json = json[(newline + 1)..^3].Trim();
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Fields(root, ["version", "operations"]);
        if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32Safe(out var number)) throw Invalid("version must be an integer.");
        if (number != 1) throw new BridgeException("unsupported_version", "Only version 1 is supported.");
        if (!root.TryGetProperty("operations", out var operations) || operations.ValueKind != JsonValueKind.Array || operations.GetArrayLength() is < 1 or > 100)
            throw Invalid("operations must contain 1 to 100 items.");
        var result = new List<Operation>();
        foreach (var item in operations.EnumerateArray())
        {
            try
            {
                var type = Text(item, "type");
                Fields(item, type switch
                {
                    "read" or "tree" => ["type", "path"],
                    "write" => ["type", "path", "changes"],
                    "create" => ["type", "path", "content"],
                    _ => throw Invalid("Unknown operation type.")
                });
                var path = (type is "read" or "tree") && !item.TryGetProperty("path", out _) ? null : Text(item, "path");
                if (path is not null && string.IsNullOrWhiteSpace(path)) throw Invalid("path cannot be empty.");
                List<Change>? changes = null;
                if (type == "write")
                {
                    if (!item.TryGetProperty("changes", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() == 0)
                        throw Invalid("changes must be a nonempty array.");
                    changes = [];
                    foreach (var change in array.EnumerateArray())
                    {
                        try
                        {
                            Fields(change, ["old", "new"]);
                            var old = Text(change, "old");
                            if (old.Length == 0) throw Invalid("old cannot be empty.");
                            changes.Add(new(old, Text(change, "new")));
                        }
                        catch (BridgeException e) { e.ChangeIndex = changes.Count; throw; }
                    }
                }
                result.Add(new(type, path, changes, type == "create" ? Text(item, "content") : null));
            }
            catch (BridgeException e) { e.OperationIndex = result.Count; throw; }
        }
        if (result.Select(o => o.Type is "write" or "create" ? "mutation" : o.Type).Distinct().Count() != 1)
            throw new BridgeException("mixed_batch", "Tree, read and mutation requests must be separate batches.");
        return result;
    }

    private static bool TryGetInt32Safe(this JsonElement value, out int number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out number);
    }
    private static string Text(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            throw Invalid($"{name} must be a string.");
        string text;
        try { text = value.GetString()!; }
        catch (InvalidOperationException) { throw Invalid("Strings must contain valid Unicode."); }
        try { Utf8.GetByteCount(text); }
        catch (EncoderFallbackException) { throw Invalid("Strings must contain valid Unicode."); }
        return text;
    }
    private static void Fields(JsonElement item, string[] allowed)
    {
        if (item.ValueKind != JsonValueKind.Object) throw Invalid("Expected an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in item.EnumerateObject())
            if (!seen.Add(field.Name) || !allowed.Contains(field.Name)) throw Invalid($"Unknown or duplicate field: {field.Name}.");
    }
    private static BridgeException Invalid(string message) => new("invalid_request", message);
}
