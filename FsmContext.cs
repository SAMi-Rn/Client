using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Client;

internal sealed class FsmContext
{
    public string[] Args { get; init; } = Array.Empty<string>();
    public bool Verbose { get; init; }

    // parsed args
    public string ServerHost { get; set; } = "127.0.0.1";
    public int ServerPort { get; set; }
    public int Threads { get; set; }
    public string NodeId { get; set; } = $"{Environment.MachineName}".Replace(' ', '-');
    
    // callback listener
    public TcpListener CallbackListener = null!;
    public int CallbackPort;
    public TcpClient? Back;
    public NetworkStream? Stream;
    public DateTime LastReceived = DateTime.Now;
    public volatile bool StopRequested = false;
    public string StopReason = "";
    public char[] Alphabet { get; set; } =
        ("ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
         "abcdefghijklmnopqrstuvwxyz" +
         "0123456789" +
         "@#%^&*()_+-=.,:;?").ToCharArray();
    
    // job state
    public AssignWork? CurrentAssign;

    // runtime
    public int ExitCode = 0;
    public bool QuitRequested = false;
    // helpers
    public readonly StringBuilder Rx = new();
    
    public void RequestStop(string? reason = null)
    {
        if (!string.IsNullOrEmpty(reason)) StopReason = reason;
        System.Threading.Volatile.Write(ref StopRequested, true);
    }
    
    public string? ErrorMessage { get; private set; }
    public void Fail(string message)
    {
        ErrorMessage = message;
    }
}