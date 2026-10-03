using System.Diagnostics;
using System.Text;

namespace Agentic.Orchestrator.Tools;

public sealed record ToolInvocation(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, TimeSpan Timeout);

public sealed record ToolResult(int ExitCode, string Output, TimeSpan Duration, bool TimedOut)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut;

    public string Tail(int lines = 40) => string.Join('\n', Output.ReplaceLineEndings("\n").Split('\n').TakeLast(lines));
}

public interface IToolRunner
{
    Task<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

public sealed class ToolPolicyViolationException(string message) : Exception(message);

/// <summary>Runs a process without a shell (no injection surface), with a hard timeout.</summary>
public sealed class ProcessToolRunner : IToolRunner
{
    public async Task<ToolResult> RunAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(invocation.FileName)
        {
            WorkingDirectory = invocation.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in invocation.Arguments)
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";

        var output = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(invocation.Timeout);
        var timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            cancellationToken.ThrowIfCancellationRequested();
        }

        lock (output)
            return new ToolResult(timedOut ? -1 : process.ExitCode, output.ToString(), stopwatch.Elapsed, timedOut);
    }
}
