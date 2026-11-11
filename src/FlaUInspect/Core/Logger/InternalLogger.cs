using System.Collections.Concurrent;

namespace FlaUInspect.Core.Logger;

public sealed class InternalLogger : ILogger {
    public ConcurrentBag<InternalLoggerMessage> Messages { get; } = [];

    public void Log(LogLevel level, string? message, params object?[] args) {
        if (message != null) {
            string text = Format(message, args);
            Messages.Add(new InternalLoggerMessage(level, text));
            FileLog.Write(level.ToString().ToUpperInvariant(), text);
            OnLogEvent();
        }
    }

    /// <summary>
    ///     Callers pass exception text (<c>e.ToString()</c>, <c>ex.Message</c>) as the message, and those can contain
    ///     braces. <c>string.Format</c> throws on them, and the throw would happen inside the caller's catch block,
    ///     so the logger only formats when there are arguments and never lets a bad format escape.
    /// </summary>
    private static string Format(string message, object?[] args) {
        if (args.Length == 0) {
            return message;
        }

        try {
            return string.Format(message, args);
        } catch (FormatException) {
            return message + " | " + string.Join(", ", args);
        }
    }

    public event EventHandler? LogEvent;


    private void OnLogEvent() {
        LogEvent?.Invoke(this, EventArgs.Empty);
    }
}