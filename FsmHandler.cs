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

    public FsmHandler(FsmContext context)
    {
        cx = context ?? throw new ArgumentNullException(nameof(context));

        table = new()
        {
            (FsmState.INIT,                 HandleInit),
            (FsmState.PARSE_ARGS,           HandleParseArgs),
            (FsmState.START_CALLBACK,       HandleStartCallback),
            (FsmState.REGISTER_WITH_SERVER, HandleRegisterWithServer),
            (FsmState.POLL,                 HandlePoll),
            (FsmState.ACCEPT_BACK,          HandleAcceptBack),
            (FsmState.READ_READY,           HandleReadReady),
            (FsmState.RUN_ASSIGN,           HandleRunAssign),
            (FsmState.END,                  HandleEnd),
            (FsmState.ERROR,                HandleError),
        };
    }

    public int Run()
    {
        verbose = cx.Verbose;
        Iterate();
        return cx.ExitCode == 0 ? 0 : cx.ExitCode;
    }

    private FsmState Quiet(FsmState s) { cx.QuietTransition = true; return s; }

    private void Iterate()
    {
        while (!cx.QuitRequested)
        {
            if (cx.StopRequested) { current = FsmState.END; break; } 
            var prev = current;
            var entry = table.FirstOrDefault(t => t.state == current);
            var next = entry.handler != null ? entry.handler() : FsmState.ERROR;

            bool quiet = cx.QuietTransition;
            cx.QuietTransition = false;

            if (verbose && next != prev && !quiet)
                Console.WriteLine($"[fsm] {prev} -> {next}");

            if (next == FsmState.ERROR) { current = HandleError(); break; }
            current = next;
            if (current == FsmState.END) break;
        }
        HandleEnd();
    }

    // ------------------- STATES -------------------

    private FsmState HandleInit() => FsmState.PARSE_ARGS;

    private FsmState HandleParseArgs()
    {
        // Expected: <serverHost> <serverPort> [threads]
        var args = cx.Args;
        var noVerb = args.Where(a => a is not "-v" and not "--verbose").ToArray();

        if (noVerb.Length is < 2 or > 3)
        {
            PrintUsage();
            cx.ExitCode = 1;
            return FsmState.ERROR;
        }

        cx.ServerHost = noVerb[0];

        if (!int.TryParse(noVerb[1], out var port) || port < 1 || port > 65535)
        { cx.Fail($"Invalid <port>: '{noVerb[1]}'"); return FsmState.ERROR; }
        cx.ServerPort = port;

        if (noVerb.Length == 3)
        {
            if (!int.TryParse(noVerb[2], out var t) || t <= 0)
            { cx.Fail("Invalid <threads>"); return FsmState.ERROR; }
            cx.Threads = t;
        }

        if (cx.Verbose)
            Console.WriteLine($"[args] server={cx.ServerHost}:{cx.ServerPort} threads={cx.Threads}");

        return FsmState.START_CALLBACK;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: dotnet run -- <serverHost> <serverPort> [threads] [-v|--verbose]");
        Console.WriteLine("Example: dotnet run -- 192.168.0.100 5001 4 -v");
    }

    private FsmState HandleStartCallback()
    {
        try
        {
            cx.CallbackListener = new TcpListener(IPAddress.Any, 0); // ephemeral
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
            using var s = tcp.GetStream();

            var reg = new ClientRegister(cx.NodeId, GetLocalAddress(), cx.CallbackPort, cx.Threads);
            Json.SendLine(s, new { type = Kinds.ClientRegister, body = reg });

            Log.Info($"[{Now()}] Sent {Kinds.ClientRegister} to {cx.ServerHost}:{cx.ServerPort} " +
                     $"callback={reg.ListenHost}:{reg.ListenPort} threads={reg.Threads}");

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
            // Poll for incoming callback connection from server
            var pending = cx.CallbackListener.Server.Poll(100_000, SelectMode.SelectRead); // 100ms
            if (pending) return FsmState.ACCEPT_BACK;
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
            var line = ReadLineBlocking(cx.Back, cx.Stream, timeoutMs: 5000);
            if (line == null || !Json.TryParseMessage(line, out var msg) || msg == null || msg.Type != Kinds.ServerHello)
            { cx.Fail("Handshake: expected SERVER_HELLO"); return FsmState.ERROR; }

            // (optional) validate body
            // var hello = Json.BodyAs<ServerHello>(msg);

            Json.SendLine(cx.Stream, new { type = Kinds.ClientHelloAck, body = new ClientHelloAck(cx.NodeId, true) });
            var ep = cx.Back?.Client?.RemoteEndPoint as IPEndPoint;
            Log.Out($"[{Now()}] {Kinds.ClientHelloAck} sent to {ep?.Address}:{ep?.Port}");
            
            cx.LastRx = DateTime.Now;

            Log.Info($"[{Now()}] Accepted server callback; handshake ok");
            return FsmState.READ_READY;
        }
        catch (Exception ex)
        {
            cx.Fail($"Accept failed: {ex.Message}");
            return FsmState.ERROR;
        }
    }

    private FsmState HandleReadReady()
    {
        if (cx.Back == null || cx.Stream == null) return FsmState.POLL;

        try
        {
            // Only proceed when data is readable
            if (!cx.Back.Client.Poll(0, SelectMode.SelectRead))
            {
                Thread.Sleep(10);
                return FsmState.READ_READY;
            }

            string? line = ReadLineAvailable(cx.Back, cx.Stream, cx.Rx);
            if (line == null) { Thread.Sleep(5); return FsmState.READ_READY; }

            cx.LastRx = DateTime.Now;
            if (!Json.TryParseMessage(line, out var msg) || msg == null) return FsmState.READ_READY;

            if (msg.Type == Kinds.AssignWork)
            {
                var a = Json.BodyAs<AssignWork>(msg);
                cx.CurrentAssign = a;
                return FsmState.RUN_ASSIGN;
            }
            
            if (msg.Type == Kinds.Stop)
            {
                var stop = Json.BodyAs<Stop>(msg);
                cx.StopReason = stop.Reason ?? "";
                cx.StopRequested = true;                  // <-- no JobState needed
                Log.Info($"[{Now()}] <- {Kinds.Stop} reason='{cx.StopReason}'");
                return FsmState.END;                      // disconnect in HandleEnd
            } 
            return FsmState.READ_READY;
        }
        catch
        {
            Log.Info($"[{Now()}] Connection closed by server");
            return FsmState.END;
        }
    }

    private FsmState HandleRunAssign()
    {
        var a = cx.CurrentAssign!;
        Log.Out($"[{Now()}] {Kinds.AssignWork} job={a.JobId} range=[{a.StartIndex}..{a.StartIndex + a.Count - 1}]");

        RunSliceMT(cx, a);    // blocking; sends checkpoints + result
        cx.CurrentAssign = null;
        return cx.StopRequested ? FsmState.END : FsmState.READ_READY;
    }

    private FsmState HandleEnd()
    {
        try { cx.Stream?.Close(); } catch { }
        try { cx.Back?.Close(); } catch { }
        try { cx.CallbackListener?.Stop(); } catch { }
        return FsmState.END;
    }

    private FsmState HandleError()
    {
        Console.WriteLine();
        Console.WriteLine("===== ERROR (client) =====");
        try { cx.Stream?.Close(); } catch { }
        try { cx.Back?.Close(); } catch { }
        try { cx.CallbackListener?.Stop(); } catch { }
        cx.ExitCode = cx.ExitCode == 0 ? 1 : cx.ExitCode;
        return FsmState.END;
    }

    // ------------------- CRACKER (MT, ordered commits) -------------------

    private static void RunSliceMT(FsmContext cx, AssignWork a)
    {
        EnsurePoolStarted(cx);                 // persistent pool; starts once

        if (a.Count > int.MaxValue)
            throw new InvalidOperationException("Slice too large for simple round-robin map.");

        // fresh job state for this assignment
        var job = new CrackJob
        {
            Assign          = a,
            Alphabet        = cx.Alphabet,
            Threads         = cx.PoolT,
            Start           = a.StartIndex,
            Count           = a.Count,
            CheckpointEvery = Math.Max(1, a.CheckpointEvery),

            PerWorkerTried  = new long[cx.PoolT],
            TriedMap = new int[(int)a.Count]  // 1 == tried
        };
        
        job.NextRel  = Math.Min(job.Threads, job.Count);
        
        // publish to pool
        cx.LocalTried = job.PerWorkerTried;    // for pretty logs
        cx.JobDone    = new CountdownEvent(cx.PoolT);
        cx.CurrentJob = job;

        // kick this job
        job.Sw.Restart();
        Interlocked.Increment(ref cx.JobVersion);   // workers observe version bump
        cx.StartWork.Set();
        
        var ctrlDone = new ManualResetEventSlim(false);
        var ctrl = new Thread(() =>
        {
            try
            {
                while (!cx.StopRequested && Volatile.Read(ref job.stopFlag) == 0)
                {
                    var tcp = cx.Back;
                    var s   = cx.Stream;
                    if (tcp == null || s == null || !tcp.Connected) break;

                    // Try to parse any already-buffered line first (ReadLineAvailable now handles this)
                    string? line = ReadLineAvailable(tcp, s, cx.Rx);
                    if (line != null && Json.TryParseMessage(line, out var m) && m != null)
                    {
                        if (m.Type == Kinds.Stop)
                        {
                            var stop = Json.BodyAs<Stop>(m);
                            cx.StopReason    = stop.Reason ?? "";
                            cx.StopRequested = true;
                            Volatile.Write(ref job.stopFlag, 1);
                            Log.Info($"[{Now()}] <- {Kinds.Stop} reason='{cx.StopReason}'");
                            break;
                        }
                        // ignore other message types during RUN_ASSIGN
                    }

                    Thread.Sleep(5);
                }

                // Final drain once more to catch a STOP that arrived *right* as we’re exiting.
                // (Useful when the server broadcasts STOP immediately after someone reports FOUND.)
                if (!cx.StopRequested)
                {
                    var tcp = cx.Back;
                    var s   = cx.Stream;
                    if (tcp != null && s != null && tcp.Connected)
                    {
                        while (true)
                        {
                            string? line = ReadLineAvailable(tcp, s, cx.Rx);
                            if (line == null) break;
                            if (Json.TryParseMessage(line, out var m) && m != null && m.Type == Kinds.Stop)
                            {
                                var stop = Json.BodyAs<Stop>(m);
                                cx.StopReason    = stop.Reason ?? "";
                                cx.StopRequested = true;
                                Volatile.Write(ref job.stopFlag, 1);
                                Log.Info($"[{Now()}] <- {Kinds.Stop} reason='{cx.StopReason}'");
                                break;
                            }
                        }
                    }
                }
            }
            finally { ctrlDone.Set(); }
        })
        { IsBackground = true, Name = "ctrl" };
        ctrl.Start();
        
        // wait for workers to complete the job
        cx.JobDone.Wait();
        cx.StartWork.Reset();
        job.Sw.Stop();
        
        Volatile.Write(ref job.stopFlag, 1);
        ctrlDone.Wait();
        if (cx.StopRequested)
        {
            return;
        }
        
        // one final checkpoint if we have unsent committed progress
        if (job.Committed > job.LastSent) SendCheckpoint(cx, job, job.Committed);

        // end-of-job per-worker snapshot (once)
        //PrintPerWorkerTried(cx);

        // send result
        Json.SendLine(cx.Stream!, new {
            type = Kinds.WorkResult,
            body = new WorkResult(
                job.Assign.JobId,
                job.resultPassword != null,
                job.resultPassword,
                Volatile.Read(ref job.TotalTried),
                (long)job.Sw.Elapsed.TotalMilliseconds)
        });
        cx.CurrentJob = null;

        Log.In($"[{Now()}] [{cx.NodeId}] {Kinds.WorkResult} job={job.Assign.JobId} found={(job.resultPassword!=null)} tried={job.TotalTried} ms={(long)job.Sw.Elapsed.TotalMilliseconds}");
    }

    private sealed class CrackJob
    {
        public AssignWork Assign = null!;
        public char[] Alphabet = Array.Empty<char>();
        public int    Threads;
        public long   Start;                // start index of this slice
        public long   Count;                // total indices in this slice
        public int    CheckpointEvery;      // checkpoint period

        // per-worker + per-index bookkeeping
        public long[] PerWorkerTried = Array.Empty<long>(); // length == Threads
        // next relative index to hand out (atomic)
        public long NextRel;  
        public int[] TriedMap       = Array.Empty<int>(); // length == Count (<= int.MaxValue guard)

        // “stop”/“found” like your Cracker
        public int stopFlag = 0;
        public string? resultPassword;

        // commit/CP tracking (contiguous tried from the slice start)
        public long Committed = 0;
        public long LastSent  = 0;
        public long LastPrintedTried = -1;
        public long TotalTried = 0;

        public object CommitLock = new();
        public object SendLock   = new();

        public Stopwatch Sw = new();
    }

    private static void PrintPerWorkerTried(FsmContext cx)
    {
        var arr = cx.LocalTried;
        if (arr is null) return;
        var sb = new StringBuilder();
        sb.Append("[local] per-worker tried:");
        for (int i = 0; i < arr.Length; i++)
        {
            long v = Volatile.Read(ref arr[i]);
            if (i > 0) sb.Append(' ');
            sb.Append($"W{i}:{v}");
        }
        Log.Info(sb.ToString());
    }

    private static void SendCheckpoint(FsmContext cx, CrackJob job, long tried)
    {
        if (cx.StopRequested || Volatile.Read(ref job.stopFlag) != 0) return;
        
        long aligned = (tried == job.Count) ? job.Count : (tried / job.CheckpointEvery) * job.CheckpointEvery;
        if (aligned <= 0) return;
        
        lock (job.SendLock)
        {
            if (aligned <= job.LastSent) return;
            job.LastSent = aligned;

            Json.SendLine(cx.Stream!, new {
                type = Kinds.Checkpoint,
                body = new Checkpoint(job.Assign.JobId, aligned, job.Assign.StartIndex + aligned - 1, DateTimeOffset.Now)
            });
            Log.In($"[{Now()}] {Kinds.Checkpoint} job={job.Assign.JobId} tried={aligned} lastIndex={job.Assign.StartIndex + aligned - 1} ts={DateTimeOffset.Now:O}");

            if (aligned > job.LastPrintedTried)
            {
                job.LastPrintedTried = aligned;
                PrintPerWorkerTried(cx);
            }
        }
    }

    private static void TryAdvanceCommit(FsmContext cx, CrackJob job)
    {   
        if (cx.StopRequested || Volatile.Read(ref job.stopFlag) != 0) return;

        lock (job.CommitLock)
        {
            while (job.Committed < job.Count && Volatile.Read(ref job.TriedMap[(int)job.Committed]) == 1)
                job.Committed++;

            if ((job.Committed / job.CheckpointEvery) > (job.LastSent / job.CheckpointEvery) || job.Committed == job.Count)
                SendCheckpoint(cx, job, job.Committed);
        }
    }

    // === Persistent worker pool ===
    
    private static void WorkerLoop(FsmContext cx, int slot) 
    {
        int seenVersion = 0;

        while (!cx.StopRequested)
        {
            cx.StartWork.Wait();                 // master Set()s when a job is published
            if (cx.StopRequested) break;

            int ver = Volatile.Read(ref cx.JobVersion);
            if (ver == seenVersion) { Thread.Sleep(1); continue; }   // already processed this version

            var job = (CrackJob?)cx.CurrentJob;
            if (job == null) { Thread.Sleep(1); continue; }

            // log once per job per worker
            var tid = Environment.CurrentManagedThreadId;
            Log.Info($"[W{slot}] start tid={tid}");

            bool first = true; // seed with slot index once, then fetch-add forever
            try
            {
                while (Volatile.Read(ref job.stopFlag) == 0 && !cx.StopRequested)
                {
                    long rel;

                    if (first)
                    {
                        first = false;
                        if (slot >= job.Count) break;   // fewer items than threads
                        rel = slot;                     // 0,1,2,...,T-1
                    }
                    else
                    {
                        rel = Interlocked.Increment(ref job.NextRel) - 1; // 0..Count-1 in order
                        if (rel >= job.Count) break;
                    }

                    long idx = job.Start + rel;
                    string cand = IndexToCandidate(idx, job.Alphabet);

                    bool ok = Verify(cand, job.Assign.StoredHash);

                    Interlocked.Increment(ref job.PerWorkerTried[slot]);
                    Interlocked.Increment(ref job.TotalTried);

                    Volatile.Write(ref job.TriedMap[(int)rel], 1);  // mark done (visible)
                    TryAdvanceCommit(cx, job);                      // advances contiguous watermark

                    if (ok)
                    {
                        job.resultPassword = cand;
                        Interlocked.Exchange(ref job.stopFlag, 1);  // stop others
                        break;
                    }
                }
            }
            finally
            {
                // ensure master unblocks even if something threw
                cx.JobDone!.Signal();
                seenVersion = ver;
            }
        }
    }
    private static void EnsurePoolStarted(FsmContext cx)
    {
        if (cx.PoolAlive) return;

        lock (cx.PoolLock)
        {
            if (cx.PoolAlive) return;

            cx.PoolT = Math.Max(1, cx.Threads);
            cx.Pool  = new Thread[cx.PoolT];
            cx.LocalTried = new long[cx.PoolT];

            // Try to avoid Nagle coalescing of small JSON lines
            try { if (cx.Back != null) cx.Back.NoDelay = true; } catch { }

            for (int i = 0; i < cx.PoolT; i++)
            {
                int slot = i;
                cx.Pool[i] = new Thread(() => WorkerLoop(cx, slot))
                {
                    IsBackground = true,
                    Name = $"W{slot}"
                };
                cx.Pool[i].Start();
            }

            cx.PoolAlive = true;
        }
    }
    // ------------------- Helpers (I/O + Cracker glue) -------------------

    private static string? ReadLineBlocking(TcpClient tcp, NetworkStream s, int timeoutMs)
    {
        var sb = new StringBuilder();
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (tcp.Available > 0)
            {
                var buf = new byte[Math.Min(tcp.Available, 4096)];
                int read = s.Read(buf, 0, buf.Length);
                if (read <= 0) break;
                sb.Append(Encoding.UTF8.GetString(buf, 0, read));
                int nl = sb.ToString().IndexOf('\n');
                if (nl >= 0) return sb.ToString(0, nl).TrimEnd('\r');
            }
            Thread.Sleep(5);
        }
        return null;
    }

    private static string? ReadLineAvailable(TcpClient tcp, NetworkStream s, StringBuilder rx)
    {
        // 1) If a full line is already buffered, return it immediately.
        int nlExisting = rx.ToString().IndexOf('\n');
        if (nlExisting >= 0)
        {
            string line = rx.ToString(0, nlExisting).TrimEnd('\r');
            rx.Remove(0, nlExisting + 1);
            return line;
        }

        // 2) Else try to read any available bytes and then return one line if present.
        int avail = tcp.Available;
        if (avail == 0) return null;

        var buf = new byte[Math.Min(avail, 8192)];
        int read = s.Read(buf, 0, buf.Length);
        if (read <= 0) return null;

        rx.Append(Encoding.UTF8.GetString(buf, 0, read));
        int nl = rx.ToString().IndexOf('\n');
        if (nl < 0) return null;

        string line2 = rx.ToString(0, nl).TrimEnd('\r');
        rx.Remove(0, nl + 1);
        return line2;
    }

    static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

    private static string GetLocalAddress()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            s.Connect("8.8.8.8", 53);
            var ip = (s.LocalEndPoint as IPEndPoint)!.Address;
            return ip.ToString();
        }
        catch { return "127.0.0.1"; }
    }

    private static string IndexToCandidate(long idx, char[] alphabet)
    {
        if (idx < 0) throw new ArgumentOutOfRangeException(nameof(idx));
        long A = alphabet.Length;
        if (A <= 1) throw new InvalidOperationException("Alphabet must have length >= 2.");

        // Find the length bucket: 1-char, then 2-char, then 3-char, ...
        long len = 1;
        long pow = A;        // A^len
        long priorCum = 0;   // total count up to previous length

        checked
        {
            while (true)
            {
                long cum = priorCum + pow;      // total strings up to this length
                if (idx < cum)
                {
                    long offset = idx - priorCum;   // 0..A^len-1 within this length
                    return ToFixedBase(offset, len, A, alphabet);
                }

                // advance to next length
                priorCum = cum;
                if (pow > long.MaxValue / A) throw new OverflowException("Index too large for generator.");
                pow *= A;
                len++;
            }
        }
    }

    private static string ToFixedBase(long value, long width, long baseN, char[] alphabet)
    {
        var buf = new char[width];
        for (long i = width - 1; i >= 0; --i)
        {
            long digit = value % baseN;
            buf[i] = alphabet[(int)digit];
            value /= baseN;
        }
        return new string(buf);
    }

    private static bool Verify(string candidate, string storedHash)
    {
        // crypt_ra returns the encoded hash of 'candidate' using 'storedHash' as the setting.
        // Match when it equals the storedHash.
        string? produced = Cracker.CryptWrap(candidate, storedHash);
        return produced is not null && string.Equals(produced, storedHash, StringComparison.Ordinal);
    }
}
