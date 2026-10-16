namespace PCL.Core.Minecraft.Launcher;

public enum LaunchProgressPhase { Launcher, Game, Ready, Failed }

/// <summary>Progress is scoped to the current phase, never a guessed combined percentage.</summary>
public sealed record LaunchProgressSnapshot(
    LaunchProgressPhase Phase,
    string Stage,
    string Detail,
    double? Progress,
    bool TelemetryConnected = false,
    string? Warning = null,
    string? Target = null,
    long? Current = null,
    long? Total = null);
