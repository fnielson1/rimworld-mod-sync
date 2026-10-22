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
        private static TcpListener listener;
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
        public static void Start(int port)
        {
            if (Running)
                return;
            Running = true;
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

                    var l = new TcpListener(IPAddress.Any, port);
                    l.Start();
                    lock (Lock)
                    {
                        if (!Running) // stopped while we were hashing
                        {
                            l.Stop();
                            return;
                        }
                        manifest = m;
                        servableFolders = folders;
                        listener = l;
                    }
                    SetStatus($"Hosting on port {port}: {m.Mods.Count} mods, {m.Configs.Count} settings files");
                    AcceptLoop(l);
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

        public static void Stop()
        {
            Running = false;
            lock (Lock)
            {
                listener?.Stop();
                listener = null;
                manifest = null;
                servableFolders = null;
            }
            SetStatus("Not hosting");
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
