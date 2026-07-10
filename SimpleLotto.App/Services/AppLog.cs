using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace SimpleLotto.App.Services;

public static class AppLog
{
    private static readonly object Gate = new();

    public static string LogDirectory
    {
        get
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SimpleLotto",
                "logs");
            Directory.CreateDirectory(directory);
            return directory;
        }
    }

    public static string CurrentLogPath => Path.Combine(
        LogDirectory,
        $"simplelotto-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) =>
        Write("INFO", message, exception: null);

    public static void Error(string message, Exception exception) =>
        Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture))
                .Append(" [")
                .Append(level)
                .Append("] ")
                .AppendLine(message);

            if (exception is not null)
                builder.AppendLine(exception.ToString());

            lock (Gate)
            {
                File.AppendAllText(CurrentLogPath, builder.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never become the reason an operator workflow fails.
        }
    }
}
