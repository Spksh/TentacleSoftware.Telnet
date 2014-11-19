using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TentacleSoftware.Telnet
{
    public class TelnetClient : IDisposable
    {
        private readonly int _port;
        private readonly string _host;
        private readonly TimeSpan _sendRate;
        private readonly SemaphoreSlim _sendRateLimit;
        private readonly CancellationTokenSource _internalCancellation;

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
            _internalCancellation = new CancellationTokenSource();

            token.Register(() => _internalCancellation.Cancel());
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

            }, _internalCancellation.Token).ContinueWith(connect => WaitForMessage(connect), _internalCancellation.Token);
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

                await _tcpClient.GetStream().WriteAsync(proxyRequest, 0, proxyRequest.Length, _internalCancellation.Token);
                await _tcpClient.GetStream().ReadAsync(proxyResponse, 0, proxyResponse.Length, _internalCancellation.Token);

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

            }, _internalCancellation.Token).ContinueWith(connectWithProxy => WaitForMessage(connectWithProxy), _internalCancellation.Token);
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
                await _sendRateLimit.WaitAsync(_internalCancellation.Token);

                if (_internalCancellation.Token.IsCancellationRequested)
                {
                    // Don't bother sending
                    return;
                }

                try
                {
                    await _tcpWriter.WriteLineAsync(message);
                }
                catch (ObjectDisposedException)
                {
                    // This happens during ReadLineAsync() when we call Disconnect() and close the underlying stream
                    // This is an expected exception during disconnection if we're in the middle of a send
                    Trace.TraceInformation("Send failed: TcpWriter or TcpWriter.BaseStream disposed. This is expected after calling Disconnect().");
                }
                catch (IOException error)
                {
                    // This happens when we start WriteLineAsync() if the socket is disconnected unexpectedly
                    Trace.TraceError("Send failed: {0}", error);
                    throw;
                }
                catch (Exception error)
                {
                    Trace.TraceError("Send failed: {0}", error);
                    throw;
                }

            }, _internalCancellation.Token);

            // Flood protection
            task.ContinueWith(async send =>
            {
                if (!send.IsCanceled && !send.IsFaulted && !_internalCancellation.Token.IsCancellationRequested)
                {
                    // Wait some time to prevent flooding
                    await Task.Delay(_sendRate, _internalCancellation.Token);
                }

                // Exit our lock
                _sendRateLimit.Release();

            }, _internalCancellation.Token);

            return task;
        }

        private Task WaitForMessage(Task connected)
        {
            if (connected.IsFaulted)
            {
                if (connected.Exception != null)
                {
                    ExceptionDispatchInfo.Capture(connected.Exception.InnerException).Throw();
                }
                else
                {
                    throw new InvalidOperationException("Connect task faulted. Aborting.");
                }
            }

            if (!connected.IsCompleted)
            {
                throw new InvalidOperationException("Connect task failed to complete. Aborting.");
            }

            return Task.Run(async () =>
            {
                while (!_internalCancellation.Token.IsCancellationRequested)
                {
                    try
                    {
                        string message = await _tcpReader.ReadLineAsync();

                        if (message == null)
                        {
                            // This happens if the server closes the connection and we haven't noticed yet
                            Trace.TraceInformation("WaitForMessage aborted: Connection closed by the server.");

                            // Bail out
                            Disconnect();

                            break;
                        }

                        OnMessageReceived(message);
                    }
                    catch (ObjectDisposedException)
                    {
                        // This happens during ReadLineAsync() when we call Disconnect() and close the underlying stream
                        // This is an expected exception during disconnection
                        Trace.TraceInformation("WaitForMessage aborted: TcpReader or TcpReader.BaseStream disposed. This is expected after calling Disconnect().");
                    }
                    catch (IOException error)
                    {
                        // This happens when we start ReadLineAsync() if the socket is disconnected unexpectedly
                        Trace.TraceError("WaitForMessage aborted: {0}", error);
                        throw;
                    }
                    catch (Exception error)
                    {
                        Trace.TraceError("WaitForMessage aborted: {0}", error);
                        throw;
                    }
                }
            }, _internalCancellation.Token);
        }

        /// <summary>
        /// Disconnecting will leave TelnetClient in an unusable state.
        /// </summary>
        public void Disconnect()
        {
            try
            {
                _internalCancellation.Cancel();

                // Both reader and writer use the TcpClient.GetStream(), and closing them will close the underlying stream
                // So closing the stream for TcpClient is redundant
                // But it means we're triple sure!
                if (_tcpClient != null)
                {
                    if (_tcpClient.Connected)
                    {
                        _tcpClient.GetStream().Close();
                    }

                    _tcpClient.Close();
                }

                if (_tcpReader != null)
                {
                    _tcpReader.Close();
                }

                if (_tcpWriter != null)
                {
                    _tcpWriter.Close();
                }

                OnConnectionClosed();
            }
            catch (Exception error)
            {
                Trace.TraceError("Disconnect error: {0}", error);
            }
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

        private bool _disposed = false;

        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }
            
            if (disposing)
            {
                Disconnect();
            }

            _disposed = true;
        }
    }
}
