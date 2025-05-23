using System;
using Spectre.Console;
namespace DepotDumper;
static class Ansi
{
    public enum ProgressState
    {
        Hidden = 0,
        Default = 1,
        Error = 2,
        Indeterminate = 3,
        Warning = 4,
    }
    const char ESC = (char)0x1B;
    const char BEL = (char)0x07;
    private static bool useProgress;
    public static void Init()
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            return;
        }
        var (supportsAnsi, legacyConsole) = AnsiDetector.Detect(stdError: false, upgrade: true);
        useProgress = supportsAnsi && !legacyConsole;
    }
    public static void Progress(ulong downloaded, ulong total)
    {
        var progress = (byte)MathF.Round(downloaded / (float)total * 100.0f);
        Progress(ProgressState.Default, progress);
    }
    public static void Progress(ProgressState state, byte progress = 0)
    {
        if (!useProgress)
        {
            return;
        }
        Console.Write($"{ESC}]9;4;{(byte)state};{progress}{BEL}");
    }
}
