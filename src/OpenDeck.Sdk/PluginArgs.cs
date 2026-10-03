namespace OpenDeck.Sdk;

/// <summary>
/// Command line passed by OpenDeck (Stream Deck SDK compatible):
/// <c>-port N -pluginUUID X -registerEvent registerPlugin -info {json}</c>
/// </summary>
public sealed record PluginArgs(int Port, string PluginUuid, string RegisterEvent, string InfoJson)
{
    public static PluginArgs Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                map[args[i].TrimStart('-')] = args[i + 1];
                i++;
            }
        }

        if (!map.TryGetValue("port", out var port) || !map.TryGetValue("pluginUUID", out var uuid))
            throw new ArgumentException("Missing -port or -pluginUUID. This executable must be launched by OpenDeck.");

        return new PluginArgs(
            int.Parse(port),
            uuid,
            map.GetValueOrDefault("registerEvent", "registerPlugin"),
            map.GetValueOrDefault("info", "{}"));
    }
}
