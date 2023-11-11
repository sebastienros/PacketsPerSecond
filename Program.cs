using CommandLine;
using Microsoft.Crank.EventSources;
using PacketsPerSecond;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

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

    payload = new byte[options.Length];
    for (int i = 0; i < options.Length; i++)
    {
        payload[i] = 1;
    }

    // Wait for Crank to connect to the event pipe
    BenchmarksEventSource.Register("packetspersecond/packetspersecond", Operations.Max, Operations.Sum, "Max packets per second", "Max packets per second", "n0");
    BenchmarksEventSource.Register("packetspersecond/bandwidth", Operations.Max, Operations.Sum, "Max bandwidth (Gb/s)", "Max bandwidth (Gb/s)", "n2");

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
    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    socket.NoDelay = true;
    socket.Bind(new IPEndPoint(IPAddress.Any, Port));
    socket.Listen(Backlog);

    // Waits for all incoming connections

    Console.WriteLine("Waiting for client connections...");

    var handlers = new List<Socket>(options.Threads);
    while (handlers.Count < options.Threads)
    {
        var handler = socket.Accept();
        handler.NoDelay = true;

        handlers.Add(handler);
    }

    Console.WriteLine($"All clients are connected ({handlers.Count})...");

    foreach (var handler in handlers)
    {
        Interlocked.Increment(ref connections);
        var taskCount = 0;
        var thread = new Thread((state) =>
        {
            var taskIndex = Interlocked.Increment(ref taskCount);
            
            try
            {
                var length = payload.Length;
                var buffer = new byte[length];
                while (true)
                {
                    var received = Receive(handler, buffer, length);
                    var sent = Send(handler, payload, length);
                    if (sent == length && received == length)
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
        
        thread.Start();
    }
}

void RunClient()
{
    BenchmarksEventSource.Register("packetspersecond/threads", Operations.First, Operations.Sum, "Threads", "Threads)", "n0");
    BenchmarksEventSource.Register("packetspersecond/duration", Operations.First, Operations.Sum, "Duration (s)", "Duration (s)", "n0");
    BenchmarksEventSource.Register("packetspersecond/length", Operations.First, Operations.Sum, "Length (B)", "Length (B)", "n0");

    BenchmarksEventSource.Measure("packetspersecond/threads", options.Threads);
    BenchmarksEventSource.Measure("packetspersecond/duration", options.Duration);
    BenchmarksEventSource.Measure("packetspersecond/length", options.Length);

    var threads = new Thread[options.Threads];
    var cancellationTokenSource = new CancellationTokenSource();
    var cancellationToken = cancellationTokenSource.Token;

    var signal = new ManualResetEvent(false);

    for (var i = 0; i < options.Threads; i++)
    {
        var thread = new Thread(() =>
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            socket.NoDelay = true;
            socket.Connect(options.Client, Port);
            var created = Interlocked.Increment(ref connections);

            // Start communications once all connections are created

            if (created == options.Threads)
            {
                Console.WriteLine($"All clients are connected ({created})...");
                signal.Set();
            }

            try
            {
                var length = payload.Length;
                var buffer = new byte[length];

                signal.WaitOne();

                while (true)
                {
                    var sent = Send(socket, payload, length);
                    var received = Receive(socket, buffer, length);
                    
                    if (sent == length && received == length)
                    {   
                        packets.Value++;
                    }
                    else
                    {
                        Interlocked.Increment(ref errors);
                    }

                    if (cancellationToken.IsCancellationRequested) break;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                Interlocked.Increment(ref errors);
            }

            Interlocked.Decrement(ref connections);
        });

        threads[i] = thread;
        thread.Start();
    }

    if (options.Duration > 0)
    {
        signal.WaitOne();
        Console.WriteLine($"Running for {options.Duration}s");
        cancellationTokenSource.CancelAfter(TimeSpan.FromSeconds(options.Duration));
    }

    foreach (var thread in threads)
    {
        thread.Join();
    }
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
int Receive(Socket handler, byte[] buffer, int toRead)
{
    var read = 0;
    while (read < toRead)
    {
        var received = handler.Receive(buffer, read, toRead - read, SocketFlags.None);
        if (received > 0)
        {
            read += received;
        }
        else
        {
            // No more data to read
            break;
        }
    }

    return read;
}

[MethodImpl(MethodImplOptions.AggressiveInlining)]
int Send(Socket handler, byte[] buffer, int toWrite)
{
    var sent = 0;
    while (sent < toWrite)
    {
        var written = handler.Send(buffer, sent, toWrite - sent, SocketFlags.None);
        if (written > 0)
        {
            sent += written;
        }
        else
        {
            // No more data to read
            break;
        }
    }

    return sent;
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
        Interlocked.Exchange(ref errors, 0);
    }
}

void WriteResult(long totalPackets, TimeSpan totalElapsed, long currentPackets, TimeSpan currentElapsed, int totalErrors)
{
    var currentRps = Math.Round(currentPackets / currentElapsed.TotalSeconds);
    var bandwidth = Math.Round(currentRps * options.Length * 8 / 1_000_000_000, 2);

    BenchmarksEventSource.Measure("packetspersecond/packetspersecond", currentRps);
    BenchmarksEventSource.Measure("packetspersecond/bandwidth", bandwidth);

    Console.WriteLine(
        $"{DateTime.UtcNow:o}" +
        $"\tCur RPS\t{currentRps}" +
        $"\t{bandwidth} Gb/s" +
        $"\tConn\t{connections}" +
        $"\tErr:\t{errors}");
}
