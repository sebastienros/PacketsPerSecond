using CommandLine;

namespace PacketsPerSecond;

public class Options
{
    [Option('s', "server")]
    public bool Server { get; set; }

    [Option('c', "client")]
    public string Client { get; set; }

    [Option('T', "threads", Default = 1)]
    public int Threads { get; set; }

    [Option('L', "length", Default = 1)]
    public int Length { get; set; }

    [Option('D', "duration", Default = 0)]
    public int Duration { get; set; }
}
