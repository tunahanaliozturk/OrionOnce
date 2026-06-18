namespace Moongazing.OrionOnce.Demo;

/// <summary>
/// Small console formatting helpers shared by the feature demos so output stays consistent and
/// easy to scan when the program runs to completion.
/// </summary>
internal static class DemoConsole
{
    public static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 72));
        Console.WriteLine($"  {title}");
        Console.WriteLine(new string('=', 72));
    }

    public static void Step(string label) => Console.WriteLine($"  - {label}");

    public static void Detail(string label, string value) =>
        Console.WriteLine($"      {label,-22}{value}");

    public static void Pass(string message) => Console.WriteLine($"  [ok]   {message}");

    public static void Note(string message) => Console.WriteLine($"  [note] {message}");
}
