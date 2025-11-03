using System.Drawing.Printing;

namespace Client;
using System.Net;
using System.Net.Sockets;

public static class Kinds
{
    public const string ServerHello    = "SERVER_HELLO";
    public const string ClientRegister = "CLIENT_REGISTER";
    public const string ClientHelloAck = "CLIENT_HELLO_ACK";
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

    public static async Task AcceptHelloAsync(int listenPort, string nodeId, CancellationToken cancellationToken = default)
    {
        var listener = new TcpListener(IPAddress.Any, listenPort);
        listener.Start();

        try
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var networkStream = client.GetStream();
            var endPoint = client.Client.RemoteEndPoint?.ToString() ?? "<unknown>";
            
            var message = await Json.ReadLineAsync(networkStream, cancellationToken);
            if (message is null || !string.Equals(message.Type, Kinds.ServerHello, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Client: Invalid Hello message from {endPoint}, closing connection.");
                return;
            }
         
            var hello = Json.DeserializeBody<ServerHello>(message);
            Console.WriteLine($"<- SERVER_HELLO time={hello.ServerTime:o} from {endPoint} for node={hello.NodeId}");
            var ack = new {type = "Client_Hello_Ack", body = new ClientHelloAck(nodeId, true)};
            await Json.SendLineAsync(networkStream, ack, cancellationToken);
            Console.WriteLine($"-> CLIENT_HELLO_ACK sent to {endPoint}");
        }
        finally
        {
            listener.Stop();
        }
    }
}