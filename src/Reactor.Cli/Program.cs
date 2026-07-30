using System.CommandLine;
using System.Text;
using Reactor.Cli;

Console.OutputEncoding = Encoding.UTF8;

var root = new RootCommand("Reactor — DOT-defined AI pipelines (an implementation of the Attractor spec)")
{
    PipelineCommands.Run(),
    PipelineCommands.Resume(),
    PipelineCommands.Validate(),
    SimulateCommand.Create(),
    PipelineCommands.Render(),
    HostCommands.Serve(),
    HostCommands.Studio(),
    HostCommands.Agent(),
    HostCommands.Models(),
    HostCommands.Doctor(),
};

var parsed = root.Parse(args);
var exitCode = await parsed.InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) }).ConfigureAwait(false);

// Usage errors (unknown options, invalid values) print help and exit with 1, which reads as "pipeline failed".
return parsed.Errors.Count > 0 && exitCode == 1 ? ExitCodes.InvalidInput : exitCode;
