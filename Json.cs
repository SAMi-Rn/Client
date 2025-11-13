using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Client;

public static class Json
{
    // shared options
    static readonly JsonSerializerOptions _opts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // send a json line with newline
    public static void SendLine(Stream stream, object obj)
    {
        string serialize = JsonSerializer.Serialize(obj, _opts) + "\n";
        var buffer = Encoding.UTF8.GetBytes(serialize);
        stream.Write(buffer, 0, buffer.Length);
        stream.Flush();
    }
    
    // try parse a json line into message
    public static bool TryParseMessage(string line, out Message? message)
    {
        message = null;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        try
        {
            message = JsonSerializer.Deserialize<Message>(line, _opts);
            return message != null;
        }
        catch
        {
            return false;
        }
    }

    // deserialize body to REGISTER_CLIENT, ASSIGN_WORK, WORK_RESULT, CHECKPOINT, STOP 
    public static T BodyAs<T>(Message m) => m.body.Deserialize<T>(_opts)!;
}