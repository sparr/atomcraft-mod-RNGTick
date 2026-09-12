using Godot;

namespace RNGTick;

/// <summary>
/// Everything this mod says, funnelled through one prefix.
///
/// <c>user://logs/godot.log</c> is the only channel a mod has once the game is running, and
/// it carries every subsystem's output, so a consistent tag is what makes this mod's lines
/// findable in it.
/// </summary>
public static class Log
{
    public const string Tag = "RNGTick";

    public static void Info(string message) => GD.Print($"[{Tag}] {message}");
    public static void Warn(string message) => GD.PrintErr($"[{Tag}] {message}");
    public static void Error(string message) => GD.PrintErr($"[{Tag}] {message}");
}
