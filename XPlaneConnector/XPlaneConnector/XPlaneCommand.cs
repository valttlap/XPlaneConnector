namespace XPlaneConnector;

public sealed class XPlaneCommand(string command, string description)
{
    private readonly string _command = command;
    private readonly string _description = description;

    public string Command { get { return _command; } }
    public string Description { get { return _description; } }
}
