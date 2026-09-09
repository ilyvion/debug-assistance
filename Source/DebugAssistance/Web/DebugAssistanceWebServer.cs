namespace DebugAssistance.Web;

// Owns the DebugAssistanceServer + the background thread its blocking accept loop runs on.
// Start/stop here are explicit so DebugAssistanceMod can gate them on Settings.ServerEnabled and
// the settings screen's checkbox.
internal sealed class DebugAssistanceWebServer(ModContentPack content) : IDisposable
{
    private readonly string _sitePath = Path.Combine(content.RootDir, "Site");
    private DebugAssistanceServer? _server;
    private Thread? _serverThread;
    private bool _disposed;

    internal void Start(int port, bool allowExternalConnections)
    {
        if (_server is not null)
        {
            return;
        }

        _server = new DebugAssistanceServer(port, _sitePath, allowExternalConnections);
        _serverThread = new Thread(_server.Start);
        _serverThread.Start();
    }

    internal void Stop()
    {
        if (_server is not { } server)
        {
            return;
        }

        server.Stop();
        _serverThread!.Join();
        server.Dispose();
        _server = null;
        _serverThread = null;
    }

    internal void ChangePort(int port, bool allowExternalConnections)
    {
        Stop();
        Start(port, allowExternalConnections);
    }

    private void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                Stop();
                _server?.Dispose();
            }

            _disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}
