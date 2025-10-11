namespace Cracker;

using System;
using System.Collections.Generic;
using System.Diagnostics;

public sealed class FsmRow
{
    public int Threads { get; init; }
    public string Found { get; init; } = "NOTFOUND";
    public double Seconds { get; init; }
}

public sealed class FsmContext
{
    public string[] Args { get; set; } = Array.Empty<string>();
    public bool Verbose { get; set; } = false;
    public bool RunAll { get; set; } = false;
    public int SpecificThreads { get; set; } = -1;
    public string ShadowFile { get; set; } = "";
    public string Username { get; set; } = "";
    
    public string? StoredHash { get; set; }
    
    public int BlockSize { get; set; } = 1;
    public char[] Alphabet { get; set; } =
        ("ABCDEFGHIJKLMNOPQRSTUVWXYZ" +
         "abcdefghijklmnopqrstuvwxyz" +
         "0123456789" +
         "@#%^&*()_+-=.,:;?").ToCharArray();
    
    public List<int> ThreadCounts { get; } = new();
    public int ThreadIndex { get; set; } = 0;
    public int CurrentThreads => ThreadCounts[ThreadIndex];

    // Timing + results
    public Stopwatch Timer { get; } = new();
    public List<FsmRow> Results { get; } = new();

    public Exception? Error { get; set; }
}