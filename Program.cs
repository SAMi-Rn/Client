namespace Client;

using System;

class Program
{
    public static void Main(string[] args)
    {   
        if (args.Length > 0 && string.Equals(args[0], "--worker", StringComparison.OrdinalIgnoreCase))
        {
            var code = ClientRunner.Run(args).GetAwaiter().GetResult();
            Environment.Exit(code);
        }
        
        var ctx = new FsmContext
        {
            Args = args
        };
        
        ctx.Verbose = args.Any(a => a.Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
        
        var fsm = new FsmHandler(ctx);
        fsm.IterateFSMStates();
    }
}


public static class Log
{
    public static void In(string text)  => Console.WriteLine($"<- {text}");
    public static void Out(string text) => Console.WriteLine($"-> {text}");
    public static void Info(string text)=> Console.WriteLine(text);
}


public static class ClientRunner
{
    // Runs the client in worker mode
    public static async Task<int> Run(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("Usage: --worker <serverHost> <serverPort> <listenPort> [threads]");
            return -1;
        }

        string serverHost = args[1];
        int serverPort = int.Parse(args[2]);
        int listenPort = int.Parse(args[3]);
        int threads = (args.Length >= 5 ? int.Parse(args[4]) : Environment.ProcessorCount);

        string nodeId = $"c-{Environment.MachineName}";
        string listenHost = GetOutboundIp(serverHost);
        
        using var cancellationToken = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true; 
            cancellationToken.Cancel();
        };
        
        var acceptConnection = Protocol.AcceptHelloAsync(listenPort, nodeId, cancellationToken.Token);
        
        var register = new ClientRegister(nodeId, listenHost, listenPort, threads);
        Protocol.RegisterAsync(serverHost, serverPort, register, cancellationToken.Token).GetAwaiter().GetResult();
        
        var channel = await acceptConnection;
        if (channel is null)
        {
            Log.Info("Handshake failed: no/invalid SERVER_HELLO.");
            return -1;
        }
        using var dialBack = channel.Value.Client;
        var reader = channel.Value.Reader;
        var stream = dialBack.GetStream();
        
        try
        {
            Cracker.EnsureCryptRaLoaded();
        }
        catch (Exception ex)
        {
            Log.Info($"crypt binding failed: {ex.Message}");
            return -1;
        }
        
        await Protocol.ReceiveJobsAsync(
            reader,
            stream,
            onWork: job => BatchCracker.Run(
                job,
                threads,
                job.CheckpointEvery,
                async (tried, lastIndex) =>
                {
                    var ck = new Checkpoint(job.JobId, tried, lastIndex, DateTimeOffset.UtcNow);
                    await Json.SendLineAsync(stream, new { type = Kinds.Checkpoint, body = ck }, cancellationToken.Token);
                    Log.Out($"{Kinds.Checkpoint} job={ck.JobId} tried={ck.Tried} lastIndex={ck.LastIndex}");
                },
                cancellationToken.Token),
            cancellationToken: cancellationToken.Token
        );
        return 0;
    }

    public static string GetOutboundIp(string remoteHost)
    {
        try
        {
            using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
                System.Net.Sockets.SocketType.Dgram,
                System.Net.Sockets.ProtocolType.Udp);
            
            socket.Connect(remoteHost, 65530);
            if (socket.LocalEndPoint is System.Net.IPEndPoint endPoint)
            {
                return endPoint.Address.ToString();
            }
        }
        catch{}

        return "127.0.0.1";
    }
}