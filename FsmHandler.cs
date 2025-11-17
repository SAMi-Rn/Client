namespace Client;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

internal sealed class FsmHandler
{
    private readonly FsmContext cx;
    private FsmState current = FsmState.INIT;
    private readonly List<(FsmState state, Func<FsmState> handler)> table;
    private bool verbose;
    private Cracker? _cracker;

    public FsmHandler(FsmContext context)
    {
        cx = context ?? throw new ArgumentNullException(nameof(context));
        table = new()
        {
            (FsmState.INIT, HandleInit),
            (FsmState.PARSE_ARGS, HandleParseArgs),
            (FsmState.START_CALLBACK, HandleStartCallback),
            (FsmState.REGISTER_WITH_SERVER, HandleRegisterWithServer),
            (FsmState.POLL, HandlePoll),
            (FsmState.ACCEPT_BACK, HandleAcceptBack),
            (FsmState.READ_AND_PROCESS, HandleReadAndProcess),
            (FsmState.CRACK, HandleCrack),
            (FsmState.END_PROGRAM, HandleEndProgram),
            (FsmState.ERROR, HandleError),
        };
    }

    public int Run()
    {
        verbose = cx.Verbose;
        Iterate();
        return cx.ExitCode == 0 ? 0 : cx.ExitCode;
    }


    private void Iterate()
    {
        while (!cx.QuitRequested)
        {
            if (cx.StopRequested)
            {
                current = FsmState.END_PROGRAM;
                break;
            }

            var prev = current;
            var entry = table.FirstOrDefault(t => t.state == current);
            var next = entry.handler != null ? entry.handler() : FsmState.ERROR;

            if (verbose && next != prev)
            {
                Console.WriteLine($"[fsm] {prev} -> {next}");
            }

            if (next == FsmState.ERROR)
            {
                current = HandleError();
                break;
            }

            current = next;
            if (current == FsmState.END_PROGRAM)
            {
                break;
            }
        }

        HandleEndProgram();
    }

    private FsmState HandleInit()
    {
        return FsmState.PARSE_ARGS;
    }


    private FsmState HandleParseArgs()
    {
        // Expected: <serverHost> <serverPort> [threads]
        var args = cx.Args;
        var withoutVerbose = args.Where(a => a is not "-v" and not "--verbose").ToArray();

        if (withoutVerbose.Length != 3)
        {
            PrintUsage();
            cx.Fail("Missing or extra arguments: <serverHost> <serverPort> <threads> are required.");
            cx.ExitCode = 1;
            return FsmState.ERROR;
        }

        cx.ServerHost = withoutVerbose[0];

        if (!int.TryParse(withoutVerbose[1], out var port) || port < 1 || port > 65535)
        {
            cx.Fail($"Invalid <port>: '{withoutVerbose[1]}'"); 
            return FsmState.ERROR;
        }
        cx.ServerPort = port;

        if (!int.TryParse(withoutVerbose[2], out var threads) || threads <= 0)
        {
            cx.Fail($"Invalid <threads>: '{withoutVerbose[2]}' (must be >= 1)"); return FsmState.ERROR;
        }
        cx.Threads = threads;

        if (cx.Verbose)
        {
            Console.WriteLine($"[args] server={cx.ServerHost}:{cx.ServerPort} threads={cx.Threads}");
        }
        
        _cracker = new Cracker(cx.Alphabet, cx.Threads);
        return FsmState.START_CALLBACK;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run -- <serverHost> <serverPort> <threads> [-v|--verbose]");
        Console.WriteLine("Example: dotnet run -- 192.168.0.100 5001 4 -v");
    }

    private FsmState HandleStartCallback()
    {
        try
        {
            cx.CallbackListener = new TcpListener(IPAddress.Any, 0);
            cx.CallbackListener.Start();
            cx.CallbackPort = ((IPEndPoint)cx.CallbackListener.LocalEndpoint).Port;
            Log.Info($"[{Now()}] Client callback listening on {cx.CallbackPort}");
            return FsmState.REGISTER_WITH_SERVER;
        }
        catch (Exception ex)
        {
            cx.Fail($"Callback listen failed: {ex.Message}");
            return FsmState.ERROR;
        }
    }

    private FsmState HandleRegisterWithServer()
    {
        try
        {
            using var tcp = new TcpClient();
            tcp.Connect(cx.ServerHost, cx.ServerPort);
            using var stream = tcp.GetStream();

            var register = new ClientRegister(cx.NodeId, GetLocalAddress(), cx.CallbackPort, cx.Threads);
            Json.SendLine(stream, new { type = Kinds.ClientRegister, body = register });

            Log.Info($"[{Now()}] Sent {Kinds.ClientRegister} to {cx.ServerHost}:{cx.ServerPort} " +
                     $"callback={register.ListenHost}:{register.ListenPort} threads={register.Threads}");

            return FsmState.POLL;
        }
        catch (Exception ex)
        {
            cx.Fail($"Register failed: {ex.Message}");
            return FsmState.ERROR;
        }
    }

    private FsmState HandlePoll()
    {
        try
        {
            // incoming callback connection from server
            var pending = cx.CallbackListener.Server.Poll(100_000, SelectMode.SelectRead); // 100ms
            if (pending)
            {
                return FsmState.ACCEPT_BACK;
            }
            return FsmState.POLL;
        }
        catch (Exception ex)
        {
            cx.Fail($"Poll failed: {ex.Message}");
            return FsmState.ERROR;
        }
    }

    private FsmState HandleAcceptBack()
    {
        try
        {
            cx.Back = cx.CallbackListener.AcceptTcpClient();
            cx.Stream = cx.Back.GetStream();
            cx.Rx.Clear();

            // expect SERVER_HELLO; reply CLIENT_HELLO_ACK(NodeId, Ok=true)
            var line = ReadLine(cx.Back, cx.Stream, timeoutMs: 5000);
            if (line == null || !Json.TryParseMessage(line, out var message) || message == null ||
                message.Type != Kinds.ServerHello)
            {
                cx.Fail("Handshake: expected SERVER_HELLO"); 
                return FsmState.ERROR;
            }
            Json.SendLine(cx.Stream, new { type = Kinds.ClientHelloAck, body = new ClientHelloAck(cx.NodeId, true) });
            var endPoint = cx.Back?.Client?.RemoteEndPoint as IPEndPoint;
            Log.Out($"[{Now()}] {Kinds.ClientHelloAck} sent to {endPoint?.Address}:{endPoint?.Port}");
            
            cx.LastReceived = DateTime.Now;

            Log.Info($"[{Now()}] Accepted server callback; handshake ok");
            return FsmState.READ_AND_PROCESS;
        }
        catch (Exception ex)
        {
            cx.Fail($"Accept failed: {ex.Message}");
            return FsmState.ERROR;
        }
    }

    private FsmState HandleReadAndProcess()
    {
        if (cx.Back == null || cx.Stream == null)
        {
            return FsmState.POLL;
        }

        try
        {
            // proceed when data is readable
            if (!cx.Back.Client.Poll(0, SelectMode.SelectRead))
            {
                Thread.Sleep(10);
                return FsmState.READ_AND_PROCESS;
            }

            string? line = ReadLineAvailable(cx.Back, cx.Stream, cx.Rx);
            if (line == null)
            {
                Thread.Sleep(5); 
                return FsmState.READ_AND_PROCESS;
            }

            cx.LastReceived = DateTime.Now;
            if (!Json.TryParseMessage(line, out var message) || message == null)
            {
                return FsmState.READ_AND_PROCESS;
            }

            if (message.Type == Kinds.AssignWork)
            {
                var assignedWork = Json.BodyAs<AssignWork>(message);
                cx.CurrentAssign = assignedWork;
                return FsmState.CRACK;
            }
            
            if (message.Type == Kinds.Stop)
            {
                var stop = Json.BodyAs<Stop>(message);
                cx.StopReason = stop.Reason ?? "";
                cx.StopRequested = true; 
                Log.Info($"[{Now()}] <- {Kinds.Stop} reason='{cx.StopReason}'");
                return FsmState.END_PROGRAM;
            } 
            return FsmState.READ_AND_PROCESS;
        }
        catch
        {
            Log.Info($"[{Now()}] Connection closed by server");
            return FsmState.END_PROGRAM;
        }
    }

    private FsmState HandleCrack()
    {
        var assignedWork = cx.CurrentAssign!;
        Log.Out($"[{Now()}] {Kinds.AssignWork} job={assignedWork.JobId} range=[{assignedWork.StartIndex}..{assignedWork.StartIndex + assignedWork.Count - 1}]");
        
        var sendLock = new object();
        var ctrlDone = new ManualResetEventSlim(false);
        var cts = new CancellationTokenSource();

        var ctrl = new Thread(() =>
            {
                try
                {
                    while (!cts.IsCancellationRequested && !cx.StopRequested)
                    {
                        var tcp = cx.Back; var s = cx.Stream;
                        if (tcp == null || s == null)
                        {
                            break;
                        }

                        if (tcp.Client.Poll(0, SelectMode.SelectRead) && tcp.Available == 0)
                        {
                            break;
                        }

                        string? line = ReadLineAvailable(tcp, s, cx.Rx);
                        if (line == null) 
                        { 
                            Thread.Sleep(5);
                            continue;
                        } 

                        if (Json.TryParseMessage(line, out var message) && message != null && message.Type == Kinds.Stop)
                        {
                            var stop = Json.BodyAs<Stop>(message);
                            cx.StopReason = stop.Reason ?? "";
                            cx.StopRequested = true;
                            Log.Info($"[{Now()}] <- {Kinds.Stop} reason='{cx.StopReason}'");
                            break;
                        }
                    }
                }
                finally { ctrlDone.Set(); }
            })
            { IsBackground = true, Name = "ctrl" };
        ctrl.Start();

        var result = _cracker!.RunSlice(
            storedHash: assignedWork.StoredHash,
            startIndex: assignedWork.StartIndex,
            count:      assignedWork.Count,
            checkpointEvery: Math.Max(1, assignedWork.CheckpointEvery),

            onWorkerStart: (slot, tid) => Log.Info($"[W{slot}] start tid={tid}"),

            onCheckpoint: (tried, per) =>
            {
                lock (sendLock)
                {
                    Json.SendLine(cx.Stream!, new {
                        type = Kinds.Checkpoint,
                        body = new Checkpoint(assignedWork.JobId, tried, assignedWork.StartIndex + tried - 1, DateTimeOffset.Now)
                    });
                    Log.In($"[{Now()}] {Kinds.Checkpoint} job={assignedWork.JobId} tried={tried} lastIndex={assignedWork.StartIndex + tried - 1} ts={DateTimeOffset.Now:O}");

                    var stringBuilder = new StringBuilder("[local] per-worker tried:");
                    for (int i = 0; i < per.Length; i++)
                    { if (i > 0) stringBuilder.Append(' '); stringBuilder.Append($"W{i}:{per[i]}"); }
                    Log.Info(stringBuilder.ToString());
                }
            },
            isStopRequested: () => cx.StopRequested
        );

        // stop listener and continue
        cts.Cancel();
        ctrlDone.Wait();

        if (cx.StopRequested)
        {
            return FsmState.END_PROGRAM;
        }

        lock (sendLock)
        {
            Json.SendLine(cx.Stream!, new {
                type = Kinds.WorkResult,
                body = new WorkResult(assignedWork.JobId, result.Found, result.Password, result.Tried, result.DurationMs)
            });
        }
        Log.In($"[{Now()}] [{cx.NodeId}] {Kinds.WorkResult} job={assignedWork.JobId} found={result.Found} tried={result.Tried} ms={result.DurationMs}");

        cx.CurrentAssign = null;
        return FsmState.READ_AND_PROCESS;
    }
    
    
    private FsmState HandleEndProgram()
    {
        try { cx.Stream?.Close(); } catch { }
        try { cx.Back?.Close(); } catch { }
        try { cx.CallbackListener?.Stop(); } catch { }
        return FsmState.END_PROGRAM;
    }

    private FsmState HandleError()
    {
        Console.WriteLine();
        Console.WriteLine("===== ERROR =====");
        Console.WriteLine(cx.ErrorMessage ?? "Unknown error");
        try { cx.Stream?.Close(); } catch { }
        try { cx.Back?.Close(); } catch { }
        try { cx.CallbackListener?.Stop(); } catch { }
        cx.ExitCode = cx.ExitCode == 0 ? 1 : cx.ExitCode;
        return FsmState.END_PROGRAM;
    }

    private static string? ReadLine(TcpClient tcp, NetworkStream stream, int timeoutMs)
    {
        var stringBuilder = new StringBuilder();
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (tcp.Available > 0)
            {
                var buffer = new byte[Math.Min(tcp.Available, 4096)];
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                stringBuilder.Append(Encoding.UTF8.GetString(buffer, 0, read));
                int newLine = stringBuilder.ToString().IndexOf('\n');
                if (newLine >= 0)
                {
                    return stringBuilder.ToString(0, newLine).TrimEnd('\r');
                }
            }
            Thread.Sleep(5);
        }
        return null;
    }

    private static string? ReadLineAvailable(TcpClient tcp, NetworkStream stream, StringBuilder received)
    {
        // if a full line is already buffered
        int newLineExisting = received.ToString().IndexOf('\n');
        if (newLineExisting >= 0)
        {
            string line = received.ToString(0, newLineExisting).TrimEnd('\r');
            received.Remove(0, newLineExisting + 1);
            return line;
        }

        // try to read any available bytes and  return one line if present
        int available = tcp.Available;
        if (available == 0)
        {
            return null;
        }

        var buffer = new byte[Math.Min(available, 8192)];
        int read = stream.Read(buffer, 0, buffer.Length);
        if (read <= 0)
        {
            return null;
        }

        received.Append(Encoding.UTF8.GetString(buffer, 0, read));
        int newLine = received.ToString().IndexOf('\n');
        if (newLine < 0)
        {
            return null;
        }

        string line2 = received.ToString(0, newLine).TrimEnd('\r');
        received.Remove(0, newLine + 1);
        return line2;
    }

    static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

    private static string GetLocalAddress()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 53);
            var ip = (socket.LocalEndPoint as IPEndPoint)!.Address;
            return ip.ToString();
        }
        catch { return "127.0.0.1"; }
    }
    
}
