using CommandLine;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using PacketsPerSecond;
using Microsoft.Crank.EventSources;

const int Port = 5201;
const int Backlog = 1024;

byte[] payload = null;
var stopwatch = Stopwatch.StartNew();
int connections = 0;
int errors = 0;
var packets = new ThreadLocal<int>(() => 0, true);
Options options = null;

var parser = new Parser(settings =>
{
    settings.CaseInsensitiveEnumValues = true;
    settings.HelpWriter = Console.Out;
});

parser.ParseArguments<Options>(args).MapResult(
    o => { options = o; return 0; },
    _ => 1
);

return await Run();

async Task<int> Run()
{
    Console.WriteLine("Starting...");

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

    // Wait for Crank to connect to the event pipe
    BenchmarksEventSource.Register("packetspersecond/packetspersecond", Operations.Max, Operations.Sum, "Max packets per second", "Max packets per second", "n0");

    var writeResultsTask = WriteResults();

    if (options.Server)
    {
        await RunServer();
    }
    else
    {
        await RunClient();
    }

    return 0;
}

async Task RunServer()
{
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    socket.Bind(new IPEndPoint(IPAddress.Any, Port));
    socket.Listen(Backlog);
    
    while (true)
    {
        // Waits for an incoming connection
        var handler = await socket.AcceptAsync();

        Interlocked.Increment(ref connections);
        var taskCount = 0;
        var task = new Task(async () =>
        {
            var taskIndex = Interlocked.Increment(ref taskCount);
            
            try
            {
                var buffer = new byte[payload.Length];
                while (true)
                {
                    var received = await handler.ReceiveAsync(buffer);
                    var sent = await handler.SendAsync(payload);
                    if (sent == payload.Length && received == payload.Length)
                    {   
                        packets.Value++;
                    }
                    else
                    {
                        Interlocked.Increment(ref errors);
                    }
                }
            }
            catch (SocketException)
            {
                // Ignore exception when client disconnects
                Interlocked.Increment(ref errors);
            }

            Interlocked.Decrement(ref connections);
        });
        task.Start();
    }
}

async Task RunClient()
{
    BenchmarksEventSource.Register("packetspersecond/threads", Operations.First, Operations.Sum, "Threads", "Threads)", "n0");
    BenchmarksEventSource.Register("packetspersecond/duration", Operations.First, Operations.Sum, "Duration (s)", "Duration (s)", "n0");
    BenchmarksEventSource.Register("packetspersecond/length", Operations.First, Operations.Sum, "Length (B)", "Length (B)", "n0");

    BenchmarksEventSource.Measure("packetspersecond/threads", options.Threads);
    BenchmarksEventSource.Measure("packetspersecond/duration", options.Duration);
    BenchmarksEventSource.Measure("packetspersecond/length", options.Length);

    var tasks = new Task[options.Threads];
    var stop = false;

    for (var i = 0; i < options.Threads; i++)
    {
        var task = new Task(async () =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(options.Client, Port);
            Interlocked.Increment(ref connections);

            try
            {
                var buffer = new byte[payload.Length];
                while (true)
                {
                    var sent = await socket.SendAsync(payload);
                    var received = await socket.ReceiveAsync(buffer);

                    if (sent == payload.Length && received == payload.Length)
                    {   
                        packets.Value++;
                    }
                    else
                    {
                        Interlocked.Increment(ref errors);
                    }

                    if (stop) break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Interlocked.Increment(ref errors);
            }

            Interlocked.Decrement(ref connections);
        });

        tasks[i] = task;
        task.Start();
    }

    if (options.Duration > 0)
    {
        await Task.Delay(TimeSpan.FromSeconds(options.Duration));
        stop = true;
    }

    Task.WaitAll(tasks);
}

async Task WriteResults()
{
    var lastPackets = (long)0;
    var lastElapsed = TimeSpan.Zero;

    while (true)
    {
        await Task.Delay(TimeSpan.FromSeconds(1));

        var p = packets.Values.Sum();
        var currentPackets = p - lastPackets;
        lastPackets = p;

        var elapsed = stopwatch.Elapsed;
        var currentElapsed = elapsed - lastElapsed;
        lastElapsed = elapsed;

        WriteResult(p, elapsed, currentPackets, currentElapsed, errors);
    }
}

void WriteResult(long totalPackets, TimeSpan totalElapsed, long currentPackets, TimeSpan currentElapsed, int totalErrors)
{
    var currentPPS = Math.Round(currentPackets / currentElapsed.TotalSeconds);

    BenchmarksEventSource.Measure("packetspersecond/packetspersecond", currentPPS);
    
    Console.WriteLine(
        $"{DateTime.UtcNow.ToString("o")}\tTot Req\t{totalPackets}" +
        $"\tCur PPS\t{currentPPS}" +
        $"\tAvg PPS\t{Math.Round(totalPackets / totalElapsed.TotalSeconds)}" +
        $"\tConn\t{connections}" +
        $"\tErr:\t{errors}");
}

