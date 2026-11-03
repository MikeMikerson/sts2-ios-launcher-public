// These names match mod field injection parameters; the woven patches mutate the private fields.
#pragma warning disable IDE1006, IDE0044
namespace Game;

public static class Log
{
    public static List<string> Lines { get; } = [];
    public static void Add(string line) => Lines.Add(line);
}

public class Base
{
    private int inherited = 20;
    public int InheritedValue => inherited;
    public virtual void Closed() => Log.Add("base closed");
}

public class Screen : Base
{
    public int Value;
    private int secret = 10;
    private static int shared = 30;
    private readonly int fixedValue = 40;

    public int ReadSecret() => secret;
    public static int ReadShared() => shared;
    public int ReadInherited() => InheritedValue;
    public int ReadFixed() => fixedValue;

    public bool IsReady()
    {
        Log.Add("ready");
        return Value > 0;
    }

    public void Press(int size) => Log.Add("press " + size);

    public string Name(string prefix, ref int counter)
    {
        counter++;
        if (prefix.Length == 0) return "none";
        return prefix + counter;
    }

    public void Overloaded(int value) => Log.Add("int");
    public void Overloaded(string value) => Log.Add("string");

    public int WithTry(int value)
    {
        try
        {
            if (value < 0) return -1;
            Log.Add("try");
        }
        finally
        {
            Log.Add("finally");
        }
        return value;
    }
}
