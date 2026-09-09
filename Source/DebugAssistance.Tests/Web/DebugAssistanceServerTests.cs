using System.Net;
using System.Net.Sockets;
using DebugAssistance.Web;
using RimTestRedux;

namespace DebugAssistance.Tests.Web;

[TestSuite]
internal static class DebugAssistanceServerTests
{
    private static int FindFreeTcpPort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    // Regression test for a bug where Stop() closed the HttpListener out from under the accept
    // loop's blocking GetContext() call, which threw an exception nothing caught, crashing the
    // process. Start()/HandleRequests() now races a pending accept against a stop signal instead
    // of relying on that exception, so Stop() should unblock the accept loop cleanly.
    [Test]
    public static void StopUnblocksAcceptLoopWithoutThrowing() =>
        Assert
            .ThatFunc(() =>
            {
                var port = FindFreeTcpPort();
                var server = new DebugAssistanceServer(
                    port,
                    Path.GetTempPath(),
                    allowExternalConnections: false
                );
                Exception? threadException = null;

                var thread = new Thread(() =>
                {
                    try
                    {
                        server.Start();
                    }
                    catch (Exception e)
                    {
                        threadException = e;
                    }
                });
                thread.Start();

                // Give the accept loop time to reach its blocking wait before Stop() interrupts it.
                Thread.Sleep(200);

                server.Stop();

                if (!thread.Join(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException(
                        "Server thread did not exit within 5 seconds of Stop() being called"
                    );
                }

                server.Dispose();

                if (threadException is not null)
                {
                    throw threadException;
                }
            })
            .Does.Not.Throw();

    [Test]
    public static void IsConnectionAllowedAcceptsLoopbackWhenExternalConnectionsDisallowed()
    {
        Assert
            .That(
                DebugAssistanceServer.IsConnectionAllowed(
                    IPAddress.Loopback,
                    allowExternalConnections: false
                )
            )
            .Is.EqualTo(true);
        Assert
            .That(
                DebugAssistanceServer.IsConnectionAllowed(
                    IPAddress.IPv6Loopback,
                    allowExternalConnections: false
                )
            )
            .Is.EqualTo(true);
    }

    [Test]
    public static void IsConnectionAllowedRejectsNonLoopbackWhenExternalConnectionsDisallowed() =>
        Assert
            .That(
                DebugAssistanceServer.IsConnectionAllowed(
                    IPAddress.Parse("192.168.1.42"),
                    allowExternalConnections: false
                )
            )
            .Is.EqualTo(false);

    [Test]
    public static void IsConnectionAllowedAcceptsNonLoopbackWhenExternalConnectionsAllowed() =>
        Assert
            .That(
                DebugAssistanceServer.IsConnectionAllowed(
                    IPAddress.Parse("192.168.1.42"),
                    allowExternalConnections: true
                )
            )
            .Is.EqualTo(true);
}
