using System;
using System.Reflection;
public static class X {
    public static bool A { get; } = false;
}
public static class Program {
    public static void Main(){
        Console.WriteLine($"before={X.A}");
        var f = typeof(X).GetField("<A>k__BackingField", BindingFlags.NonPublic|BindingFlags.Static);
        Console.WriteLine($"field? {f!=null} initonly={f?.IsInitOnly}");
        try { f?.SetValue(null, true); Console.WriteLine("set ok"); }
        catch(Exception ex){ Console.WriteLine(ex.GetType().Name+": "+ex.Message);}        
        Console.WriteLine($"after={X.A}");
    }
}
