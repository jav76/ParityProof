using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ParityProof.Core.Logging;

public static partial class StackTraceSanitizer
{
    private const string REDACTED_USER_PROFILE = "%USERPROFILE%";
    private const string REDACTED_UNIX_HOME = "~";
    private const string STACK_TRACE_OMITTED_MESSAGE = "    [Stack trace omitted per security policy]";
    private const int MAX_INNER_EXCEPTION_DEPTH = 10;

    [GeneratedRegex(@"(?i)[a-zA-Z]:\\(?:Users|Documents and Settings)\\[^\\]+", RegexOptions.Compiled)]
    private static partial Regex WindowsUserProfileRegex();

    [GeneratedRegex(@"/(?:home|Users)/[^/\s]+", RegexOptions.Compiled)]
    private static partial Regex UnixHomeRegex();

    public static string SanitizeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        string result = WindowsUserProfileRegex().Replace(text, REDACTED_USER_PROFILE);
        result = UnixHomeRegex().Replace(result, REDACTED_UNIX_HOME);

        return result;
    }

    public static string FormatException(Exception? exception, StackTracePolicy policy)
    {
        if (exception is null)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        AppendExceptionDetails(builder, exception, policy, depth: 0);
        return builder.ToString();
    }

    private static void AppendExceptionDetails(
        StringBuilder builder,
        Exception ex,
        StackTracePolicy policy,
        int depth)
    {
        if (depth > MAX_INNER_EXCEPTION_DEPTH)
        {
            builder.AppendLine("    ... [Remaining inner exceptions truncated to prevent infinite recursion]");
            return;
        }

        string indent = new(' ', depth * 2);

        if (depth > 0)
        {
            builder.AppendLine();
            builder.AppendLine($"{indent}--- Inner Exception (Depth {depth}) ---");
        }

        builder.AppendLine($"{indent}Type: {ex.GetType().FullName}");

        string message = policy switch
        {
            StackTracePolicy.Full => ex.Message,
            _ => SanitizeText(ex.Message)
        };
        builder.AppendLine($"{indent}Message: {message}");
        builder.AppendLine($"{indent}HResult: 0x{ex.HResult:X8}");

        if (!string.IsNullOrEmpty(ex.Source))
        {
            builder.AppendLine($"{indent}Source: {ex.Source}");
        }

        if (ex.TargetSite is not null)
        {
            builder.AppendLine($"{indent}TargetSite: {ex.TargetSite.DeclaringType?.FullName}.{ex.TargetSite.Name}");
        }

        builder.AppendLine($"{indent}Stack Trace:");
        switch (policy)
        {
            case StackTracePolicy.None:
                builder.AppendLine($"{indent}{STACK_TRACE_OMITTED_MESSAGE}");
                break;

            case StackTracePolicy.Sanitized:
                if (string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    builder.AppendLine($"{indent}    (No stack trace available)");
                }
                else
                {
                    string sanitizedStackTrace = SanitizeText(ex.StackTrace);
                    builder.AppendLine(sanitizedStackTrace);
                }
                break;

            case StackTracePolicy.Full:
            default:
                if (string.IsNullOrWhiteSpace(ex.StackTrace))
                {
                    builder.AppendLine($"{indent}    (No stack trace available)");
                }
                else
                {
                    builder.AppendLine(ex.StackTrace);
                }
                break;
        }

        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
        {
            for (int i = 0; i < agg.InnerExceptions.Count; i++)
            {
                AppendExceptionDetails(builder, agg.InnerExceptions[i], policy, depth + 1);
            }
        }
        else if (ex.InnerException is not null)
        {
            AppendExceptionDetails(builder, ex.InnerException, policy, depth + 1);
        }
    }
}
