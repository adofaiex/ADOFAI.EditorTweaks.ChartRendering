using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace ADOFAI.EditorTweaks.ChartRendering.Features.WebUi
{
    // HttpListener can only bind to loopback without an administrator URL ACL, and it rejects a
    // Host header that does not match its prefix. This proxy accepts LAN connections on 0.0.0.0,
    // rewrites Host to 127.0.0.1:<localPort>, and forwards the rest to the loopback listener. The
    // existing HTTP server, token checks, SSE stream and static files keep working unchanged.
    internal sealed class WebUiLanProxy
    {
        private const int MaxConnections = 32;
        private const int BufferSize = 16 * 1024;
        private const int MaxHeaderBytes = 64 * 1024;

        private readonly TcpListener listener;
        private readonly int localPort;
        private readonly Thread acceptThread;
        private int connections;
        private volatile bool stopping;

        private WebUiLanProxy(TcpListener listener, int localPort)
        {
            this.listener = listener;
            this.localPort = localPort;
            acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "ADOFAI.EditorTweaks.ChartRendering.WebUiLanProxy"
            };
            acceptThread.Start();
        }

        public static WebUiLanProxy? TryStart(int localPort, out int lanPort, out string error)
        {
            error = string.Empty;
            for (int port = localPort + 1; port <= localPort + 100 && port <= 65535; port++)
            {
                TcpListener candidate = new TcpListener(IPAddress.Any, port);
                try
                {
                    candidate.Start();
                    lanPort = port;
                    return new WebUiLanProxy(candidate, localPort);
                }
                catch (Exception exception)
                {
                    error = exception.Message;
                    try
                    {
                        candidate.Stop();
                    }
                    catch
                    {
                    }
                }
            }

            lanPort = 0;
            if (string.IsNullOrEmpty(error))
            {
                error = "No free LAN port was found.";
            }

            return null;
        }

        public void Stop()
        {
            if (stopping)
            {
                return;
            }

            stopping = true;
            try
            {
                listener.Stop();
            }
            catch
            {
            }
        }

        private void AcceptLoop()
        {
            while (!stopping)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    if (stopping)
                    {
                        return;
                    }

                    continue;
                }

                if (Interlocked.Increment(ref connections) > MaxConnections)
                {
                    Interlocked.Decrement(ref connections);
                    try
                    {
                        client.Close();
                    }
                    catch
                    {
                    }

                    continue;
                }

                Thread thread = new Thread(() => Handle(client))
                {
                    IsBackground = true,
                    Name = "ADOFAI.EditorTweaks.ChartRendering.WebUiLanClient"
                };
                thread.Start();
            }
        }

        private void Handle(TcpClient client)
        {
            TcpClient? server = null;
            try
            {
                client.NoDelay = true;
                server = new TcpClient();
                server.NoDelay = true;
                server.Connect(IPAddress.Loopback, localPort);

                NetworkStream clientStream = client.GetStream();
                NetworkStream serverStream = server.GetStream();
                if (!TryReadRequestHeader(clientStream, out byte[] header, out byte[] body))
                {
                    return;
                }

                byte[] rewritten = RewriteRequestHeader(header, localPort);
                serverStream.Write(rewritten, 0, rewritten.Length);
                if (body.Length > 0)
                {
                    serverStream.Write(body, 0, body.Length);
                }

                serverStream.Flush();

                Thread upstream = new Thread(() => Pump(clientStream, serverStream))
                {
                    IsBackground = true,
                    Name = "ADOFAI.EditorTweaks.ChartRendering.WebUiLanUpstream"
                };
                upstream.Start();
                Pump(serverStream, clientStream);
                upstream.Join(250);
            }
            catch
            {
            }
            finally
            {
                try
                {
                    server?.Close();
                }
                catch
                {
                }

                try
                {
                    client.Close();
                }
                catch
                {
                }

                Interlocked.Decrement(ref connections);
            }
        }

        private static bool TryReadRequestHeader(NetworkStream stream, out byte[] header, out byte[] body)
        {
            header = Array.Empty<byte>();
            body = Array.Empty<byte>();
            byte[] buffer = new byte[BufferSize];
            using (MemoryStream accumulated = new MemoryStream())
            {
                while (accumulated.Length <= MaxHeaderBytes)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        return false;
                    }

                    accumulated.Write(buffer, 0, read);
                    byte[] data = accumulated.ToArray();
                    int end = FindHeaderEnd(data);
                    if (end < 0)
                    {
                        continue;
                    }

                    int headerLength = end + 4;
                    header = new byte[headerLength];
                    Array.Copy(data, 0, header, 0, headerLength);
                    int bodyLength = data.Length - headerLength;
                    body = new byte[bodyLength];
                    if (bodyLength > 0)
                    {
                        Array.Copy(data, headerLength, body, 0, bodyLength);
                    }

                    return true;
                }
            }

            return false;
        }

        private static int FindHeaderEnd(byte[] data)
        {
            for (int i = 0; i <= data.Length - 4; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                {
                    return i;
                }
            }

            return -1;
        }

        // HttpListener validates the Host header against its prefix, so the LAN host must become
        // 127.0.0.1:<localPort>. Forcing Connection: close keeps one request per proxy connection;
        // EventSource still works because the server streams the response before closing.
        private static byte[] RewriteRequestHeader(byte[] header, int localPort)
        {
            string text = Encoding.ASCII.GetString(header);
            int end = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            string head = end >= 0 ? text.Substring(0, end) : text;
            string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            List<string> output = new List<string>();
            if (lines.Length > 0)
            {
                output.Add(lines[0]);
            }

            bool hasHost = false;
            bool hasConnection = false;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add("Host: 127.0.0.1:" + localPort);
                    hasHost = true;
                }
                else if (line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
                {
                    output.Add("Connection: close");
                    hasConnection = true;
                }
                else
                {
                    output.Add(line);
                }
            }

            if (!hasHost)
            {
                output.Add("Host: 127.0.0.1:" + localPort);
            }

            if (!hasConnection)
            {
                output.Add("Connection: close");
            }

            return Encoding.ASCII.GetBytes(string.Join("\r\n", output) + "\r\n\r\n");
        }

        private static void Pump(NetworkStream from, NetworkStream to)
        {
            byte[] buffer = new byte[BufferSize];
            try
            {
                while (true)
                {
                    int read = from.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        return;
                    }

                    to.Write(buffer, 0, read);
                    to.Flush();
                }
            }
            catch
            {
            }
        }
    }
}
