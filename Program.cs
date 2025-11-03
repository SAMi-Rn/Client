namespace Client;

using System;

class Program
{
    public static void Main(string[] args)
    {   
        if (args.Length > 0 && string.Equals(args[0], "--worker", StringComparison.OrdinalIgnoreCase))
        {
            Environment.Exit(ClientRunner.Run(args));
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

public static class ClientRunner
{
    public static int Run(string[] args)
    {
        if (args.Length < 4)
        {
            Console.WriteLine("Usage: --worker <serverHost> <serverPort> <listenPort> [threads]");
            return -1;
        }

        string serverHost = args[1];
        int serverPort = int.Parse(args[2]);
        int listnPort = int.Parse(args[3]);
        int threads = (args.Length >= 5 ? int.Parse(args[4]) : Environment.ProcessorCount);

        string nodeId = $"c-{Environment.MachineName}";
        string listenHost = GetOutboundIp(serverHost);
        
        using var cancelationToken = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true; 
            cancelationToken.Cancel();
        };
        
        var acceptConnection = Protocol.AcceptHelloAsync(listnPort, nodeId, cancelationToken.Token);
        
        var register = new ClientRegister(nodeId, listenHost, listnPort, threads);
        Protocol.RegisterAsync(serverHost, serverPort, register, cancelationToken.Token).GetAwaiter().GetResult();
        
        acceptConnection.GetAwaiter().GetResult();
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