namespace Client;

class Program
{
    public static void Main(string[] args)
    {
        var verbose = args.Any(a => a is "-v" or "--verbose");
        var cleaned = args.Where(a => a is not "-v" and not "--verbose").ToArray();

        var ctx = new FsmContext { Args = cleaned, Verbose = verbose };
        var fsm = new FsmHandler(ctx);
        var exit = fsm.Run();
        Environment.Exit(exit);
    }
}

public static class Log
{
    public static void In(string text)  => Console.WriteLine($"<- {text}");
    public static void Out(string text) => Console.WriteLine($"\n-> {text}");
    public static void Info(string text)=> Console.WriteLine(text);
}