using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Threading.Tasks;
using AspectInjector.Broker;
using Serilog.Events;

namespace ParityProof.Core.Logging;

[Aspect(Scope.Global)]
public sealed class LogMethodAspect
{
    [Advice(Kind.Around)]
    public object? Around(
        [Argument(Source.Type)] Type type,
        [Argument(Source.Name)] string name,
        [Argument(Source.Metadata)] MethodBase method,
        [Argument(Source.Target)] Func<object[], object> target,
        [Argument(Source.Arguments)] object[] args,
        [Argument(Source.Triggers)] Attribute[] triggers)
    {
        if (method.IsConstructor)
        {
            return target(args);
        }

        LogEventLevel level = LogEventLevel.Debug;
        for (int i = 0; i < triggers.Length; i++)
        {
            if (triggers[i] is LogMethodAttribute attr)
            {
                level = attr.Level;
                break;
            }
        }

        if (!AppLogger.IsEnabled(level))
        {
            return target(args);
        }

        string formattedArgs = FormatArguments(method, args);
        AppLogger.Logger.Write(
            level,
            "[{TypeName}.{MethodName}] Entering with arguments: ({Arguments:l})",
            type.Name,
            name,
            formattedArgs);

        Stopwatch sw = Stopwatch.StartNew();
        object? result;
        try
        {
            result = target(args);
        }
        catch (Exception ex)
        {
            sw.Stop();
            AppLogger.Logger.Write(
                LogEventLevel.Error,
                ex,
                "[{TypeName}.{MethodName}] Failed after {ElapsedMs}ms with exception: {ErrorMessage}",
                type.Name,
                name,
                sw.ElapsedMilliseconds,
                ex.Message);
            throw;
        }

        if (result is Task task)
        {
            HandleAsyncTask(task, method, type, name, sw, level);
            return task;
        }

        sw.Stop();
        if (method is MethodInfo mi && mi.ReturnType == typeof(void))
        {
            AppLogger.Logger.Write(
                level,
                "[{TypeName}.{MethodName}] Exited in {ElapsedMs}ms",
                type.Name,
                name,
                sw.ElapsedMilliseconds);
        }
        else
        {
            string formattedResult = AppLogger.FormatValue(result);
            AppLogger.Logger.Write(
                level,
                "[{TypeName}.{MethodName}] Exited in {ElapsedMs}ms with return value: {ReturnValue:l}",
                type.Name,
                name,
                sw.ElapsedMilliseconds,
                formattedResult);
        }

        return result;
    }

    [UnconditionalSuppressMessage(
        "ReflectionAnalysis",
        "IL2075:UnrecognizedReflectionPattern",
        Justification = "Dynamic PropertyInfo retrieval on Task<T>.Result is safe because Task<T>.Result is preserved on generic Task instances.")]
    private static void HandleAsyncTask(
        Task task,
        MethodBase method,
        Type type,
        string name,
        Stopwatch sw,
        LogEventLevel level)
    {
        task.ContinueWith(
            completedTask =>
            {
                sw.Stop();
                if (completedTask.IsFaulted)
                {
                    Exception? actualEx = completedTask.Exception?.InnerException ?? completedTask.Exception;
                    AppLogger.Logger.Write(
                        LogEventLevel.Error,
                        actualEx,
                        "[{TypeName}.{MethodName}] Async failed after {ElapsedMs}ms with exception: {ErrorMessage}",
                        type.Name,
                        name,
                        sw.ElapsedMilliseconds,
                        actualEx?.Message ?? "Unknown error");
                }
                else if (completedTask.IsCanceled)
                {
                    AppLogger.Logger.Write(
                        level,
                        "[{TypeName}.{MethodName}] Async canceled after {ElapsedMs}ms",
                        type.Name,
                        name,
                        sw.ElapsedMilliseconds);
                }
                else
                {
                    bool isGenericTask = method is MethodInfo mi &&
                        mi.ReturnType.IsGenericType &&
                        mi.ReturnType.GetGenericTypeDefinition() == typeof(Task<>);

                    if (isGenericTask)
                    {
                        Type taskType = completedTask.GetType();
                        PropertyInfo? resultProp = taskType.GetProperty("Result");
                        object? taskResult = resultProp?.GetValue(completedTask);
                        string formattedResult = AppLogger.FormatValue(taskResult);
                        AppLogger.Logger.Write(
                            level,
                            "[{TypeName}.{MethodName}] Async completed in {ElapsedMs}ms with return value: {ReturnValue:l}",
                            type.Name,
                            name,
                            sw.ElapsedMilliseconds,
                            formattedResult);
                    }
                    else
                    {
                        AppLogger.Logger.Write(
                            level,
                            "[{TypeName}.{MethodName}] Async completed in {ElapsedMs}ms",
                            type.Name,
                            name,
                            sw.ElapsedMilliseconds);
                    }
                }
            },
            TaskContinuationOptions.ExecuteSynchronously);
    }

    private static string FormatArguments(MethodBase method, object[] args)
    {
        if (args is null || args.Length == 0)
        {
            return string.Empty;
        }

        ParameterInfo[] parameters = method.GetParameters();
        string[] parts = new string[args.Length];

        for (int i = 0; i < args.Length; i++)
        {
            string paramName = i < parameters.Length ? parameters[i].Name ?? $"arg{i}" : $"arg{i}";
            string val = AppLogger.FormatValue(args[i]);
            parts[i] = $"{paramName}: {val}";
        }

        return string.Join(", ", parts);
    }
}
