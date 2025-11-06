namespace Client;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Sockets;


public sealed record Message(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("body")] JsonElement body
    );

// Server -> Client
public sealed record AssignWork(
    string JobId,
    string StoredHash,
    long   StartIndex,
    long   Count,
    int    CheckpointEvery 
    );

// Client -> Server
public sealed record Checkpoint
(
    string JobId,
    long   Tried,
    long   LastIndex,
    DateTimeOffset Ts
);

// Client -> Server
public sealed record WorkResult(
    string JobId,
    bool   Found,
    string? Password,
    long   Tried,
    long   DurationMs);

public sealed record Stop(string Reason);   

public static class Json
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
    
    // send objects as one JSON line: {...}\n
    public static async Task SendLineAsync(Stream stream, object message, CancellationToken token = default)
    {
        string json = JsonSerializer.Serialize(message, JsonOpts);
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static StreamReader CreateReader(Stream stream)
    {
        return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, 
            leaveOpen: true);
    }
    
    // Read one JSON line and parse to Envelope
    public static async Task<Message?> ReadLineAsync(StreamReader reader, CancellationToken token = default)
    {
        string? line = await reader.ReadLineAsync(token);
        if (string.IsNullOrWhiteSpace(line)) return null;
        return JsonSerializer.Deserialize<Message>(line, JsonOpts);
    }

    // Deserialize the payload of an Envelope to a specific type
    // returns null if deserialization fails
    public static T DeserializeBody<T>(Message msg)
    {
        return msg.body.Deserialize<T>(JsonOpts)!;
    }
}


// Server to Client
public sealed record ServerHello(DateTimeOffset ServerTime, string NodeId);

// Client to Server
public sealed record ClientHelloAck(string NodeId, bool Ok);

// Client to Server
public sealed record ClientRegister(string NodeId, string ListenHost, int ListenPort, int Threads);