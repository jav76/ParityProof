using System;
using Avalonia;
using ParityProof.Core.Logging;
using ParityProof.Core.Models;

namespace ParityProof.App;

internal sealed class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        LoggingOptions options = LoggingOptions.Parse(args);
        AppLogger.Initialize(options);

        AppLogger.Logger.Information(
            "{ApplicationVersion} starting up (Commit: {CommitSha}). MinimumLevel: {MinimumLevel}, FileLogging: {FileEnabled}, SqliteLogging: {SqliteEnabled}, EventLog: {EventLogEnabled}",
            BuildInfo.Current.DisplayVersion,
            BuildInfo.Current.CommitSha,
            options.MinimumLevel,
            options.EnableFile,
            options.EnableSqlite,
            options.EnableEventLog);

        AppDomain.CurrentDomain.UnhandledException += (sender, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
            {
                AppLogger.Logger.Fatal(ex, "Unhandled AppDomain exception occurred. Process terminating: {IsTerminating}", eventArgs.IsTerminating);
            }
            else
            {
                AppLogger.Logger.Fatal(
                    "Unhandled AppDomain exception of non-Exception type: {ExceptionObject}. Process terminating: {IsTerminating}",
                    eventArgs.ExceptionObject,
                    eventArgs.IsTerminating);
            }

            AppLogger.CloseAndFlush();
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, eventArgs) =>
        {
            AppLogger.Logger.Error(eventArgs.Exception, "Unobserved background task exception encountered");
        };

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            AppLogger.Logger.Fatal(ex, "ParityProof terminated unexpectedly");
            throw;
        }
        finally
        {
            AppLogger.CloseAndFlush();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
