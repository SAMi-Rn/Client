namespace Client;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Sockets;


public sealed record Message(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("body")] JsonElement body
    );

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
    
    // Read one JSON line and parse to Envelope
    public static async Task<Message?> ReadLineAsync(Stream stream, CancellationToken token = default)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024, leaveOpen: true);
        string? line = await reader.ReadLineAsync().WaitAsync(token);
        
        // end of stream
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }
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