namespace Cracker;

using System;

class Program
{
    public static void Main(string[] args)
    {
        var ctx = new FsmContext
        {
            Args = args
        };
        
        ctx.Verbose = args.Any(a => a.Equals("-v", StringComparison.OrdinalIgnoreCase) ||
                                    a.Equals("--verbose", StringComparison.OrdinalIgnoreCase));
        
        var fsm = new FsmHandler(ctx);
        fsm.IterateFSMStates();
    }
}