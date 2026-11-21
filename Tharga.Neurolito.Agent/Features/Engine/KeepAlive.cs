namespace Tharga.Neurolito.Agent.Features.Engine;

/// <summary>How long the engine is told to keep a model loaded after a run.</summary>
internal static class KeepAlive
{
    /// <summary>
    /// The larger of the request's timeout and the server's setting for this agent, so a long job never loses its model
    /// halfway and a machine whose owner wants models kept longer keeps them. A setting that is not positive counts as none;
    /// null with neither leaves the decision to the engine's own default.
    /// </summary>
    public static int? Choose(int? timeoutMinutes, int? serverMinutes)
    {
        var timeout = timeoutMinutes is > 0 ? timeoutMinutes : null;
        var server = serverMinutes is > 0 ? serverMinutes : null;
        if (timeout == null) return server;
        if (server == null) return timeout;
        return Math.Max(timeout.Value, server.Value);
    }
}
