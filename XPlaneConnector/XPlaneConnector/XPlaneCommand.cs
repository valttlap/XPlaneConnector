namespace XPlaneConnector;

public sealed class XPlaneCommand(string command, string description)
{
    private readonly string command = command;
    private readonly string description = description;

    public string Command { get { return command; } }
    public string Description { get { return description; } }
}

