using System.Text;
using System.Text.Json;

namespace CopilotFs.Core;

public sealed class Bridge
{
    // Deterministic failure injection for transaction tests; not exposed by the protocol or CLI.
    internal Action<int>? BeforeWrite { get; init; }
    internal Action<int>? BeforeRestore { get; init; }
    internal Action? BeforeCommit { get; init; }

    public Response Execute(string input, string workingDirectory)
    {
        try
        {
            var operations = Protocol.Parse(input);
            var repo = new Repository(workingDirectory);
            return operations[0].Type is "read" or "tree" ? Read(repo, operations, operations[0].Type == "tree") : Mutate(repo, operations);
        }
        catch (Exception e) when (Expected(e)) { return new(false, "unchanged", Describe(e)); }
    }

    private static Response Read(Repository repo, List<Operation> operations, bool pathsOnly)
    {
        var files = new SortedDictionary<string, FileContent>(StringComparer.Ordinal);
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        var skipped = new SortedDictionary<string, SkippedFile>(StringComparer.Ordinal);
        var seen = new HashSet<string>(Repository.PathComparer);
        string[]? candidates = null;
        long total = 0;
        for (var i = 0; i < operations.Count; i++)
        {
            var operation = operations[i];
            try
            {
                var target = repo.Resolve(operation.Path);
                if (File.Exists(target))
                {
                    if (pathsOnly) throw new BridgeException("not_a_directory", "Tree target must be a directory.");
                    Add(target, false); continue;
                }
                if (!Directory.Exists(target)) throw new BridgeException("not_found", "Read target does not exist.");
                candidates ??= repo.Files();
                var prefix = repo.Relative(target);
                foreach (var candidate in candidates.Select(p => p.TrimEnd('/')).Order(StringComparer.Ordinal))
                {
                    if (prefix != "." && !candidate.StartsWith(prefix + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
                    try
                    {
                        var full = repo.Resolve(candidate);
                        if (File.Exists(full)) Add(full, true);
                        else if (Directory.Exists(full)) skipped[candidate] = new(candidate, "nested_repository");
                        else skipped[candidate] = new(candidate, "missing");
                    }
                    catch (BridgeException e) when (e.Code is "unsupported_link" or "nested_repository" or "forbidden_path")
                    { skipped[candidate] = new(candidate, e.Code); }
                }
            }
            catch (Exception e) when (Expected(e)) { throw WithContext(e, i, operation.Path); }
        }
        var response = new Response(true, Context: new(Path.GetFileName(repo.Root), repo.Relative(repo.Cwd)),
            Files: pathsOnly ? null : files.Values.ToArray(), Skipped: skipped.Values.ToArray(),
            Paths: pathsOnly ? paths.ToArray() : null);
        CheckResponse(response);
        return response;

        void Add(string full, bool recursive)
        {
            if (seen.Contains(full)) return;
            var relative = repo.Relative(full);
            if (pathsOnly)
            {
                paths.Add(relative);
                seen.Add(full);
                return;
            }
            try
            {
                var bytes = ReadBytes(full);
                var (text, _) = Decode(bytes);
                total += bytes.Length;
                if (total > Protocol.BatchLimit) throw new BridgeException("limit_exceeded", "Read content exceeds 10 MiB.");
                files.Add(relative, new(relative, text));
                seen.Add(full);
                skipped.Remove(relative);
            }
            catch (BridgeException e) when (recursive && e.Code == "unsupported_text") { skipped[relative] = new(relative, e.Code); }
        }
    }

    private sealed record Staged(string FullPath, string Relative, byte[]? Original, byte[] Updated, MutationResult Result);

    private Response Mutate(Repository repo, List<Operation> operations)
    {
        var staged = new List<Staged>();
        var targets = new HashSet<string>(Repository.PathComparer);
        long total = 0;
        for (var i = 0; i < operations.Count; i++)
        {
            var op = operations[i];
            try
            {
                var full = repo.Resolve(op.Path);
                if (!targets.Add(full)) throw new BridgeException("duplicate_target", "Each file may be mutated only once per batch.");
                byte[]? original = null;
                byte[] updated;
                if (op.Type == "create")
                {
                    if (Repository.Exists(full)) throw new BridgeException("already_exists", "Create target already exists.");
                    updated = Encode(op.Content!, false);
                }
                else
                {
                    if (Directory.Exists(full)) throw new BridgeException("not_a_file", "Write target is a directory.");
                    if (!File.Exists(full)) throw new BridgeException("not_found", "Write target does not exist.");
                    original = ReadBytes(full);
                    var (text, bom) = Decode(original);
                    for (var changeIndex = 0; changeIndex < op.Changes!.Count; changeIndex++)
                    {
                        var change = op.Changes[changeIndex];
                        var first = text.IndexOf(change.Old, StringComparison.Ordinal);
                        if (first < 0) throw new BridgeException("old_not_found", "old matches 0 times; expected exactly 1.") { ChangeIndex = changeIndex };
                        if (text.IndexOf(change.Old, first + 1, StringComparison.Ordinal) >= 0)
                            throw new BridgeException("old_not_unique", "old matches more than once; expected exactly 1.") { ChangeIndex = changeIndex };
                        text = string.Concat(text.AsSpan(0, first), change.New, text.AsSpan(first + change.Old.Length));
                        if (Protocol.Utf8.GetByteCount(text) > Protocol.FileLimit) throw new BridgeException("limit_exceeded", "File exceeds 1 MiB.");
                    }
                    updated = Encode(text, bom);
                }
                total += (original?.Length ?? 0) + updated.Length;
                if (total > Protocol.BatchLimit) throw new BridgeException("limit_exceeded", "Original and updated mutation content exceeds 10 MiB.");
                // Detect file/directory conflicts, including parents that do not exist yet.
                foreach (var other in staged)
                    if (IsChild(full, other.FullPath) || IsChild(other.FullPath, full)) throw new BridgeException("invalid_path", "Mutation targets conflict as file and directory.");
                for (var parent = Path.GetDirectoryName(full); parent is not null && !Repository.PathComparer.Equals(parent, repo.Root); parent = Path.GetDirectoryName(parent))
                    if (File.Exists(parent)) throw new BridgeException("not_a_file", "A parent path is a file.");
                staged.Add(new(full, repo.Relative(full), original, updated, new(i, op.Type, repo.Relative(full), op.Changes?.Count)));
            }
            catch (Exception e) when (Expected(e)) { throw WithContext(e, i, op.Path); }
        }
        var success = new Response(true, "committed", Results: staged.Select(s => s.Result).ToArray());
        CheckResponse(success); // No response-size failures after a successful commit.
        BeforeCommit?.Invoke();
        foreach (var item in staged) Verify(item);
        var touched = new List<Staged>();
        var directories = new List<string>();
        Staged? committing = null;
        try
        {
            foreach (var item in staged)
            {
                committing = item;
                BeforeWrite?.Invoke(item.Result.OperationIndex);
                Verify(item);
                EnsureParents(Path.GetDirectoryName(item.FullPath)!);
                // Open existing files without truncation; CreateNew cannot overwrite a racing create.
                using var stream = new FileStream(item.FullPath, item.Original is null ? FileMode.CreateNew : FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (item.Original is not null)
                {
                    if (stream.Length != item.Original.Length) throw new BridgeException("concurrent_change", "File changed during commit.");
                    var actual = new byte[item.Original.Length];
                    stream.ReadExactly(actual);
                    if (!actual.AsSpan().SequenceEqual(item.Original)) throw new BridgeException("concurrent_change", "File changed during commit.");
                    stream.Position = 0;
                }
                touched.Add(item); // Also restore a file if its write fails halfway through.
                stream.Write(item.Updated);
                stream.SetLength(item.Updated.Length);
                stream.Flush(true);
            }
            return success;
        }
        catch (Exception e) when (Expected(e))
        {
            var failedRestore = false;
            foreach (var item in touched.AsEnumerable().Reverse())
            {
                try
                {
                    BeforeRestore?.Invoke(item.Result.OperationIndex);
                    repo.Resolve(item.Relative);
                    if (item.Original is null) File.Delete(item.FullPath);
                    else File.WriteAllBytes(item.FullPath, item.Original);
                }
                catch (Exception restoreError) when (Expected(restoreError)) { failedRestore = true; }
            }
            foreach (var directory in directories.AsEnumerable().Reverse())
            {
                try { repo.Resolve(repo.Relative(directory)); Directory.Delete(directory, false); }
                catch (Exception restoreError) when (Expected(restoreError)) { failedRestore = true; }
            }
            return new(false, failedRestore ? "indeterminate" : touched.Count == 0 && directories.Count == 0 ? "unchanged" : "rolled_back",
                new(failedRestore ? "rollback_failed" : "commit_failed",
                    failedRestore ? "Commit failed and rollback was incomplete. Inspect all batch targets before continuing." : $"Commit failed ({Describe(e).Code}). No batch changes remain.",
                    committing?.Result.OperationIndex, Path: committing?.Relative));
        }

        void Verify(Staged item)
        {
            try
            {
                repo.Resolve(item.Relative);
                if (item.Original is null ? Repository.Exists(item.FullPath) : !File.Exists(item.FullPath) || !ReadBytes(item.FullPath).AsSpan().SequenceEqual(item.Original))
                    throw new BridgeException("concurrent_change", "Target changed after simulation.");
            }
            catch (Exception e) when (Expected(e)) { throw WithContext(e, item.Result.OperationIndex, item.Relative); }
        }
        void EnsureParents(string directory)
        {
            if (Directory.Exists(directory)) return;
            EnsureParents(Path.GetDirectoryName(directory)!);
            repo.Resolve(repo.Relative(directory));
            Directory.CreateDirectory(directory);
            directories.Add(directory);
        }
    }

    private static bool IsChild(string candidate, string parent) => candidate.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static byte[] ReadBytes(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > Protocol.FileLimit) throw new BridgeException("limit_exceeded", "File exceeds 1 MiB.");
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
    private static (string Text, bool Bom) Decode(byte[] bytes)
    {
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        try
        {
            var text = Protocol.Utf8.GetString(bytes.AsSpan(bom ? 3 : 0));
            if (text.Contains('\0')) throw new BridgeException("unsupported_text", "Only UTF-8 text without NUL is supported.");
            return (text, bom);
        }
        catch (DecoderFallbackException) { throw new BridgeException("unsupported_text", "Only UTF-8 text is supported."); }
    }
    private static byte[] Encode(string text, bool bom)
    {
        if (text.Contains('\0')) throw new BridgeException("unsupported_text", "NUL is unsupported.");
        var bytes = Protocol.Utf8.GetBytes(text);
        if (bom) bytes = [0xef, 0xbb, 0xbf, .. bytes];
        if (bytes.Length > Protocol.FileLimit) throw new BridgeException("limit_exceeded", "File exceeds 1 MiB.");
        return bytes;
    }
    private static void CheckResponse(Response response)
    {
        if (Encoding.UTF8.GetByteCount(response.ToJson()) > Protocol.BatchLimit) throw new BridgeException("limit_exceeded", "JSON response exceeds 10 MiB; request a smaller selection.");
    }
    private static bool Expected(Exception e) => e is BridgeException or JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException;
    private static Failure Describe(Exception e) => e switch
    {
        BridgeException bridge => bridge.Failure,
        JsonException => new("invalid_json", "Clipboard does not contain valid JSON."),
        UnauthorizedAccessException => new("access_denied", "Filesystem access denied."),
        FileNotFoundException or DirectoryNotFoundException => new("not_found", "File or directory does not exist."),
        IOException => new("io_error", "Filesystem operation failed."),
        _ => new("invalid_request", "Request contains an unsupported value.")
    };
    private static BridgeException WithContext(Exception e, int index, string? path)
    {
        var result = e as BridgeException ?? new BridgeException(Describe(e).Code, Describe(e).Message);
        result.OperationIndex ??= index;
        result.TargetPath ??= path;
        return result;
    }
}
