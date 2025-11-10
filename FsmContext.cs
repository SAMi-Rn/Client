using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Client;

internal sealed class FsmContext
{
    // argv / flags
    public string[] Args { get; init; } = Array.Empty<string>();
    public bool Verbose { get; init; }

    // parsed args
    public string ServerHost { get; set; } = "127.0.0.1";
    public int    ServerPort { get; set; }
    public int    Threads    { get; set; } = Math.Max(1, Environment.ProcessorCount);

    // identity
    public string NodeId { get; set; } = $"{Environment.MachineName}".Replace(' ', '-');
    
    // callback listener (server connects back here)
    public TcpListener CallbackListener = null!;
    public int         CallbackPort;
    public TcpClient?  Back;                 // accepted callback connection
    public NetworkStream? Stream;            // Back.GetStream()
    public DateTime LastRx = DateTime.Now;   // last activity (RX) from server
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
    public bool QuietTransition = false;
    public bool QuitRequested = false;
    
    
    public bool PoolAlive;
    public int  PoolT;
    public Thread[]? Pool;
    public ManualResetEventSlim StartWork { get; } = new(false);
    public CountdownEvent? JobDone;
    public int JobVersion = 0;

    public long[]? LocalTried;   // pretty logs
    public object PoolLock { get; } = new();

    // current job published to pool
    public volatile object? CurrentJob; 
    
    // helpers
    public readonly StringBuilder Rx = new();

    // small helpers mirroring server
    public void Fail(string msg) { Log.Info(msg); ExitCode = ExitCode == 0 ? 1 : ExitCode; }
}