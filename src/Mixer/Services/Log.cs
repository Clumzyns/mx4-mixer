using Mixer.Config;

namespace Mixer.Services;

public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private static void Write(string level, string message, Exception? exception)
    {
        try
        {
            var dir = ConfigStore.DirectoryPath;
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "mixer.log");
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {message}";
            if (exception != null)
                line += " " + exception.GetType().Name + ": " + exception.Message;

            lock (Gate)
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    var previous = path + ".old";
                    if (File.Exists(previous)) File.Delete(previous);
                    File.Move(path, previous);
                }
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must not take the mixer down.
        }
    }
}
