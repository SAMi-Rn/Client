using System.Diagnostics;
using System.Drawing.Printing;

namespace Client;
using System.Net;
using System.Net.Sockets;

public static class Kinds
{
    public const string ServerHello    = "SERVER_HELLO";
    public const string ClientRegister = "CLIENT_REGISTER";
    public const string ClientHelloAck = "CLIENT_HELLO_ACK";
    public const string AssignWork     = "ASSIGN_WORK";
    public const string WorkResult     = "WORK_RESULT"; 
    public const string Checkpoint    = "CHECKPOINT";
    public const string Stop           = "STOP"; 
}

public class Protocol
{   
    // Client to Server, sends CLIENT_REGISTER
    public static async Task RegisterAsync(string serverHost, int serverPort, ClientRegister register, CancellationToken cancellationToken = default)
    {
        using var tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(serverHost, serverPort, cancellationToken);
        using var networkStream = tcpClient.GetStream();

        var line = new {type = Kinds.ClientRegister, body = register};
        
        await Json.SendLineAsync(networkStream, line, cancellationToken);
    }

    public static async Task<(TcpClient Client, StreamReader Reader)?> AcceptHelloAsync(int listenPort, string nodeId, CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Any, listenPort);
        listener.Start();

        try
        {
            var client = await listener.AcceptTcpClientAsync();
            var networkStream = client.GetStream();
            var reader = Json.CreateReader(networkStream); 
            
            var endPoint = client.Client.RemoteEndPoint?.ToString() ?? "<unknown>";
            
            // SERVER_HELLO
            var message = await Json.ReadLineAsync(reader, cancellationToken);
            if (message is null || !string.Equals(message.Type, Kinds.ServerHello, StringComparison.OrdinalIgnoreCase))
            {
                Log.Info($"Client: invalid HELLO from {endPoint}, closing.");
                client.Dispose();
                return null;
            }
            
            var hello = Json.DeserializeBody<ServerHello>(message);
            Log.In($"{Kinds.ServerHello} time={hello.ServerTime:o} from {endPoint} for node={hello.NodeId}");
            
            // CLIENT_HELLO_ACK
            var ack = new {type = Kinds.ClientHelloAck
                , body = new ClientHelloAck(nodeId, true)};
            await Json.SendLineAsync(networkStream, ack, cancellationToken);
            Log.Out($"{Kinds.ClientHelloAck} sent to {endPoint}");
            
            return (client, reader);
        }
        finally
        {
            listener.Stop();
        }
    }
    
    // receive ASSIGN_WORK, run it, send WORK_RESULT.
    public static async Task ReceiveJobsAsync(
        StreamReader reader,
        NetworkStream stream,
        Func<AssignWork, CancellationToken, Task<WorkResult>> onWork,
        CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // read next message
            var first = await Json.ReadLineAsync(reader, cancellationToken);
            if (first is null)
            {
                Log.Info("Client: server closed the connection.");
                break;
            }
            if (string.Equals(first.Type, Kinds.Stop, StringComparison.Ordinal))
            {
                var stop = Json.DeserializeBody<Stop>(first);
                Log.In($"{Kinds.Stop} reason='{stop.Reason}'");
                break; // stop listening; connection will close
            }
            
            if (!string.Equals(first.Type, Kinds.AssignWork, StringComparison.Ordinal))
            {
                Log.Info($"Client: unexpected '{first.Type}', ignoring and waiting for next.");
                continue; 
            } 
            var job = Json.DeserializeBody<AssignWork>(first);
            Log.In($"{Kinds.AssignWork} job={job.JobId} range=[{job.StartIndex}..{job.StartIndex + job.Count - 1}]");

            // 🔑 Create a job-scoped CTS we can cancel if STOP arrives mid-batch
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Start the cracking task
            var jobTask = onWork(job, jobCts.Token);

            while (true)
            {
                // ONE read at a time; cancellable so we can abort if job finishes first
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var readTask = Json.ReadLineAsync(reader, readCts.Token);

                var winner = await Task.WhenAny(jobTask, readTask);
                if (winner == jobTask)
                {
                    // Job done → cancel the read and send result
                    readCts.Cancel();
                    try { await readTask; } catch { /* canceled */ }

                    var result = await jobTask;
                    await Json.SendLineAsync(stream, new { type = Kinds.WorkResult, body = result }, cancellationToken);
                    Log.Out($"{Kinds.WorkResult} job={result.JobId} found={result.Found} tried={result.Tried} ms={result.DurationMs}");
                    break; // back to outer loop to await next ASSIGN_WORK
                }

                // Read finished first
                var msg = await readTask; // already completed
                if (msg is null)
                {
                    Log.Info("Client: server closed the connection.");
                    jobCts.Cancel();
                    try { await jobTask; } catch (OperationCanceledException) { }
                    return;
                }

                if (string.Equals(msg.Type, Kinds.Stop, StringComparison.Ordinal))
                {
                    var s = Json.DeserializeBody<Stop>(msg);
                    Log.In($"{Kinds.Stop} reason='{s.Reason}'");
                    jobCts.Cancel();
                    try { await jobTask; } catch (OperationCanceledException) { }
                    return; // done
                }

                // Ignore anything else while job is running and loop again
            }
        }
    }
    
    
    private static async Task<AssignWork?> ReceiveWorkAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var msg = await Json.ReadLineAsync(reader, cancellationToken);
        if (msg is null || !string.Equals(msg.Type, Kinds.AssignWork, StringComparison.Ordinal))
        {
            Log.Info($"Client: expected {Kinds.AssignWork}, got '{msg?.Type ?? "EOF"}'.");
            return null;
        }

        var job = Json.DeserializeBody<AssignWork>(msg);
        Log.In($"{Kinds.AssignWork} job={job.JobId} range=[{job.StartIndex}..{job.StartIndex + job.Count - 1}]");
        return job;
    }
    
    
    private static async Task<WorkResult> ExecuteAssignmentAsync(
        AssignWork job,
        Func<AssignWork, Task<WorkResult>> handleWork,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await handleWork(job);
            return result with { DurationMs = stopwatch.ElapsedMilliseconds };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Info($"Client: assignment failed: {ex.Message}");
            return new WorkResult(job.JobId, Found: false, Password: null, Tried: 0, DurationMs: stopwatch.ElapsedMilliseconds);
        }
    }
    
    
    private static async Task SendResultAsync(Stream stream, WorkResult result, CancellationToken cancellationToken)
    {
        await Json.SendLineAsync(stream, new { type = Kinds.WorkResult, body = result }, cancellationToken);
        Log.Out($"{Kinds.WorkResult} job={result.JobId} found={result.Found} tried={result.Tried} ms={result.DurationMs}");
    }
}