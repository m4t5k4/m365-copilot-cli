using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CopilotFs.Core;

// Dependency-free executable test suite. Any failure returns a nonzero exit code.
var tests = new List<(string Name, Action<Fixture> Run)>
{
    ("explicit paths use root; omitted read uses cwd", f => {
        f.Put("root.txt", "root"); f.Put("sub/child.txt", "child");
        var response = f.Run(new { type = "read" }, cwd: "sub");
        Check(response.Ok && response.Files!.Count == 1 && response.Files[0].Path == "sub/child.txt");
        Check(f.Run(new { type = "read", path = "root.txt" }, cwd: "sub").Files![0].Content == "root");
    }),
    ("git exclusions, tracked ignored, negation and deleted files", f => {
        f.Put(".gitignore", "*.log\n!keep.log\nsub/ignored.txt\n");
        f.Put("tracked.log", "tracked"); f.Git("add", "-f", "tracked.log");
        f.Put("gone.txt", "gone"); f.Git("add", "gone.txt"); File.Delete(f.PathOf("gone.txt"));
        f.Put("drop.log", "ignored"); f.Put("keep.log", "kept"); f.Put("sub/ignored.txt", "ignored");
        f.Put(".git/info/exclude", "local.txt\n"); f.Put("local.txt", "ignored");
        var r = f.Run(new { type = "read" }); Check(r.Ok);
        var paths = r.Files!.Select(x => x.Path).ToArray();
        Check(paths.Contains("tracked.log") && paths.Contains("keep.log") && !paths.Contains("drop.log") && !paths.Contains("local.txt") && !paths.Contains("sub/ignored.txt"));
        Check(r.Skipped!.Any(s => s.Path == "gone.txt" && s.Reason == "missing"));
        Check(f.Run(new { type = "read", path = "drop.log" }).Ok);
    }),
    ("global Git excludes", f => {
        var global = System.IO.Path.Combine(f.Parent, "global-ignore"); File.WriteAllText(global, "global.txt\n");
        f.Git("config", "core.excludesFile", global); f.Put("global.txt", "skip");
        Check(!f.Run(new { type = "read" }).Files!.Any(x => x.Path == "global.txt"));
    }),
    ("overlapping reads deduplicate and sort", f => {
        f.Put("b.txt", "b"); f.Put("a.txt", "a");
        var r = f.Batch(new { type = "read", path = "." }, new { type = "read", path = "a.txt" });
        Check(r.Ok && r.Files!.Count == 2 && r.Files[0].Path == "a.txt");
    }),
    ("empty directory and missing target", f => {
        Directory.CreateDirectory(f.PathOf("empty")); Check(f.Run(new { type = "read", path = "empty" }).Files!.Count == 0);
        Error(f.Run(new { type = "read", path = "missing" }), "not_found");
    }),
    ("nontext skipped recursively but explicit access fails", f => {
        File.WriteAllBytes(f.PathOf("binary"), [0xff, 0]);
        Check(f.Run(new { type = "read" }).Skipped!.Single().Reason == "unsupported_text");
        Error(f.Run(new { type = "read", path = "binary" }), "unsupported_text");
    }),
    ("exact sequential replacements preserve BOM and CRLF", f => {
        File.WriteAllBytes(f.PathOf("a"), [0xef, 0xbb, 0xbf, .. Encoding.UTF8.GetBytes("one\r\ntwo\r\n")]);
        var r = f.Run(new { type = "write", path = "a", changes = new[] { new { old = "one", @new = "ONE" }, new { old = "ONE\r\ntwo", @new = "done" } } });
        Check(r.Ok && File.ReadAllBytes(f.PathOf("a")).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("done\r\n"))));
    }),
    ("replacement matching is case and newline sensitive", f => {
        f.Put("a", "Hello\r\n"); Error(f.Run(Write("a", "hello", "x")), "old_not_found");
        Error(f.Run(Write("a", "Hello\n", "x")), "old_not_found");
    }),
    ("overlapping occurrences are ambiguous", f => { f.Put("a", "aaa"); Error(f.Run(Write("a", "aa", "b")), "old_not_unique"); }),
    ("late validation failure leaves earlier targets unchanged", f => {
        f.Put("a", "old"); f.Put("b", "repeat repeat");
        var r = f.Batch(Write("a", "old", "new"), Write("b", "repeat", "new"));
        Error(r, "old_not_unique"); Check(r.Error!.OperationIndex == 1 && r.Error.ChangeIndex == 0 && File.ReadAllText(f.PathOf("a")) == "old");
    }),
    ("create parents and empty content", f => { Check(f.Run(new { type = "create", path = "new/deep/a", content = "" }).Ok); Check(File.Exists(f.PathOf("new/deep/a"))); }),
    ("create cannot overwrite file or directory", f => {
        f.Put("a", "original"); Error(f.Run(new { type = "create", path = "a", content = "x" }), "already_exists");
        Error(f.Run(new { type = "create", path = ".", content = "x" }), "already_exists");
    }),
    ("duplicate targets are rejected", f => {
        f.Put("a", "x"); Error(f.Batch(Write("a", "x", "y"), Write("A", "y", "z")), "duplicate_target");
    }),
    ("parent/child mutation targets conflict before commit", f => {
        Error(f.Batch(new { type = "create", path = "new", content = "x" }, new { type = "create", path = "new/child", content = "x" }), "invalid_path");
        Check(!File.Exists(f.PathOf("new")));
    }),
    ("invalid and administrative paths", f => {
        foreach (var path in new[] { "../outside", "C:/outside", "/outside", "a:stream", "a\\b", "a/../b", "a.", "a ", "NUL.txt", "*.cs", "a//b" })
            Error(f.Run(new { type = "create", path, content = "x" }), "invalid_path");
        Error(f.Run(new { type = "read", path = ".git/config" }), "forbidden_path");
    }),
    ("nested repositories are boundaries", f => {
        f.Put("nested/a", "x"); f.GitAt(f.PathOf("nested"), "init", "--quiet");
        Error(f.Run(new { type = "read", path = "nested/a" }), "nested_repository");
        Check(!f.Run(new { type = "read" }).Files!.Any(x => x.Path.StartsWith("nested/")));
        Check(f.Run(new { type = "read" }, cwd: "nested").Files!.Single().Content == "x");
    }),
    ("Git worktree .git file is supported", f => {
        f.Put("a", "x"); f.Git("add", "a"); f.Git("-c", "user.name=Tests", "-c", "user.email=tests@example.invalid", "commit", "--quiet", "-m", "fixture");
        var worktree = System.IO.Path.Combine(f.Parent, "worktree"); f.Git("worktree", "add", "--quiet", "--detach", worktree);
        Check(new Bridge().Execute(Request(new { type = "read" }), worktree).Files!.Single().Content == "x");
    }),
    ("precommit concurrent edit is not overwritten", f => {
        f.Put("a", "old"); var bridge = new Bridge { BeforeCommit = () => f.Put("a", "external") };
        Error(bridge.Execute(Request(Write("a", "old", "new")), f.Root), "concurrent_change"); Check(File.ReadAllText(f.PathOf("a")) == "external");
    }),
    ("write failure restores changed files and created directories", f => {
        f.Put("a", "old"); f.Put("b", "last");
        var bridge = new Bridge { BeforeWrite = i => { if (i == 2) throw new IOException("Injected failure"); } };
        var r = bridge.Execute(Request(Write("a", "old", "new"), new { type = "create", path = "new/deep/file", content = "created" }, Write("b", "last", "changed")), f.Root);
        Check(!r.Ok && r.MutationState == "rolled_back" && File.ReadAllText(f.PathOf("a")) == "old" && !Directory.Exists(f.PathOf("new")));
    }),
    ("rollback failure reports indeterminate", f => {
        f.Put("a", "old"); f.Put("b", "old");
        var bridge = new Bridge { BeforeWrite = i => { if (i == 1) throw new IOException(); }, BeforeRestore = _ => throw new IOException() };
        var r = bridge.Execute(Request(Write("a", "old", "new"), Write("b", "old", "new")), f.Root);
        Error(r, "rollback_failed"); Check(r.MutationState == "indeterminate");
    }),
    ("read request cannot contain mutations", f => { Error(f.Batch(new { type = "read" }, new { type = "create", path = "a", content = "x" }), "mixed_batch"); Check(!File.Exists(f.PathOf("a"))); }),
    ("strict JSON and fenced input", f => {
        foreach (var json in new[] { "{}", "{\"version\":1,\"version\":1,\"operations\":[{\"type\":\"read\"}]}", "{\"version\":1,\"operations\":[{\"type\":\"read\",\"extra\":true}]}", Request(new { type = "delete", path = "a" }), Request(new { type = "read", path = (string?)null }), Request(new { type = "write", path = "a", changes = new[] { new { old = "", @new = "x" } } }) })
            Error(new Bridge().Execute(json, f.Root), "invalid_request");
        Error(new Bridge().Execute("not json", f.Root), "invalid_json");
        Check(new Bridge().Execute("```json\n" + Request(new { type = "read" }) + "\n```", f.Root).Ok);
        Error(new Bridge().Execute("{\"version\":2,\"operations\":[{\"type\":\"read\"}]}", f.Root), "unsupported_version");
    }),
    ("file and response size limits are precommit", f => {
        f.Put("large", new string('a', 1024 * 1024 + 1)); Error(f.Run(new { type = "read", path = "large" }), "limit_exceeded");
        f.Put("a", "old"); Error(f.Batch(Write("a", "old", "new"), new { type = "create", path = "b", content = new string('b', 1024 * 1024 + 1) }), "limit_exceeded");
        Check(File.ReadAllText(f.PathOf("a")) == "old");
    }),
    ("clipboard roundtrip and failure after commit", f => {
        string? output = null; using var terminal = new StringWriter();
        Check(ClipboardWorkflow.Run(() => Request(new { type = "read" }), s => output = s, terminal, f.Root) == 0);
        Check(JsonDocument.Parse(output!).RootElement.GetProperty("ok").GetBoolean());
        var exit = ClipboardWorkflow.Run(() => Request(new { type = "create", path = "a", content = "x" }), _ => throw new IOException(), terminal, f.Root);
        Check(exit == 3 && File.Exists(f.PathOf("a")) && terminal.ToString().Contains("committed"));
        Check(ClipboardWorkflow.Run(() => throw new IOException(), _ => throw new Exception("Must not write"), terminal, f.Root) == 3);
    }),
    ("no repository returns structured failure", f => { Error(new Bridge().Execute(Request(new { type = "read" }), f.Parent), "repository_not_found"); })
};

tests.AddRange([
    ("tree recursively lists all eligible paths without reading content", f => {
        f.Put(".gitignore", "ignored/\n*.log\n!keep.log\n");
        f.Put("ignored/nested/a", "hidden"); f.Put("drop.log", "hidden"); f.Put("keep.log", "visible");
        f.Put("tracked.log", "visible"); f.Git("add", "-f", "tracked.log");
        f.Put("src/deep/a.cs", "code");
        File.WriteAllBytes(f.PathOf("binary"), [0xff, 0]); f.Put("large", new string('a', 1024 * 1024 + 1));
        f.Put(".git/info/exclude", "local.txt\n"); f.Put("local.txt", "hidden");
        var global = System.IO.Path.Combine(f.Parent, "global-ignore"); File.WriteAllText(global, "global.txt\n");
        f.Git("config", "core.excludesFile", global); f.Put("global.txt", "hidden");
        var r = f.Run(new { type = "tree" }); Check(r.Ok && r.Files is null);
        Check(r.Paths!.SequenceEqual(new[] { ".gitignore", "binary", "keep.log", "large", "src/deep/a.cs", "tracked.log" }));
    }),
    ("tree scope, overlaps, empty folders and target validation", f => {
        f.Put("a", "a"); f.Put("src/deep/b", "b"); Directory.CreateDirectory(f.PathOf("empty"));
        Check(f.Run(new { type = "tree" }, cwd: "src").Paths!.SequenceEqual(new[] { "src/deep/b" }));
        Check(f.Run(new { type = "tree", path = "." }, cwd: "src").Paths!.Count == 2);
        Check(f.Batch(new { type = "tree", path = "src" }, new { type = "tree", path = "src/deep" }).Paths!.Count == 1);
        Check(f.Run(new { type = "tree", path = "empty" }).Paths!.Count == 0);
        Error(f.Run(new { type = "tree", path = "a" }), "not_a_directory");
        Error(f.Run(new { type = "tree", path = "missing" }), "not_found");
        Error(f.Run(new { type = "tree", path = "../outside" }), "invalid_path");
        Error(f.Run(new { type = "tree", path = ".git" }), "forbidden_path");
    }),
    ("tree respects repository boundaries and omits deleted tracked files", f => {
        f.Put("nested/a", "x"); f.GitAt(f.PathOf("nested"), "init", "--quiet");
        f.Git("update-index", "--add", "--cacheinfo", "160000,1111111111111111111111111111111111111111,vendor"); f.Put("vendor/a", "x");
        f.Put("gone", "gone"); f.Git("add", "gone"); File.Delete(f.PathOf("gone"));
        var r = f.Run(new { type = "tree" }); Check(r.Ok && r.Paths!.Count == 0 && r.Skipped!.Any(s => s.Reason == "missing"));
        Error(f.Run(new { type = "tree", path = "nested" }), "nested_repository");
    }),
    ("tree batches are separate and clipboard output contains paths", f => {
        Error(f.Batch(new { type = "tree" }, new { type = "read" }), "mixed_batch");
        Error(f.Batch(new { type = "tree" }, new { type = "create", path = "a", content = "x" }), "mixed_batch");
        Check(!File.Exists(f.PathOf("a")));
        f.Put("a", "secret content"); string? output = null; using var terminal = new StringWriter();
        Check(ClipboardWorkflow.Run(() => Request(new { type = "tree" }), s => output = s, terminal, f.Root) == 0);
        Check(output!.Contains("\"paths\":[\"a\"]") && !output.Contains("secret content") && terminal.ToString().Contains("Tree: 1 files"));
    }),
    ("uninitialized submodule is still a boundary", f => {
        f.Git("update-index", "--add", "--cacheinfo", "160000,1111111111111111111111111111111111111111,vendor");
        f.Put("vendor/a", "external");
        Error(f.Run(new { type = "read", path = "vendor/a" }), "nested_repository");
        Error(f.Run(new { type = "create", path = "vendor/new", content = "x" }), "nested_repository");
        Check(f.Run(new { type = "read" }).Files!.Count == 0);
    }),
    ("invalid Unicode is a structured error", f => {
        Error(new Bridge().Execute("{\"version\":1,\"operations\":[{\"type\":\"create\",\"path\":\"a\",\"content\":\"\\uD800\"}]}", f.Root), "invalid_request");
    }),
    ("hardlinks cannot read or change external files", f => {
        var outside = System.IO.Path.Combine(f.Parent, "outside.txt"); File.WriteAllText(outside, "external");
        if (!Native.CreateHardLink(f.PathOf("linked"), outside, IntPtr.Zero)) throw new System.ComponentModel.Win32Exception();
        Error(f.Run(new { type = "read", path = "linked" }), "unsupported_link");
        Error(f.Run(Write("linked", "external", "changed")), "unsupported_link");
        Check(File.ReadAllText(outside) == "external");
    }),
    ("junction traversal is rejected, including create parents", f => {
        var outside = System.IO.Path.Combine(f.Parent, "outside"); Directory.CreateDirectory(outside); File.WriteAllText(System.IO.Path.Combine(outside, "a"), "external");
        var link = f.PathOf("linked");
        var start = new ProcessStartInfo("powershell") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{outside.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync(); process.WaitForExit(); Task.WaitAll(output, error);
        if (process.ExitCode != 0) throw new Exception(error.Result);
        f.Junctions.Add(link);
        Error(f.Run(new { type = "read", path = "linked/a" }), "unsupported_link");
        Error(f.Run(new { type = "create", path = "linked/new", content = "x" }), "unsupported_link");
        Check(!File.Exists(System.IO.Path.Combine(outside, "new")));
        var r = f.Run(new { type = "read" }); Check(r.Ok && r.Files!.Count == 0);
    }),
    ("oversized serialized response is rejected without partial content", f => {
        for (var i = 0; i < 6; i++) f.Put($"file{i}", new string('\u0001', 350_000));
        var response = f.Run(new { type = "read" }); Error(response, "limit_exceeded"); Check(response.Files is null);
    }),
    ("operation and request limits", f => {
        var operations = Enumerable.Range(0, 101).Select(_ => (object)new { type = "read" }).ToArray();
        Error(f.Batch(operations), "invalid_request");
        Error(new Bridge().Execute(new string(' ', 10 * 1024 * 1024 + 1), f.Root), "limit_exceeded");
    }),
    ("existing parent file prevents all mutations", f => {
        f.Put("parent", "file"); f.Put("a", "old");
        var r = f.Batch(Write("a", "old", "new"), new { type = "create", path = "parent/child", content = "x" });
        Check(!r.Ok && r.MutationState == "unchanged" && File.ReadAllText(f.PathOf("a")) == "old");
    }),
    ("write can remove text and no-op safely", f => {
        f.Put("a", "remove stay"); Check(f.Run(Write("a", "remove ", "")).Ok);
        Check(f.Run(Write("a", "stay", "stay")).Ok && File.ReadAllText(f.PathOf("a")) == "stay");
    }),
    ("later explicit nontext read cancels recursive read output", f => {
        f.Put("text", "ok"); File.WriteAllBytes(f.PathOf("binary"), [0xff]);
        var r = f.Batch(new { type = "read" }, new { type = "read", path = "binary" });
        Error(r, "unsupported_text"); Check(r.Files is null);
    }),
    ("read-only commit failure rolls back earlier writes", f => {
        f.Put("a", "old"); f.Put("b", "old"); File.SetAttributes(f.PathOf("b"), FileAttributes.ReadOnly);
        var r = f.Batch(Write("a", "old", "new"), Write("b", "old", "new"));
        Check(!r.Ok && r.MutationState == "rolled_back" && File.ReadAllText(f.PathOf("a")) == "old");
    })
]);

int failures = 0;
foreach (var (name, run) in tests)
{
    using var fixture = new Fixture();
    try { run(fixture); Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures++; Console.Error.WriteLine($"FAIL {name}: {e}"); }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} tests passed.");
return failures == 0 ? 0 : 1;

static object Write(string path, string old, string replacement) => new { type = "write", path, changes = new[] { new { old, @new = replacement } } };
static string Request(params object[] ops) => JsonSerializer.Serialize(new { version = 1, operations = ops });
static void Check(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static void Error(Response response, string code) { if (response.Ok || response.Error?.Code != code) throw new Exception($"Expected {code}: {response.ToJson()}"); }

sealed class Fixture : IDisposable
{
    public List<string> Junctions { get; } = [];
    public string Parent { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "copilot-fs-tests-" + Guid.NewGuid().ToString("N"));
    public string Root => System.IO.Path.Combine(Parent, "repo");
    public Fixture() { Directory.CreateDirectory(Root); Git("init", "--quiet"); }
    public string PathOf(string path) => System.IO.Path.Combine(Root, path);
    public void Put(string path, string content) { var full = PathOf(path); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!); File.WriteAllText(full, content, new UTF8Encoding(false)); }
    public Response Run(object operation, string? cwd = null) => new Bridge().Execute(JsonSerializer.Serialize(new { version = 1, operations = new[] { operation } }), cwd is null ? Root : PathOf(cwd));
    public Response Batch(params object[] operations) => new Bridge().Execute(JsonSerializer.Serialize(new { version = 1, operations }), Root);
    public void Git(params string[] args) => GitAt(Root, args);
    public void GitAt(string cwd, params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(); Task.WaitAll(stdout, stderr);
        if (process.ExitCode != 0) throw new Exception(stderr.Result);
    }
    public void Dispose()
    {
        // Only our unique, resolved fixture directory may be recursively deleted.
        var full = System.IO.Path.GetFullPath(Parent);
        var temp = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(full).StartsWith("copilot-fs-tests-", StringComparison.Ordinal)) throw new Exception("Unsafe fixture cleanup path.");
        foreach (var junction in Junctions) Directory.Delete(junction, false);
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(full, true);
    }
}

static class Native
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    internal static extern bool CreateHardLink(string name, string existing, IntPtr attributes);
}
