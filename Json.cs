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
        string s = JsonSerializer.Serialize(obj, _opts) + "\n";
        var buf = Encoding.UTF8.GetBytes(s);
        stream.Write(buf, 0, buf.Length);
        stream.Flush();
    }
    
    // try parse a json line into Message
    public static bool TryParseMessage(string line, out Message? msg)
    {
        msg = null;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try { msg = JsonSerializer.Deserialize<Message>(line, _opts); return msg != null; }
        catch { return false; }
    }

    // deserialize body to REGISTER_CLIENT, ASSIGN_WORK, WORK_RESULT, CHECKPOINT, STOP 
    public static T BodyAs<T>(Message m) => m.body.Deserialize<T>(_opts)!;
}