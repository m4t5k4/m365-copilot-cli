using CopilotFs.Core;

namespace CopilotFs;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--help"])
        {
            Console.WriteLine("""
                copilot-fs — local filesystem bridge for Copilot (Windows, protocol v1)

                Usage: copilot-fs [--help | --version]

                Copy a version 1 JSON request to the clipboard, then run in your Git
                repository. The JSON response replaces the clipboard contents.
                Supports tree, read, or write/create batches. Tree lists all file paths
                recursively using Git exclusions, without reading file contents.
                Explicit paths are repository-relative; omitted paths use the current directory.
                Mutations are simulated first, then committed with best-effort rollback.

                Exit: 0 success, 1 rejected/rolled back, 2 incomplete rollback,
                      3 clipboard failure. Inspect terminal output before retrying.
                """);
            return 0;
        }
        if (args is ["--version"]) { Console.WriteLine("copilot-fs 1.0.0 (protocol 1)"); return 0; }
        if (args.Length != 0) { Console.Error.WriteLine("Unknown arguments. Use copilot-fs --help."); return 1; }
        return ClipboardWorkflow.Run(
            () => Clipboard.GetText(TextDataFormat.UnicodeText),
            text => Clipboard.SetDataObject(text, true, 5, 100),
            Console.Out, Environment.CurrentDirectory);
    }
}
