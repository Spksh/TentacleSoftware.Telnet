TentacleSoftware.Telnet
=======================

A simple task-based event-driven Telnet client that supports SOCKS4 proxies.

1. Instantiate a new TelnetClient, passing in destination host and port. Include a throttle timespan to avoid flooding the server.

```
_telnetClient = new TelnetClient("myhost.somewhere.com", 1153, TimeSpan.FromSeconds(3), _cancellationSource.Token);
```

2. Subscribe to events

```
_telnetClient.ConnectionClosed += HandleConnectionClosed;
_telnetClient.MessageReceived += HandleMessageReceived;
```

3. Connect with our without a SOCKS4 proxy

```
await _telnetClient.Connect();
```
or
```
await _telnetClient.Connect("proxy.somewhere.com", 3455, null);
```

4. Handle application-specific welcome messages from your telnet server

```
private void HandleMessageReceived(object sender, string message)
{
    ...
}

5. Send something in response.

```
await _telnetClient.Send("magic!");
```
