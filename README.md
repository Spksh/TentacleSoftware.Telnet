TentacleSoftware.Telnet
=======================

A simple task-based event-driven Telnet client that supports SOCKS4 proxies.

Nuget Package: https://www.nuget.org/packages/TentacleSoftware.Telnet/

Step 1. Instantiate a new TelnetClient, passing in destination host and port. Include a throttle timespan to avoid flooding the server.

```
_telnetClient = new TelnetClient("myhost.somewhere.com", 1153, TimeSpan.FromSeconds(3), _cancellationSource.Token);
```

Step 2. Subscribe to events

```
_telnetClient.ConnectionClosed += HandleConnectionClosed;
_telnetClient.MessageReceived += HandleMessageReceived;
```

Step 3. Connect with or without a SOCKS4 proxy

```
await _telnetClient.Connect();
```
or
```
await _telnetClient.Connect("proxy.somewhere.com", 3455, null);
```

Step 4. Handle application-specific welcome messages from your telnet server

```
private void HandleMessageReceived(object sender, string message)
{
    ...
}
```

Step 5. Send something in response.

```
await _telnetClient.Send("magic!");
```

For extra bonus points:

To observe errors inside the message reading loop, add a TraceListener:

```
Trace.Listeners.Add(new ConsoleTraceListener(true));
```
