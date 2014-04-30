using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TentacleSoftware.Telnet
{
    public class TelnetClient
    {
        private readonly int _port;
        private readonly string _host;
        private readonly TimeSpan _sendRate;
        private readonly SemaphoreSlim _sendRateLimit;
        private readonly CancellationToken _cancellationToken;

        private TcpClient _tcpClient;
        private StreamReader _tcpReader;
        private StreamWriter _tcpWriter;

        public EventHandler<string> MessageReceived;
        public EventHandler ConnectionClosed;

        /// <summary>
        /// Simple telnet client
        /// </summary>
        /// <param name="host">Destination Hostname or IP</param>
        /// <param name="port">Destination TCP port number</param>
        /// <param name="sendRate">Minimum time span between sends. This is a throttle to prevent flooding the server.</param>
        /// <param name="token"></param>
        public TelnetClient(string host, int port, TimeSpan sendRate, CancellationToken token)
        {
            _host = host;
            _port = port;
            _sendRate = sendRate;
            _sendRateLimit = new SemaphoreSlim(1);
            _cancellationToken = token;
        }

        /// <summary>
        /// Connect and wait for incoming messages. When this task completes you are connected.
        /// </summary>
        /// <returns></returns>
        public Task Connect()
        {
            return Task.Run(() =>
            {
                _tcpClient = new TcpClient(_host, _port);
                _tcpReader = new StreamReader(_tcpClient.GetStream());
                _tcpWriter = new StreamWriter(_tcpClient.GetStream()) { AutoFlush = true };

            }, _cancellationToken).ContinueWith(_ => WaitForMessage(), _cancellationToken);
        }

        /// <summary>
        /// Connect via SOCKS4 proxy. See http://en.wikipedia.org/wiki/SOCKS#SOCKS4.
        /// </summary>
        /// <param name="socks4ProxyHost"></param>
        /// <param name="socks4ProxyPort"></param>
        /// <param name="socks4ProxyUser"></param>
        /// <returns></returns>
        public Task Connect(string socks4ProxyHost, int socks4ProxyPort, string socks4ProxyUser)
        {
            return Task.Run(async () =>
            {
                // Simple implementation of http://en.wikipedia.org/wiki/SOCKS#SOCKS4
                // Similar to http://biko.codeplex.com/

                byte[] hostAddress = Dns.GetHostAddresses(_host).First().GetAddressBytes();
                byte[] hostPort = new byte[2]; // 16-bit number
                hostPort[0] = Convert.ToByte(_port / 256);
                hostPort[1] = Convert.ToByte(_port % 256);
                byte[] proxyUserId = Encoding.ASCII.GetBytes(socks4ProxyUser ?? string.Empty); // Can't pass in null

                // We build a "please connect me" packet to send to the proxy
                // Then we wait for an ack
                // If successful, we can use the TelnetClient directly and traffic will be proxied
                byte[] proxyRequest = new byte[9 + proxyUserId.Length];

                proxyRequest[0] = 4; // SOCKS4;
                proxyRequest[1] = 0x01; // Connect (we don't support Bind);

                hostPort.CopyTo(proxyRequest, 2);
                hostAddress.CopyTo(proxyRequest, 4);
                proxyUserId.CopyTo(proxyRequest, 8);

                proxyRequest[8 + proxyUserId.Length] = 0x00; // UserId terminator

                // Response
                // - First byte is null
                // - Second byte is our result code (we want 0x5a Request granted)
                // - Last 6 bytes should be ignored
                byte[] proxyResponse = new byte[8];

                _tcpClient = new TcpClient(socks4ProxyHost, socks4ProxyPort);

                await _tcpClient.GetStream().WriteAsync(proxyRequest, 0, proxyRequest.Length, _cancellationToken);
                await _tcpClient.GetStream().ReadAsync(proxyResponse, 0, proxyResponse.Length, _cancellationToken);

                if (proxyResponse[1] != 0x5a) // Request granted
                {
                    switch (proxyResponse[1])
                    {
                        case 0x5b:
                            throw new InvalidOperationException("Request rejected or failed.");
                        case 0x5c:
                            throw new InvalidOperationException("Request failed because client is not running identd (or not reachable from the server).");
                        case 0x5d:
                            throw new InvalidOperationException("Request failed because client's identd could not confirm the user ID string in the request.");
                        default:
                            throw new InvalidOperationException("Unknown error occured.");
                    }
                }

                _tcpReader = new StreamReader(_tcpClient.GetStream());
                _tcpWriter = new StreamWriter(_tcpClient.GetStream()) { AutoFlush = true };

            }, _cancellationToken).ContinueWith(_ => WaitForMessage(), _cancellationToken);
        }

        public Task Send(string message)
        {
            if (message == null)
            {
                return Task.FromResult(1);
            }

            Task task = Task.Run(async () =>
            {
                // Wait for any previous send commands to finish and release the semaphore
                // This throttles our commands
                await _sendRateLimit.WaitAsync(_cancellationToken);

                if (_cancellationToken.IsCancellationRequested)
                {
                    // Don't bother sending
                    return;
                }

                await _tcpWriter.WriteLineAsync(message);

            }, _cancellationToken);

            // Flood protection
            task.ContinueWith(async send =>
            {
                if (!send.IsCanceled && !send.IsFaulted && !_cancellationToken.IsCancellationRequested)
                {
                    // Wait some time to prevent flooding
                    await Task.Delay(_sendRate, _cancellationToken);
                }

                // Exit our lock
                _sendRateLimit.Release();

            }, _cancellationToken);

            return task;
        }

        private Task WaitForMessage()
        {
            return Task.Run(async () =>
            {
                while (!_cancellationToken.IsCancellationRequested)
                {
                    if (_tcpReader == null)
                    {
                        // We've probably disconnected and disposed of our reader
                        break;
                    }

                    try
                    {
                        string message = await _tcpReader.ReadLineAsync();

                        if (message == null)
                        {
                            OnConnectionClosed();
                            break;
                        }

                        OnMessageReceived(message);
                    }
                    catch (Exception error)
                    {
                        Console.WriteLine(error);
                        throw;
                    }

                }
            }, _cancellationToken);
        }

        private void OnMessageReceived(string message)
        {
            EventHandler<string> messageReceived = MessageReceived;

            if (messageReceived != null)
            {
                messageReceived(this, message);
            }
        }

        private void OnConnectionClosed()
        {
            EventHandler connectionClosed = ConnectionClosed;

            if (connectionClosed != null)
            {
                connectionClosed(this, new EventArgs());
            }
        }
    }
}
