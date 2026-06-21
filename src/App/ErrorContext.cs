using System.Text.RegularExpressions;

namespace RuneshapePriceChecker;

internal static partial class ErrorContext
{
    private static readonly Regex StackFrameRegex = StackFrameRegexGen();

    /// Extracts a concise error context string from an exception: file name and line number of the first user-code stack frame.
    public static string FromException(Exception ex)
    {
        try
        {
            var stack = ex.StackTrace;
            if (string.IsNullOrEmpty(stack))
                return ex.GetType().Name;

            var match = StackFrameRegex.Match(stack);
            if (match.Success)
            {
                var file = Path.GetFileName(match.Groups["file"].Value);
                var line = match.Groups["line"].Value;
                return $"{ex.GetType().Name} at {file}:{line}";
            }

            // Fallback: first line of stack
            var first = stack.AsSpan().IndexOf('\n');
            var firstLine = first > 0 ? stack[..first].Trim() : stack.Trim();
            return $"{ex.GetType().Name}: {firstLine}";
        }
        catch
        {
            return ex.GetType().Name;
        }
    }

    [GeneratedRegex(@"\sin\s(?<file>[^:]+):line\s(?<line>\d+)", RegexOptions.Compiled)]
    private static partial Regex StackFrameRegexGen();
}
