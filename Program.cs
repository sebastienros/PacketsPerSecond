using CommandLine;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime;
using System.Threading;
using System.Threading.Tasks;

namespace PacketsPerSecond
{
    class Program
    {
        private const int _port = 5201;
        private const int _backlog = 1024;

        private static readonly byte[] _payload = new byte[] { 1 };

        private static Stopwatch _stopwatch = Stopwatch.StartNew();
        private static int _connections;
        private static long _packets;

        private static Options _options;

        private class Options
        {
            [Option('s', "server")]
            public bool Server { get; set; }

            [Option('c', "client")]
            public string Client { get; set; }

            [Option('P', "parallel", Default = 1)]
            public int Parallel { get; set; }
        }

        static int Main(string[] args)
        {
            var parser = new Parser(settings =>
            {
                settings.CaseInsensitiveEnumValues = true;
                settings.HelpWriter = Console.Out;
            });

            return parser.ParseArguments<Options>(args).MapResult(
                options => Run(options),
                _ => 1
            );
        }

        private static int Run(Options options)
        {
            _options = options;

            if (!_options.Server && _options.Client == null)
            {
                throw new InvalidOperationException("Either '-s' or '-c' is required");
            }

            if (_options.Server && _options.Client != null)
            {
                throw new InvalidOperationException("Cannot set both '-s' and '-c'");
            }

            if (!GCSettings.IsServerGC)
            {
                throw new InvalidOperationException("Server GC must be enabled");
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

        private static void RunServer()
        {
            using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                socket.Bind(new IPEndPoint(IPAddress.Any, _port));
                socket.Listen(_backlog);
                while (true)
                {
                    var handler = socket.Accept();

                    Interlocked.Increment(ref _connections);
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            var buffer = new byte[_payload.Length];
                            while (true)
                            {
                                handler.Receive(buffer);
                                handler.Send(_payload);
                                Interlocked.Increment(ref _packets);
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

        private static void RunClient()
        {
            var threads = new Thread[_options.Parallel];

            for (var i=0; i < _options.Parallel; i++)
            {
                var thread = new Thread(() =>
                {
                    using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                    {
                        socket.Connect(_options.Client, _port);
                        Interlocked.Increment(ref _connections);

                        try
                        {
                            var buffer = new byte[_payload.Length];
                            while (true)
                            {
                                socket.Send(_payload);
                                socket.Receive(buffer);
                                Interlocked.Increment(ref _packets);
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

        private static async Task WriteResults()
        {
            var lastPackets = (long)0;
            var lastElapsed = TimeSpan.Zero;

            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));

                var packets = _packets;
                var currentPackets = packets - lastPackets;
                lastPackets = packets;

                var elapsed = _stopwatch.Elapsed;
                var currentElapsed = elapsed - lastElapsed;
                lastElapsed = elapsed;

                WriteResult(packets, elapsed, currentPackets, currentElapsed);
            }
        }

        private static void WriteResult(long totalPackets, TimeSpan totalElapsed, long currentPackets, TimeSpan currentElapsed)
        {
            Console.WriteLine(
                $"{DateTime.UtcNow.ToString("o")}\tTot Req\t{totalPackets}" +
                $"\tCur PPS\t{Math.Round(currentPackets / currentElapsed.TotalSeconds)}" +
                $"\tAvg PPS\t{Math.Round(totalPackets / totalElapsed.TotalSeconds)}" +
                $"\tConn\t{_connections}");
        }
    }
}
