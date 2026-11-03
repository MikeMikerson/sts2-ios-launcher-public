using System;
using Godot;
using STS2Mobile.Steam.Abstractions;

namespace STS2Mobile.FullGame;

/// <summary>Routes Steam service diagnostics to the persistent development log. Messages carry no credentials.</summary>
public sealed class GodotSteamLogger : ILogger
{
    public static readonly GodotSteamLogger Instance = new();
    public void Info(string tag, string message, Exception? exception = null) => GD.Print(Format(tag, message, exception));
    public void Warn(string tag, string message, Exception? exception = null) => GD.Print("WARN " + Format(tag, message, exception));
    public void Error(string tag, string message, Exception? exception = null) => GD.PrintErr(Format(tag, message, exception));
    private static string Format(string tag, string message, Exception? exception) =>
        exception == null ? $"[Steam/{tag}] {message}" : $"[Steam/{tag}] {message}: {exception.GetType().Name}: {exception.Message}";
}
