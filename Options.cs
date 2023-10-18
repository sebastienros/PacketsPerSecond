using CommandLine;

namespace PacketsPerSecond;

public class Options
{
    [Option('s', "server")]
    public bool Server { get; set; }

    [Option('c', "client")]
    public string Client { get; set; }

    [Option('P', "parallel", Default = 1)]
    public int Parallel { get; set; }

    [Option('L', "length", Default = 1)]
    public int Length { get; set; }
}
