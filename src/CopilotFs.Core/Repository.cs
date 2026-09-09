using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CopilotFs.Core;

internal sealed class Repository
{
    internal string Root { get; }
    internal string Cwd { get; }
    private string[]? submodules;
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal Repository(string cwd)
    {
        Cwd = Path.GetFullPath(cwd);
        var current = new DirectoryInfo(Cwd);
        while (current is not null && !Exists(Path.Combine(current.FullName, ".git"))) current = current.Parent;
        Root = current?.FullName ?? throw new BridgeException("repository_not_found", "No .git found above the working directory.");
        // Reject linked ancestors too: lexical containment alone does not establish physical containment.
        for (var parent = new DirectoryInfo(Cwd); parent is not null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new BridgeException("unsupported_link", "Linked repository paths are unsupported.");
    }

    internal string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
    internal string Resolve(string? path)
    {
        path ??= Relative(Cwd);
        if (Path.IsPathRooted(path) || path.Contains('\\') || path.Contains(':')) throw new BridgeException("invalid_path", "Use repository-relative paths with forward slashes.");
        var parts = path.Split('/');
        foreach (var part in parts)
        {
            if (part == "." && parts.Length == 1) continue;
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.Any(c => c < 32 || "<>\"|?*".Contains(c)))
                throw new BridgeException("invalid_path", "Invalid path segment.");
            if (part.Equals(".git", StringComparison.OrdinalIgnoreCase)) throw new BridgeException("forbidden_path", ".git is inaccessible.");
            var stem = part.Split('.')[0];
            if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
                throw new BridgeException("invalid_path", "Device paths are unsupported.");
        }
        var full = Path.GetFullPath(Path.Combine(Root, path));
        var relative = Path.GetRelativePath(Root, full);
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new BridgeException("outside_repository", "Path leaves the repository.");
        submodules ??= RunGit("ls-files", "--stage", "-z")
            .Where(entry => entry.StartsWith("160000 ", StringComparison.Ordinal))
            .Select(entry => entry[(entry.IndexOf('\t') + 1)..]).ToArray();
        var portable = relative.Replace('\\', '/');
        if (submodules.Any(module => Repository.PathComparer.Equals(module, portable) || portable.StartsWith(module + "/", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
            throw new BridgeException("nested_repository", "Submodules are inaccessible from this repository.");
        var cursor = Root;
        CheckLink(cursor);
        foreach (var part in parts.Where(p => p != "."))
        {
            cursor = Path.Combine(cursor, part);
            CheckLink(cursor);
            if (Directory.Exists(cursor) && Exists(Path.Combine(cursor, ".git")))
                throw new BridgeException("nested_repository", "Nested repositories are inaccessible from this repository.");
        }
        return full;
    }

    internal static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void CheckLink(string path)
    {
        if (!Exists(path)) return;
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new BridgeException("unsupported_link", "Symlinks and junctions are unsupported.");
        if (OperatingSystem.IsWindows() && (attributes & FileAttributes.Directory) == 0)
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(file, out var info)) throw new IOException("Cannot inspect file links.", new Win32Exception(Marshal.GetLastWin32Error()));
            if (info.NumberOfLinks > 1) throw new BridgeException("unsupported_link", "Hard-linked files are unsupported.");
        }
    }

    internal string[] Files() => RunGit("ls-files", "--cached", "--others", "--exclude-standard", "-z");

    private string[] RunGit(params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        // Never let inherited repository overrides redirect discovery to another checkout.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.ArgumentList.Add("--no-optional-locks");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(start) ?? throw new BridgeException("git_unavailable", "Cannot start Git.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000)) { process.Kill(true); throw new BridgeException("git_failed", "Git enumeration timed out."); }
            Task.WaitAll(output, error);
            if (process.ExitCode != 0) throw new BridgeException("git_failed", "Git could not enumerate repository files.");
            return output.Result.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Win32Exception) { throw new BridgeException("git_unavailable", "Git must be installed and available on PATH."); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}
