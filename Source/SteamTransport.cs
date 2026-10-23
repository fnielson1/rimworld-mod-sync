using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Steamworks;
using Verse;
using Verse.Steam;

namespace ModSync
{
    /// <summary>
    /// Carries Mod Sync requests over Steam peer-to-peer networking, for Multiplayer games joined through Steam,
    /// where the client has no IP address for the host. Each request gets its own stream, as a TCP connection would.
    ///
    /// Uses its own P2P channel. Multiplayer reads only channel 0 (host) and a random 16-bit channel per client,
    /// so it never sees these packets, and the P2P session Multiplayer already opened between the two players carries them.
    ///
    /// Packet: kind byte, stream id (int32), then the data for Data packets. The client picks a new stream id for
    /// each request, and the first Data packet with an unknown id opens a stream on the host.
    /// </summary>
    public static class SteamTransport
    {
        public const string AddressPrefix = "steam:";

        private const int Channel = 0x4D535943; // "MSYC": outside the 16-bit range Multiplayer uses
        private const byte KindData = 0;
        private const byte KindClose = 1;
        private const int HeaderSize = 5;
        private const int MaxChunk = 512 * 1024;      // Steam allows up to 1 MB per reliable packet
        private const int MaxQueued = 4 * 1024 * 1024; // wait for Steam to send this much before queuing more
        private const int ReadTimeoutMs = 30000;

        private static readonly object Lock = new object();
        private static readonly Dictionary<StreamKey, P2PStream> streams = new Dictionary<StreamKey, P2PStream>();
        private static readonly Random random = new Random();
        private static Thread pump;
        private static volatile bool serving;

        /// <summary>Whether requests from other players are answered. Set while hosting a Multiplayer game over Steam.</summary>
        public static bool Serving => serving;

        public static void StartServing()
        {
            if (!SteamManager.Initialized)
            {
                Log.Warning("[ModSync] Steam isn't running, so players joining through Steam can't sync.");
                return;
            }
            serving = true;
            EnsurePump();
            SyncServer.RefreshStatus();
            Log.Message("[ModSync] Answering Mod Sync requests over Steam");
        }

        public static void StopServing()
        {
            if (!serving)
                return;
            serving = false;
            SyncServer.RefreshStatus();
            Log.Message("[ModSync] Stopped answering Mod Sync requests over Steam");
        }

        /// <summary>Parses "steam:&lt;SteamID64&gt;", Multiplayer's connection string for a Steam host.</summary>
        public static bool TryParseAddress(string address, out ulong steamId)
        {
            steamId = 0;
            address = (address ?? "").Trim();
            return address.StartsWith(AddressPrefix, StringComparison.OrdinalIgnoreCase)
                   && ulong.TryParse(address.Substring(AddressPrefix.Length), out steamId)
                   && steamId != 0;
        }

        /// <summary>Opens a stream to a host for one request. Safe to call from background threads.</summary>
        public static Stream Connect(ulong steamId)
        {
            if (!SteamManager.Initialized)
                throw new IOException("Steam isn't running, so Mod Sync can't reach a host you joined through Steam.");
            P2PStream stream;
            lock (Lock)
            {
                StreamKey key;
                do key = new StreamKey(steamId, random.Next());
                while (streams.ContainsKey(key));
                stream = new P2PStream(key);
                streams[key] = stream;
            }
            EnsurePump();
            return stream;
        }

        // ---------- Receiving ----------

        private static void EnsurePump()
        {
            lock (Lock)
            {
                if (pump != null)
                    return;
                pump = new Thread(Pump) { IsBackground = true, Name = "ModSync Steam" };
                pump.Start();
            }
        }

        /// <summary>Reads our channel until there's nothing left to do, so no Steam calls are made once we're idle (or Steam shuts down).</summary>
        private static void Pump()
        {
            var buffer = new byte[MaxChunk + HeaderSize];
            while (true)
            {
                lock (Lock)
                {
                    if (!SteamManager.Initialized || (!serving && streams.Count == 0))
                    {
                        pump = null;
                        return;
                    }
                }
                try
                {
                    bool received = false;
                    while (SteamNetworking.IsP2PPacketAvailable(out uint size, Channel))
                    {
                        if (size > buffer.Length)
                            buffer = new byte[size];
                        if (!SteamNetworking.ReadP2PPacket(buffer, (uint)buffer.Length, out uint length, out CSteamID remote, Channel))
                            break;
                        received = true;
                        Receive(remote.m_SteamID, buffer, (int)length);
                    }
                    if (!received)
                        Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    Log.Warning("[ModSync] Steam receive failed: " + ex.Message);
                    Thread.Sleep(500);
                }
            }
        }

        private static void Receive(ulong peer, byte[] data, int length)
        {
            if (length < HeaderSize)
                return;
            byte kind = data[0];
            var key = new StreamKey(peer, BitConverter.ToInt32(data, 1));
            bool opened = false;
            P2PStream stream;
            lock (Lock)
            {
                if (!streams.TryGetValue(key, out stream))
                {
                    // Only a host takes new streams, and only while it serves. Steam only delivers packets from
                    // players whose session was accepted, which Multiplayer does when they join.
                    if (kind != KindData || !serving || !SyncServer.Running)
                        return;
                    stream = new P2PStream(key);
                    streams[key] = stream;
                    opened = true;
                }
            }
            if (kind == KindClose)
                stream.EndOfData();
            else
                stream.Append(data, HeaderSize, length - HeaderSize);
            if (opened)
                ThreadPool.QueueUserWorkItem(_ => SyncServer.Serve(stream, AddressPrefix + peer));
        }

        // ---------- Sending ----------

        private static void Send(StreamKey key, byte kind, byte[] data, int offset, int count)
        {
            var packet = new byte[HeaderSize + count];
            packet[0] = kind;
            BitConverter.GetBytes(key.Id).CopyTo(packet, 1);
            Buffer.BlockCopy(data, offset, packet, HeaderSize, count);

            var target = new CSteamID(key.Peer);
            // Steam queues reliable packets without limit; wait for it to catch up so a large mod isn't held in memory.
            var deadline = DateTime.UtcNow.AddMilliseconds(ReadTimeoutMs);
            while (SteamNetworking.GetP2PSessionState(target, out P2PSessionState_t state) && state.m_nBytesQueuedForSend > MaxQueued)
            {
                if (DateTime.UtcNow > deadline)
                    throw new IOException("Steam stopped sending to the other player.");
                Thread.Sleep(10);
            }
            if (!SteamNetworking.SendP2PPacket(target, packet, (uint)packet.Length, EP2PSend.k_EP2PSendReliable, Channel))
                throw new IOException("Steam couldn't send to the other player.");
        }

        private static void Remove(StreamKey key)
        {
            lock (Lock)
                streams.Remove(key);
        }

        private struct StreamKey : IEquatable<StreamKey>
        {
            public readonly ulong Peer;
            public readonly int Id;

            public StreamKey(ulong peer, int id)
            {
                Peer = peer;
                Id = id;
            }

            public bool Equals(StreamKey other) => Peer == other.Peer && Id == other.Id;
            public override bool Equals(object obj) => obj is StreamKey other && Equals(other);
            public override int GetHashCode() => Peer.GetHashCode() * 31 + Id;
        }

        /// <summary>One request's data in each direction. Reads block until data arrives, the other side closes, or the timeout.</summary>
        private sealed class P2PStream : Stream
        {
            private static readonly byte[] Empty = new byte[0];

            private readonly StreamKey key;
            private readonly Queue<byte[]> chunks = new Queue<byte[]>();
            private byte[] current;
            private int position;
            private bool ended;
            private bool disposed;

            public P2PStream(StreamKey key)
            {
                this.key = key;
            }

            public override bool CanRead => true;
            public override bool CanWrite => true;
            public override bool CanSeek => false;
            public override bool CanTimeout => true;
            public override int ReadTimeout { get; set; } = ReadTimeoutMs;

            public void Append(byte[] data, int offset, int count)
            {
                var chunk = new byte[count];
                Buffer.BlockCopy(data, offset, chunk, 0, count);
                lock (chunks)
                {
                    chunks.Enqueue(chunk);
                    Monitor.PulseAll(chunks);
                }
            }

            public void EndOfData()
            {
                lock (chunks)
                {
                    ended = true;
                    Monitor.PulseAll(chunks);
                }
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (chunks)
                {
                    while (current == null || position == current.Length)
                    {
                        if (chunks.Count > 0)
                        {
                            current = chunks.Dequeue();
                            position = 0;
                        }
                        else if (ended)
                            return 0;
                        else if (!Monitor.Wait(chunks, ReadTimeout))
                            throw new IOException("No answer over Steam. Does the other player have Mod Sync active, and are you still connected?");
                    }
                    int n = Math.Min(count, current.Length - position);
                    Buffer.BlockCopy(current, position, buffer, offset, n);
                    position += n;
                    return n;
                }
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                while (count > 0)
                {
                    int n = Math.Min(count, MaxChunk);
                    Send(key, KindData, buffer, offset, n);
                    offset += n;
                    count -= n;
                }
            }

            public override void Flush()
            {
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !disposed)
                {
                    disposed = true;
                    try
                    {
                        Send(key, KindClose, Empty, 0, 0);
                    }
                    catch (Exception)
                    {
                        // The other side times out instead.
                    }
                    Remove(key);
                }
                base.Dispose(disposing);
            }

            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
