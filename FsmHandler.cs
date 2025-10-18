namespace Cracker;

using System;
using System.Collections.Generic;
using System.Linq;

public sealed class FsmHandler
{
    private readonly FsmContext ctx;
    private FsmState currentState = FsmState.CHECK_ARGUMENTS;

    private readonly List<(FsmState state, Func<FsmState> handler)> table;

    public FsmHandler(FsmContext context)
    {
        ctx = context ?? throw new ArgumentNullException(nameof(context));
        table = new()
        {   
            (FsmState.CHECK_ARGUMENTS,       () => handleCheckArguments()),
            (FsmState.BIND_CRYPT,            () => handleBindCrypt()),
            (FsmState.READ_SHADOW,           () => handleReadShadow()),
            (FsmState.PREPARE_ALPHABET,      () => handlePrepareAlphabet()),
            (FsmState.PREPARE_THREAD_COUNTS, () => handlePrepareThreadCounts()),
            (FsmState.START_TIMER,           () => handleStartTimer()),
            (FsmState.RUN_CRACK,             () => handleRunCrack()),
            (FsmState.STOP_TIMER,            () => handleStopTimer()),
            (FsmState.NEXT_THREAD_COUNT,     () => handleNextThreadCount()),
            (FsmState.END_PROGRAM,           () => handleEndProgram()),
            (FsmState.ERROR,                 () => handleError()),
        };
    }

    public void IterateFSMStates()
    {
        while (currentState != FsmState.END_PROGRAM)
        {
            bool hasHandler = false;
            foreach (var (state, handler) in table)
            {
                if (state == currentState)
                {
                    // call handler and capture next state
                    var next = handler();

                    if (ctx.Verbose)
                    {
                        Console.WriteLine($"[fsm] {currentState} -> {next}");
                    }
                    
                    currentState = next;
                    hasHandler = true;
                    break;
                }
            }

            if (!hasHandler)
            {
                Console.WriteLine($"[fsm] No handler for state {currentState}");
                currentState = FsmState.ERROR;
            }
        }
        var end = table.Find(t => t.state == FsmState.END_PROGRAM).handler;
        end();
    }
    
    public FsmState handleCheckArguments()
    {
        var args = ctx.Args;

        if (args.Length < 2)
        {
            Console.WriteLine("Usage: dotnet run -- <shadow_file> <username> [threads|'all'] [-v|--verbose]");
            return FsmState.ERROR;
        }

        ctx.ShadowFile = args[0];
        ctx.Username   = args[1];

        ctx.RunAll = false;
        ctx.SpecificThreads = -1;

        for (int i = 2; i < args.Length; i++)
        {
            var a = args[i];
            
            if (a.Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                ctx.RunAll = true;
            }
            else if (int.TryParse(a, out var t) && t > 0)
            {
                ctx.SpecificThreads = t;
            }
            else if (a.Equals("-v", StringComparison.OrdinalIgnoreCase) || a.Equals("--verbose", StringComparison.OrdinalIgnoreCase))
            {
                // no op, it gets handled in the main
            }
            else
            {
                Console.WriteLine($"Unrecognized argument: {a}");
                var msg = "Usage: dotnet run -- <shadow_file> <username> [threads|'all'] [-v|--verbose]";
                ctx.Error = new ArgumentException(msg);
                return FsmState.ERROR;
            }
        }

        if (ctx.Verbose)
            Console.WriteLine($"[info] args parsed → file={ctx.ShadowFile}, user={ctx.Username},");

        return FsmState.BIND_CRYPT;
    }

    public FsmState handleBindCrypt()
    {
        try
        {
            Cracker.EnsureCryptRaLoaded();
            return FsmState.READ_SHADOW;
        }
        catch (Exception ex)
        {
            ctx.Error = ex; 
            return FsmState.ERROR;
        }
    }

    public FsmState handleReadShadow()
    {
        ctx.StoredHash = Cracker.GetHashForUser(ctx.ShadowFile, ctx.Username);
        if (ctx.StoredHash is null)
        {
            var msg = $"No hash found for user: {ctx.Username}";
            ctx.Error = new InvalidOperationException(msg);
            return FsmState.ERROR;
        }

        return FsmState.PREPARE_ALPHABET;
    }

    public FsmState handlePrepareAlphabet()
    {
        if (ctx.Alphabet.Length != 79)
        {
            var msg = "Alphabet should be 79 characters long";
            ctx.Error = new NotSupportedException(msg);
            return FsmState.ERROR;
        }
        return FsmState.PREPARE_THREAD_COUNTS;
    }

    public FsmState handlePrepareThreadCounts()
    {
        ctx.ThreadCounts.Clear();
        
        if (ctx.RunAll)
        {
            ctx.ThreadCounts.AddRange(new[] { 1, 2, 3, 4, 8, 16 });
        }
        
        else if (ctx.SpecificThreads > 0)
        {
            ctx.ThreadCounts.Add(ctx.SpecificThreads);
        }
        else
        {
            ctx.ThreadCounts.AddRange(new[] { 1, 2, 3, 4 });
        }

        ctx.ThreadIndex = 0;
        Console.WriteLine($"\n[info] blockSize={ctx.BlockSize}; threadCounts=[{string.Join(",", ctx.ThreadCounts)}]\n");
        return FsmState.START_TIMER;
    }

    public FsmState handleStartTimer()
    {
        ctx.Timer.Restart();
        return FsmState.RUN_CRACK;
    }

    public FsmState handleRunCrack()
    {
        try
        {
            var found = Cracker.CrackRangeMultiThread(
                ctx.StoredHash!, ctx.Alphabet,
                ctx.CurrentThreads, ctx.BlockSize);

            // Seconds get filled after STOP_TIMER
            ctx.Results.Add(new FsmRow
            {
                Threads = ctx.CurrentThreads,
                Found   = found ?? "NOTFOUND",
                Seconds = 0
            });

            return FsmState.STOP_TIMER;
        }
        catch (Exception ex)
        {
            ctx.Error = ex;
            return FsmState.ERROR;
        }
    }

    public FsmState handleStopTimer()
    {
        ctx.Timer.Stop();
        // fill last row’s time
        if (ctx.Results.Count > 0)
        {
            var i = ctx.Results.Count - 1;
            var r = ctx.Results[i];
            ctx.Results[i] = new FsmRow { Threads = r.Threads, Found = r.Found, Seconds = ctx.Timer.Elapsed.TotalSeconds };
            if (ctx.Verbose)
            {
                Console.WriteLine($"\n[info] threads={ctx.CurrentThreads} found={ctx.Results[i].Found} stop ({ctx.Timer.Elapsed.TotalSeconds:0.000}s)\n");
            }
        }
        return FsmState.NEXT_THREAD_COUNT;
    }
    
    public FsmState handleNextThreadCount()
    {
        ctx.ThreadIndex++;
        if (ctx.ThreadIndex < ctx.ThreadCounts.Count)
        {
            return FsmState.START_TIMER;
        }
            
        return FsmState.END_PROGRAM;
    }

    public FsmState handleEndProgram()
    {
        PrintSummaryTableAndCharts();
        if (ctx.Verbose)
        {
            Console.Error.WriteLine("\n[info] Cleaning up and Exiting...");
        }
        
        GC.Collect();
        GC.WaitForPendingFinalizers();
        
        return FsmState.END_PROGRAM;
    }

    public FsmState handleError()
    {

        if (ctx.Error != null)
        {
            Console.Error.WriteLine($"[error] {ctx.Error.Message}");
        }
        
        return FsmState.END_PROGRAM;
    }
    private void PrintSummaryTableAndCharts()
    {
        if (ctx.Results.Count == 0)
        {
            return;
        }

        // sort by threads
        var rows = ctx.Results.OrderBy(r => r.Threads).ToList();

        // baseline from the first row
        double baseline = rows[0].Seconds > 0 ? rows[0].Seconds : 1.0;

        var headers = new[] { "threads", "found", "time(s)", "speed(x)" };
        var data = rows.Select(r => new[]
        {
            r.Threads.ToString(),
            r.Found,
            r.Seconds.ToString("0.000"),
            (baseline / (r.Seconds > 0 ? r.Seconds : 1.0)).ToString("0.00") + "×",
        }).ToList();

        Console.WriteLine();
        PrintTable(headers, data);

        Console.WriteLine();
        PrintBarChart("\t\t  Time vs Threads",
            rows.Select(r => r.Threads.ToString()).ToList(),
            rows.Select(r => r.Seconds).ToList(),
            "s");

        var speed = rows.Select(r => baseline / (r.Seconds > 0 ? r.Seconds : 1.0)).ToList();
        Console.WriteLine();
        PrintBarChart("\t\t Speed vs Threads",
            rows.Select(r => r.Threads.ToString()).ToList(),
            speed,
            "×");
        try
        {
            SaveScottPlotCharts(rows);
        }
        catch (Exception ex)
        {
            if (ctx.Verbose)
                Console.WriteLine($"[warn] Failed to save ScottPlot charts: {ex.Message}");
        }
    }

    private static void PrintTable(string[] headers, List<string[]> rows)
    {
        int cols = headers.Length;
        int[] widths = new int[cols];
        for (int i = 0; i < cols; i++)
        {
            widths[i] = headers[i].Length;
        }

        foreach (var row in rows)
        {
            for (int i = 0; i < cols; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }
        
        PrintRow(headers, widths);
        PrintSep(widths);

        foreach (var row in rows)
        {
            PrintRow(row, widths);
        }
    }

    private static void PrintRow(string[] cols, int[] widths)
    {
        for (int i = 0; i < cols.Length; i++)
        {
            if (i > 0) Console.Write(" | ");
            Console.Write(cols[i].PadLeft(widths[i]));
        }
        Console.WriteLine();
    }

    private static void PrintSep(int[] widths)
    {
        for (int i = 0; i < widths.Length; i++)
        {
            if (i > 0) Console.Write("-+-");
            Console.Write(new string('-', widths[i]));
        }
        Console.WriteLine();
    }

    private static void PrintBarChart(string title, List<string> xs, List<double> ys, string unit)
    {
        Console.WriteLine(title);

        if (xs.Count != ys.Count || xs.Count == 0)
        {
            return;
        }

        double max = ys.Max();
        if (max <= 0) max = 1;
        const int maxBar = 40;

        for (int i = 0; i < xs.Count; i++)
        {
            int n = (int)Math.Round(ys[i] / max * maxBar);
            string bar = new string('█', Math.Max(0, n));
            Console.WriteLine($"{xs[i],3} | {bar,-40} {ys[i]:0.###}{unit}");
        }
    }
    
    private static void SaveScottPlotCharts(List<FsmRow> rows)
    {
        if (rows == null || rows.Count == 0)
        {
            return;
        }
        
        double[] xs = rows.Select(r => (double)r.Threads).ToArray();
        double[] ysTime = rows.Select(r => r.Seconds).ToArray();

        double baseline = rows[0].Seconds > 0 ? rows[0].Seconds : rows.Select(r => r.Seconds).FirstOrDefault(s => s > 0, 1.0);
        double[] ysSpeed = ysTime.Select(t => baseline / (t > 0 ? t : 1.0)).ToArray();
        
        var plt1 = new ScottPlot.Plot(800, 500);
        plt1.Title("Time vs Threads");
        plt1.XLabel("Threads");
        plt1.YLabel("Seconds");
        plt1.AddScatter(xs, ysTime, markerSize: 5);
        plt1.SaveFig("time_vs_threads.png");
        
        var plt2 = new ScottPlot.Plot(800, 500);
        plt2.Title("Speed vs Threads");
        plt2.XLabel("Threads");
        plt2.YLabel("Speed (×)");
        plt2.AddScatter(xs, ysSpeed, markerSize: 5);
        plt2.SaveFig("speed_vs_threads.png");

        Console.WriteLine("[info] Saved charts: time_vs_threads.png, speed_vs_threads.png");
    }
}