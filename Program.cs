using CommandLine;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using PacketsPerSecond;

const int Port = 5201;
const int Backlog = 1024;

byte[] payload = null;
var stopwatch = Stopwatch.StartNew();
int connections = 0;
long packets = 0;
Options options;

var parser = new Parser(settings =>
{
    settings.CaseInsensitiveEnumValues = true;
    settings.HelpWriter = Console.Out;
});

return parser.ParseArguments<Options>(args).MapResult(
    o => { options = o; return Run(); },
    _ => 1
);

int Run()
{
    if (!options.Server && options.Client == null)
    {
        throw new InvalidOperationException("Either '-s' or '-c' is required");
    }

    if (options.Server && options.Client != null)
    {
        throw new InvalidOperationException("Cannot set both '-s' and '-c'");
    }

    if (!GCSettings.IsServerGC)
    {
        throw new InvalidOperationException("Server GC must be enabled");
    }

    payload = new byte[options.Length];
    for (int i = 0; i < options.Length; i++)
    {
        payload[i] = 1;
    }
    
    var writeResultsTask = WriteResults();

    if (options.Server)
    {
        RunServer();
    }
    else
    {
        RunClient();
    }

    return 0;
}

void RunServer()
{
    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
    {
        socket.Bind(new IPEndPoint(IPAddress.Any, Port));
        socket.Listen(Backlog);
        while (true)
        {
            var handler = socket.Accept();

            Interlocked.Increment(ref connections);
            var thread = new Thread(() =>
            {
                try
                {
                    var buffer = new byte[payload.Length];
                    while (true)
                    {
                        handler.Receive(buffer);
                        handler.Send(payload);
                        Interlocked.Increment(ref packets);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    Environment.Exit(1);
                }
            });
            thread.Start();
        }
    }
}

void RunClient()
{
    var threads = new Thread[options.Parallel];

    for (var i=0; i < options.Parallel; i++)
    {
        var thread = new Thread(() =>
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Connect(options.Client, Port);
                Interlocked.Increment(ref connections);

                try
                {
                    var buffer = new byte[payload.Length];
                    while (true)
                    {
                        socket.Send(payload);
                        socket.Receive(buffer);
                        Interlocked.Increment(ref packets);
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    Environment.Exit(1);
                }
            }
        });
        threads[i] = thread;
        thread.Start();
    }

    foreach (var thread in threads)
    {
        thread.Join();
    }
}

async Task WriteResults()
{
    var lastPackets = (long)0;
    var lastElapsed = TimeSpan.Zero;

    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));

        var p = packets;
        var currentPackets = p - lastPackets;
        lastPackets = packets;

        var elapsed = stopwatch.Elapsed;
        var currentElapsed = elapsed - lastElapsed;
        lastElapsed = elapsed;

        WriteResult(packets, elapsed, currentPackets, currentElapsed);
    }
}

void WriteResult(long totalPackets, TimeSpan totalElapsed, long currentPackets, TimeSpan currentElapsed)
{
    Console.WriteLine(
        $"{DateTime.UtcNow.ToString("o")}\tTot Req\t{totalPackets}" +
        $"\tCur PPS\t{Math.Round(currentPackets / currentElapsed.TotalSeconds)}" +
        $"\tAvg PPS\t{Math.Round(totalPackets / totalElapsed.TotalSeconds)}" +
        $"\tConn\t{connections}");
}

