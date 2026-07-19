using System.CommandLine;
using System.Text;
using Reactor.Cli;

Console.OutputEncoding = Encoding.UTF8;

var root = new RootCommand("Reactor — DOT-defined AI pipelines (an implementation of the Attractor spec)")
{
    PipelineCommands.Run(),
    PipelineCommands.Resume(),
    PipelineCommands.Validate(),
    PipelineCommands.Render(),
    HostCommands.Serve(),
    HostCommands.Agent(),
    HostCommands.Models(),
    HostCommands.Doctor(),
};

return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) }).ConfigureAwait(false);
