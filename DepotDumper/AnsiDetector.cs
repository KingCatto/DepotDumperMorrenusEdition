using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.System.Console;
namespace Spectre.Console;
internal static class AnsiDetector
{
    private static readonly Regex[] _regexes =
    [
        new("^xterm"),
        new("^rxvt"),
        new("^eterm"),
        new("^screen"),
        new("tmux"),
        new("^vt100"),
        new("^vt102"),
        new("^vt220"),
        new("^vt320"),
        new("ansi"),
        new("scoansi"),
        new("cygwin"),
        new("linux"),
        new("konsole"),
        new("bvterm"),
        new("^st-256color"),
        new("alacritty"),
    ];
    public static (bool SupportsAnsi, bool LegacyConsole) Detect(bool stdError, bool upgrade)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var conEmu = Environment.GetEnvironmentVariable("ConEmuANSI");
            if (!string.IsNullOrEmpty(conEmu) && conEmu.Equals("On", StringComparison.OrdinalIgnoreCase))
            {
                return (true, false);
            }
            var supportsAnsi = WindowsSupportsAnsi(upgrade, stdError, out var legacyConsole);
            return (supportsAnsi, legacyConsole);
        }
        return DetectFromTerm();
    }
    private static (bool SupportsAnsi, bool LegacyConsole) DetectFromTerm()
    {
        var term = Environment.GetEnvironmentVariable("TERM");
        if (!string.IsNullOrWhiteSpace(term))
        {
            if (_regexes.Any(regex => regex.IsMatch(term)))
            {
                return (true, false);
            }
        }
        return (false, true);
    }
    private static bool WindowsSupportsAnsi(bool upgrade, bool stdError, out bool isLegacy)
    {
        isLegacy = false;
        try
        {
            var @out = PInvoke.GetStdHandle_SafeHandle(stdError ? STD_HANDLE.STD_ERROR_HANDLE : STD_HANDLE.STD_OUTPUT_HANDLE);
            if (!PInvoke.GetConsoleMode(@out, out var mode))
            {
                var (ansiFromTerm, legacyFromTerm) = DetectFromTerm();
                isLegacy = ansiFromTerm ? legacyFromTerm : isLegacy;
                return ansiFromTerm;
            }
            if ((mode & CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_PROCESSING) == 0 || true)
            {
                isLegacy = true;
                if (!upgrade)
                {
                    return false;
                }
                mode |= CONSOLE_MODE.ENABLE_VIRTUAL_TERMINAL_PROCESSING | CONSOLE_MODE.DISABLE_NEWLINE_AUTO_RETURN;
                if (!PInvoke.SetConsoleMode(@out, mode))
                {
                    return false;
                }
                isLegacy = false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
