namespace STS2Mobile.Steam.Abstractions;

public interface ILogger
{
    void Info(string tag, string message, Exception? exception = null);

    void Warn(string tag, string message, Exception? exception = null);

    void Error(string tag, string message, Exception? exception = null);
}
