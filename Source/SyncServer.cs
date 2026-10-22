using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using RimWorld;
using Verse;

namespace ModSync
{
    /// <summary>Serves the host's manifest and mod folders. Runs on background threads; only reads from a snapshot taken at start.</summary>
    public static class SyncServer
    {
        private static readonly object Lock = new object();
        private static readonly HashSet<int> ports = new HashSet<int>(); // wanted; opened once the manifest is ready
        private static readonly Dictionary<int, TcpListener> listeners = new Dictionary<int, TcpListener>();
        private static Manifest manifest;
        private static Dictionary<string, string> servableFolders; // folder name -> full path
        private static HostInfo hostInfo;
        private static string status = "Not hosting";
        private static int requestsServed;

        public static bool Running { get; private set; }

        public static string Status
        {
            get { lock (Lock) return status; }
        }

        public static int RequestsServed => requestsServed;

        /// <summary>Must be called on the main thread: snapshots the mod list, then hashes and listens in the background.</summary>
        public static void Start(params int[] wanted)
        {
            if (Running)
            {
                foreach (var port in wanted)
                    Listen(port);
                return;
            }
            Running = true;
            lock (Lock)
            {
                ports.Clear();
                ports.UnionWith(wanted);
            }
            SetStatus("Preparing mod list...");

            var snapshot = Snapshot();
            var gameVersion = VersionControl.CurrentVersionString;
            var configDir = GenFilePaths.ConfigFolderPath;
            var ownFolder = ModSyncMod.Instance.Content.FolderName;
            hostInfo = ModSyncMod.OwnInfo();

            var thread = new Thread(() =>
            {
                try
                {
                    var folders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int i = 0;
                    foreach (var entry in snapshot)
                    {
                        i++;
                        if (entry.Mod.Kind == ModKind.Official || entry.Mod.FolderName == ownFolder)
                            continue;
                        SetStatus($"Hashing mods ({i}/{snapshot.Count}): {entry.Mod.Name}");
                        entry.Mod.Hash = ModFolder.Identify(entry.Mod.Kind, entry.RootDir, out entry.Mod.Size);
                        folders[entry.Mod.FolderName] = entry.RootDir;
                    }

                    var m = new Manifest { GameVersion = gameVersion };
                    m.Mods.AddRange(snapshot.Select(e => e.Mod));
                    m.Configs.AddRange(CollectConfigs(configDir, snapshot.Select(e => e.Mod.FolderName), ownFolder));

                    lock (Lock)
                    {
                        if (!Running) // stopped while we were hashing
                            return;
                        manifest = m;
                        servableFolders = folders;
                        foreach (var port in ports)
                            Open(port);
                        UpdateStatus();
                    }
                }
                catch (Exception ex)
                {
                    if (Running)
                    {
                        Log.Error("[ModSync] Server error: " + ex);
                        SetStatus("Error: " + ex.Message);
                        Running = false;
                    }
                }
            }) { IsBackground = true, Name = "ModSync server" };
            thread.Start();
        }

        /// <summary>Also accept clients on this port. Returns false if it was already wanted. Doesn't start the server.</summary>
        public static bool Listen(int port)
        {
            lock (Lock)
            {
                if (!Running || !ports.Add(port))
                    return false;
                if (manifest != null)
                {
                    Open(port);
                    UpdateStatus();
                }
                return true;
            }
        }

        /// <summary>Stops accepting clients on this port, leaving any other ports open.</summary>
        public static void Unlisten(int port)
        {
            lock (Lock)
            {
                ports.Remove(port);
                if (listeners.TryGetValue(port, out var l))
                {
                    l.Stop();
                    listeners.Remove(port);
                }
                if (manifest != null)
                    UpdateStatus();
            }
        }

        public static void Stop()
        {
            Running = false;
            lock (Lock)
            {
                foreach (var l in listeners.Values)
                    l.Stop();
                listeners.Clear();
                ports.Clear();
                manifest = null;
                servableFolders = null;
            }
            SetStatus("Not hosting");
        }

        /// <summary>Under Lock. A port that can't be opened is logged and skipped so the other ports still work.</summary>
        private static void Open(int port)
        {
            if (listeners.ContainsKey(port))
                return;
            try
            {
                var l = new TcpListener(IPAddress.Any, port);
                l.Start();
                listeners[port] = l;
                new Thread(() => AcceptLoop(l)) { IsBackground = true, Name = "ModSync server " + port }.Start();
            }
            catch (SocketException ex)
            {
                Log.Warning($"[ModSync] Can't listen on TCP port {port}: {ex.Message}");
            }
        }

        /// <summary>Under Lock, once the manifest is ready.</summary>
        private static void UpdateStatus()
        {
            var open = listeners.Keys.OrderBy(p => p).ToList();
            var failed = ports.Where(p => !listeners.ContainsKey(p)).OrderBy(p => p).ToList();
            status = open.Count == 0
                ? (failed.Count > 0 ? $"Error: can't open TCP port {string.Join(", ", failed)}. Is something else using it?" : "Not accepting connections")
                : $"Hosting on port {string.Join(", ", open)}: {manifest.Mods.Count} mods, {manifest.Configs.Count} settings files";
            if (open.Count > 0 && failed.Count > 0)
                status += $" (port {string.Join(", ", failed)} unavailable)";
        }

        private class SnapshotEntry
        {
            public ManifestMod Mod;
            public string RootDir;
        }

        private static List<SnapshotEntry> Snapshot()
        {
            var list = new List<SnapshotEntry>();
            foreach (var mod in ModsConfig.ActiveModsInLoadOrder)
            {
                ModKind kind;
                ulong workshopId = 0;
                if (mod.Official)
                    kind = ModKind.Official;
                else if (mod.Source == ContentSource.SteamWorkshop)
                {
                    kind = ModKind.Workshop;
                    workshopId = mod.GetPublishedFileId().m_PublishedFileId;
                }
                else
                    kind = ModKind.Local;

                list.Add(new SnapshotEntry
                {
                    RootDir = mod.RootDir.FullName,
                    Mod = new ManifestMod
                    {
                        PackageId = mod.PackageId,
                        PackageIdNonUnique = mod.PackageIdNonUnique,
                        Name = mod.Name,
                        Kind = kind,
                        WorkshopId = workshopId,
                        FolderName = mod.FolderName,
                    },
                });
            }
            return list;
        }

        /// <summary>Mod settings files (Config/Mod_{folder}_{class}.xml) belonging to active mods, excluding our own.</summary>
        private static IEnumerable<ConfigFile> CollectConfigs(string configDir, IEnumerable<string> activeFolders, string ownFolder)
        {
            var prefixes = activeFolders.Where(f => f != ownFolder)
                .Select(f => GenText.SanitizeFilename("Mod_" + f + "_"))
                .ToList();
            foreach (var path in Directory.GetFiles(configDir, "Mod_*.xml"))
            {
                var name = Path.GetFileName(path);
                if (prefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    yield return new ConfigFile { FileName = name, Data = File.ReadAllBytes(path) };
            }
        }

        private static void AcceptLoop(TcpListener l)
        {
            while (Running)
            {
                TcpClient client;
                try
                {
                    client = l.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    return; // listener stopped
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
        }

        private static void Handle(TcpClient client)
        {
            var remote = client.Client.RemoteEndPoint?.ToString();
            using (client)
            using (var stream = client.GetStream())
            using (var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true))
            using (var w = new BinaryWriter(new BufferedStream(stream, 65536), Encoding.UTF8))
            {
                try
                {
                    client.ReceiveTimeout = 30000;
                    if (r.ReadUInt32() != Protocol.Magic)
                        return;
                    int version = r.ReadInt32();
                    byte cmd = r.ReadByte();
                    if (cmd == Protocol.CmdHello)
                    {
                        // Answered whatever the client's protocol version, so it can find out it's outdated.
                        w.Write(Protocol.StatusOk);
                        w.Write(hostInfo.ProtocolVersion);
                        w.Write(hostInfo.ModVersion);
                        w.Write(hostInfo.WorkshopId);
                        w.Flush();
                        return;
                    }
                    if (version != Protocol.Version)
                    {
                        Error(w, $"Version mismatch: host uses Mod Sync protocol {Protocol.Version}, you use {version}. Update Mod Sync to match the host.");
                        return;
                    }

                    Manifest m;
                    Dictionary<string, string> folders;
                    lock (Lock)
                    {
                        m = manifest;
                        folders = servableFolders;
                    }

                    if (cmd == Protocol.CmdManifest)
                    {
                        w.Write(Protocol.StatusOk);
                        m.Write(w);
                    }
                    else if (cmd == Protocol.CmdMod)
                    {
                        var folder = r.ReadString();
                        if (!folders.TryGetValue(folder, out var path))
                        {
                            Error(w, "Host does not serve mod folder " + folder);
                            return;
                        }
                        Log.Message($"[ModSync] Sending {folder} to {remote}");
                        w.Write(Protocol.StatusOk);
                        w.Flush();
                        ModFolder.Pack(path, w.BaseStream);
                    }
                    else
                    {
                        Error(w, "Unknown command " + cmd);
                        return;
                    }
                    w.Flush();
                    Interlocked.Increment(ref requestsServed);
                }
                catch (Exception ex)
                {
                    Log.Warning($"[ModSync] Request from {remote} failed: {ex.Message}");
                }
            }
        }

        private static void Error(BinaryWriter w, string message)
        {
            w.Write(Protocol.StatusError);
            w.Write(message);
            w.Flush();
        }

        private static void SetStatus(string s)
        {
            lock (Lock) status = s;
        }
    }
}
