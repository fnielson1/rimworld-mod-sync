using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace ModSync
{
    /// <summary>Blocking network calls to a host. Safe to use from background threads.</summary>
    public class SyncClient
    {
        private readonly string host;
        private readonly int[] fallbackPorts;
        private int port;

        public string Address => $"{host}:{port}";

        public SyncClient(string address)
        {
            ParseAddress(address, out host, out port);
            fallbackPorts = new int[0];
        }

        /// <summary>Hello tries each port in order; the first that answers is used from then on.</summary>
        public SyncClient(string host, params int[] ports)
        {
            if (string.IsNullOrEmpty(host) || ports.Length == 0)
                throw new FormatException("Enter the host's address.");
            this.host = host;
            port = ports[0];
            fallbackPorts = ports;
        }

        public static void ParseAddress(string address, out string host, out int port)
        {
            address = (address ?? "").Trim();
            port = Protocol.DefaultPort;
            host = address;
            int colon = address.LastIndexOf(':');
            // A single colon means host:port; more than one is a bare IPv6 address.
            if (colon > 0 && address.IndexOf(':') == colon)
            {
                host = address.Substring(0, colon);
                if (!int.TryParse(address.Substring(colon + 1), out port) || port <= 0 || port > 65535)
                    throw new FormatException("Invalid port in address: " + address);
            }
            if (host.Length == 0)
                throw new FormatException("Enter the host's address.");
        }

        public HostInfo Hello()
        {
            if (fallbackPorts.Length <= 1)
                return HelloOnce();
            foreach (var candidate in fallbackPorts)
            {
                port = candidate;
                try
                {
                    return HelloOnce();
                }
                catch (Exception ex) when (ex is IOException || ex is SocketException)
                {
                    if (candidate == fallbackPorts[fallbackPorts.Length - 1])
                        throw new IOException($"Could not reach Mod Sync on {host} (tried TCP port {string.Join(", ", fallbackPorts)}). " +
                                              "The host needs Mod Sync active, and players outside their network need one of those ports forwarded.");
                }
            }
            throw new InvalidOperationException(); // unreachable
        }

        private HostInfo HelloOnce()
        {
            return Request(Protocol.CmdHello, null, (stream, r) => new HostInfo
            {
                ProtocolVersion = r.ReadInt32(),
                ModVersion = r.ReadString(),
                WorkshopId = r.ReadUInt64(),
            });
        }

        public Manifest FetchManifest()
        {
            return Request(Protocol.CmdManifest, null, (stream, r) => Manifest.Read(r));
        }

        public void DownloadMod(string folderName, string targetDir, Action<long> onBytes)
        {
            Request(Protocol.CmdMod, folderName, (stream, r) =>
            {
                ModFolder.Unpack(stream, targetDir, onBytes);
                return true;
            });
        }

        private T Request<T>(byte cmd, string arg, Func<Stream, BinaryReader, T> readPayload)
        {
            using (var client = new TcpClient())
            {
                var connect = client.BeginConnect(host, port, null, null);
                if (!connect.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                    throw new IOException($"Could not reach {host}:{port}. Is the host hosting, and is the port open?");
                client.EndConnect(connect);
                client.ReceiveTimeout = 60000;

                using (var stream = new BufferedStream(client.GetStream(), 65536))
                using (var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
                using (var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
                {
                    w.Write(Protocol.Magic);
                    w.Write(Protocol.Version);
                    w.Write(cmd);
                    if (arg != null)
                        w.Write(arg);
                    w.Flush();

                    if (r.ReadByte() != Protocol.StatusOk)
                        throw new IOException("Host refused: " + r.ReadString());
                    return readPayload(stream, r);
                }
            }
        }
    }
}
