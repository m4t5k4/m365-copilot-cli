namespace CopilotFs.Core;

public static class ClipboardWorkflow
{
    public static int Run(Func<string> readClipboard, Action<string> writeClipboard, TextWriter terminal, string cwd)
    {
        string input;
        try { input = readClipboard(); }
        catch (Exception)
        {
            terminal.WriteLine(new Response(false, "unchanged", new("clipboard_read_failed", "Cannot read clipboard. No operations executed.")).ToJson());
            return 3;
        }
        var response = new Bridge().Execute(input, cwd);
        var json = response.ToJson();
        try { writeClipboard(json); }
        catch (Exception)
        {
            terminal.WriteLine("clipboard_write_failed: response could not be copied. Execution result follows; do not rerun a committed batch.");
            terminal.WriteLine(json);
            return response.MutationState == "indeterminate" ? 2 : 3;
        }
        if (response.Ok)
            terminal.WriteLine(response.Paths is not null
                ? $"Repository: {response.Context!.Repository}. Tree: {response.Paths.Count} files; {response.Skipped!.Count} skipped."
                : response.Files is not null
                ? $"Repository: {response.Context!.Repository}. Read: {response.Files.Count} files; {response.Skipped!.Count} skipped."
                : $"Committed: {response.Results!.Count(r => r.Type == "write")} written, {response.Results!.Count(r => r.Type == "create")} created.");
        else terminal.WriteLine($"{response.Error!.Code}: {response.Error.Message} Mutation state: {response.MutationState}.");
        terminal.WriteLine("Response is on the clipboard.");
        return response.Ok ? 0 : response.MutationState == "indeterminate" ? 2 : 1;
    }
}
