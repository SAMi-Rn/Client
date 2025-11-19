namespace Client;

using System.Text.Json;
using System.Text.Json.Serialization;

public static class Kinds
{
    public const string ServerHello    = "SERVER_HELLO";
    public const string ClientRegister = "CLIENT_REGISTER";
    public const string ClientHelloAck = "CLIENT_HELLO_ACK";
    public const string AssignWork     = "ASSIGN_WORK";
    public const string WorkResult     = "WORK_RESULT";
    public const string Checkpoint     = "CHECKPOINT";
    public const string Stop           = "STOP";
}

// Server -> Client
public sealed record ServerHello(DateTimeOffset ServerTime, string NodeId);

// Client -> Server
public sealed record ClientHelloAck(string NodeId, bool Ok);

// Client -> Server
public sealed record ClientRegister(string NodeId, string ListenHost, int ListenPort, int Threads);

public sealed record Message(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("body")] JsonElement body
);

// Server -> Client
public sealed record AssignWork
(
    string JobId,
    string StoredHash,
    long   StartIndex,
    long   Count,
    int    Checkpoint
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
public sealed record WorkResult
(
    string JobId,
    bool   Found,
    string? Password,
    long   Tried,
    long   DurationMs
);

public sealed record Stop(string Reason);