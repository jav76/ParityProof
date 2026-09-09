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
            "{ApplicationVersion} starting up (Commit: {CommitSha}). MinimumLevel: {MinimumLevel}, FileLogging: {FileEnabled}, SqliteLogging: {SqliteEnabled}",
            BuildInfo.Current.DisplayVersion,
            BuildInfo.Current.CommitSha,
            options.MinimumLevel,
            options.EnableFile,
            options.EnableSqlite);

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
