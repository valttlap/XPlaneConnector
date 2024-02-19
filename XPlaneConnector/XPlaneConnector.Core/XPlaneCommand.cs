namespace XPlaneConnector.Core;

public sealed class XPlaneCommand(string command, string description)
{
    public string Command { get; } = command;
    public string Description { get; } = description;
}
