using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using Steamworks;
using RimWorld;
using Verse;
using Verse.Steam;

namespace ModSync
{
    public enum ItemAction
    {
        Checking,
        UpToDate,
        Subscribe,
        Download,
        Replace,
        MissingDlc,
    }

    public enum ItemState
    {
        Pending,
        Working,
        Done,
        Failed,
        Skipped,
    }

    public class PlanItem
    {
        public ManifestMod Mod;
        public ItemAction Action;
        public ItemState State;
        public string ExistingDir; // client's current copy, for Replace / hash checks
        public string Detail;
        public float Progress;
    }

    public enum JobPhase
    {
        Connecting,
        UpdatingSelf,   // host has a newer Mod Sync; waiting for Steam to install it
        SelfUpdated,    // newer Mod Sync is on disk; restart to load it
        Planning,
        Ready,
        Syncing,
        WaitingForSteam,
        Finished,
        Failed,
    }

    /// <summary>
    /// The client side of a sync. Network and disk work runs on a background thread;
    /// anything touching game state is queued to the main thread and drained by Tick().
    /// </summary>
    public class SyncJob
    {
        private readonly SyncClient client;
        private readonly bool directWorkshop;
        private readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        private int steamPollTick;

        public JobPhase Phase { get; private set; } = JobPhase.Connecting;
        public string Message { get; private set; } = "Connecting...";
        public Manifest Manifest { get; private set; }
        public List<PlanItem> Items { get; } = new List<PlanItem>();
        public List<string> Warnings { get; } = new List<string>();

        public bool HasChanges => Items.Any(i => i.Action == ItemAction.Subscribe || IsFromHost(i));

        /// <summary>
        /// Mods copied straight from the host can't be verified the way Workshop items can, so they're
        /// only installed when the player confirms they know the host. Asked fresh for every sync.
        /// </summary>
        public bool TrustHost;

        public int FromHostCount => Items.Count(IsFromHost);

        public static bool IsFromHost(PlanItem item) => item.Action == ItemAction.Download || item.Action == ItemAction.Replace;

        /// <summary>Workshop item to offer a link to when Mod Sync has to be updated by hand.</summary>
        public ulong UpdateWorkshopId { get; private set; }
        public float SelfUpdateProgress { get; private set; }
        private string targetModVersion;

        private static string WorkDir => Path.Combine(GenFilePaths.SaveDataFolderPath, "ModSync");

        /// <summary>Multiplayer server to join after the restart, as a Multiplayer connection string. Null outside Multiplayer.</summary>
        public string Rejoin;

        /// <summary>host:port, of the port that answered once connected.</summary>
        public string Address => client.Address;

        public SyncJob(string address, bool directWorkshop) : this(new SyncClient(address), directWorkshop)
        {
        }

        public SyncJob(SyncClient client, bool directWorkshop)
        {
            this.client = client;
            this.directWorkshop = directWorkshop || !SteamManager.Initialized;
            Background(() =>
            {
                var info = client.Hello();
                mainThread.Enqueue(() => CheckHostVersion(info));
            });
        }

        /// <summary>Call every frame from the UI.</summary>
        public void Tick()
        {
            while (mainThread.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Fail(ex);
                }
            }
            if (Phase == JobPhase.WaitingForSteam && ++steamPollTick % 30 == 0)
                PollSteam();
            if (Phase == JobPhase.UpdatingSelf && ++steamPollTick % 30 == 0)
                PollSelfUpdate();
        }

        // ---------- Mod Sync version check (main thread) ----------

        private void CheckHostVersion(HostInfo host)
        {
            var own = ModSyncMod.OwnInfo();
            int? cmp = ModSyncMod.CompareVersions(host.ModVersion, own.ModVersion);
            bool hostNewer = cmp > 0 || (cmp == null && host.ProtocolVersion > Protocol.Version);

            if (hostNewer)
            {
                StartSelfUpdate(host, own);
                return;
            }
            if (host.ProtocolVersion != Protocol.Version)
            {
                Fail($"The host has an older Mod Sync (v{host.ModVersion}) that can't talk to yours (v{own.ModVersion}). The host needs to update Mod Sync and restart.");
                return;
            }
            if (cmp < 0)
                Warnings.Add($"The host has an older Mod Sync (v{host.ModVersion}) than you (v{own.ModVersion}).");

            Background(() =>
            {
                var manifest = client.FetchManifest();
                mainThread.Enqueue(() => BuildPlan(manifest));
            });
        }

        private void StartSelfUpdate(HostInfo host, HostInfo own)
        {
            var ownMeta = ModSyncMod.Instance.Content.ModMetaData;
            var versions = $"The host has Mod Sync v{host.ModVersion}, you have v{own.ModVersion}.";
            UpdateWorkshopId = host.WorkshopId != 0 ? host.WorkshopId : own.WorkshopId;

            if (UpdateWorkshopId == 0)
                Fail(versions + " Neither copy is linked to a Workshop item, so update Mod Sync manually to match the host.");
            else if (!SteamManager.Initialized)
                Fail(versions + " Steam isn't running, so it can't update automatically. Update Mod Sync from its Workshop page.");
            else if (ownMeta == null || ownMeta.Source != ContentSource.SteamWorkshop || own.WorkshopId != UpdateWorkshopId)
                Fail(versions + " Your copy isn't the Workshop version, so Steam can't update it. Update your copy, or remove it and subscribe on the Workshop.");
            else
            {
                targetModVersion = host.ModVersion;
                SteamUGC.DownloadItem(new PublishedFileId_t(UpdateWorkshopId), true);
                Phase = JobPhase.UpdatingSelf;
                Message = versions + " Updating from the Steam Workshop...";
                PollSelfUpdate();
            }
        }

        private void PollSelfUpdate()
        {
            var id = new PublishedFileId_t(UpdateWorkshopId);
            var state = (EItemState)SteamUGC.GetItemState(id);
            bool busy = (state & (EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending)) != 0;
            if (SteamUGC.GetItemDownloadInfo(id, out ulong done, out ulong total) && total > 0)
                SelfUpdateProgress = (float)done / total;

            // Trust the version on disk rather than the item state: it only changes once Steam has actually swapped the files.
            string installed = null;
            if (SteamUGC.GetItemInstallInfo(id, out _, out string folder, 1024, out _))
                installed = ReadModVersion(folder);

            if (ModSyncMod.CompareVersions(installed, targetModVersion) >= 0)
            {
                Phase = JobPhase.SelfUpdated;
                SelfUpdateProgress = 1f;
                Message = $"Mod Sync updated to v{installed}. Restart to load it; the sync continues automatically afterwards.";
            }
            else if (busy)
                Message = $"Steam is downloading Mod Sync v{targetModVersion}...";
            else
                Message = $"Waiting for Steam to install Mod Sync v{targetModVersion} (installed: v{installed ?? "?"}). " +
                          "If this doesn't finish, exit the game so Steam can apply the update, then start it again. " +
                          "If the host runs a version that isn't on the Workshop yet, this can't finish.";
        }

        private static string ReadModVersion(string modFolder)
        {
            try
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(Path.Combine(Path.Combine(modFolder, "About"), "About.xml"));
                return doc.SelectSingleNode("/ModMetaData/modVersion")?.InnerText;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ---------- Planning (main thread) ----------

        private void BuildPlan(Manifest manifest)
        {
            Manifest = manifest;
            Phase = JobPhase.Planning;
            Message = "Comparing mods...";

            if (manifest.GameVersion != VersionControl.CurrentVersionString)
                Warnings.Add($"Host runs RimWorld {manifest.GameVersion}, you run {VersionControl.CurrentVersionString}.");
            if (directWorkshop && !SteamManager.Initialized)
                Warnings.Add("Steam is not running, so Workshop mods will be downloaded from the host instead.");

            var ownPackageId = ModSyncMod.Instance.Content.PackageId;
            var installed = ModLister.AllInstalledMods.ToList();
            var toHash = new List<PlanItem>();

            foreach (var mod in manifest.Mods)
            {
                var item = new PlanItem { Mod = mod, Action = ItemAction.UpToDate };
                Items.Add(item);

                if (mod.PackageIdNonUnique == ownPackageId)
                    continue; // never overwrite ourselves while running

                if (mod.Kind == ModKind.Official)
                {
                    if (ModLister.GetModWithIdentifier(mod.PackageIdNonUnique, ignorePostfix: true) == null)
                    {
                        item.Action = ItemAction.MissingDlc;
                        Warnings.Add($"You don't own {mod.Name}. It can't be synced, so it will be left out.");
                    }
                    continue;
                }

                if (mod.Kind == ModKind.Workshop && !directWorkshop)
                {
                    bool have = installed.Any(m => m.Source == ContentSource.SteamWorkshop && m.GetPublishedFileId().m_PublishedFileId == mod.WorkshopId);
                    if (!have)
                        item.Action = ItemAction.Subscribe;
                    continue;
                }

                // Transferred directly: compare against our local copy, if any.
                var existing = installed.FirstOrDefault(m => m.Source == ContentSource.ModsFolder && m.FolderName == mod.FolderName)
                    ?? installed.FirstOrDefault(m => m.Source == ContentSource.ModsFolder && m.PackageIdNonUnique == mod.PackageIdNonUnique);
                if (existing == null)
                {
                    item.Action = ItemAction.Download;
                }
                else
                {
                    item.Action = ItemAction.Checking;
                    item.ExistingDir = existing.RootDir.FullName;
                    toHash.Add(item);
                }
            }

            Background(() =>
            {
                foreach (var item in toHash)
                {
                    var hash = ModFolder.Identify(item.Mod.Kind, item.ExistingDir, out _);
                    bool sameFolder = Path.GetFileName(item.ExistingDir) == item.Mod.FolderName;
                    var action = hash == item.Mod.Hash && sameFolder ? ItemAction.UpToDate : ItemAction.Replace;
                    mainThread.Enqueue(() => item.Action = action);
                }
                mainThread.Enqueue(() =>
                {
                    Phase = JobPhase.Ready;
                    Message = HasChanges ? "Ready to sync." : "All mods already match the host. Syncing will apply the mod list and settings.";
                });
            });
        }

        // ---------- Syncing ----------

        /// <summary>Main thread.</summary>
        public void Start()
        {
            if (Phase != JobPhase.Ready)
                return;
            Phase = JobPhase.Syncing;
            Message = "Syncing...";

            foreach (var item in Items.Where(i => i.Action == ItemAction.Subscribe))
            {
                SteamUGC.SubscribeItem(new PublishedFileId_t(item.Mod.WorkshopId));
                item.State = ItemState.Working;
                item.Detail = "Subscribed, waiting for Steam";
            }

            var transfers = Items.Where(IsFromHost).ToList();
            if (!TrustHost)
            {
                foreach (var item in transfers)
                {
                    item.State = ItemState.Skipped;
                    item.Detail = "Skipped: host not trusted";
                    if (item.Action == ItemAction.Replace && Path.GetFileName(item.ExistingDir) == item.Mod.FolderName)
                        Warnings.Add($"Kept your own copy of {item.Mod.Name}, which differs from the host's.");
                }
                transfers.Clear();
            }
            var modsDir = GenFilePaths.ModsFolderPath;
            var configDir = GenFilePaths.ConfigFolderPath;
            var backupDir = Path.Combine(WorkDir, "Backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var downloadDir = Path.Combine(WorkDir, "Downloads");

            Background(() =>
            {
                foreach (var item in transfers)
                    Transfer(item, modsDir, downloadDir, backupDir);
                WriteConfigs(configDir, backupDir);
                mainThread.Enqueue(() =>
                {
                    Phase = JobPhase.WaitingForSteam;
                    PollSteam();
                });
            });
        }

        private void Transfer(PlanItem item, string modsDir, string downloadDir, string backupDir)
        {
            var mod = item.Mod;
            var temp = Path.Combine(downloadDir, mod.FolderName);
            try
            {
                item.State = ItemState.Working;
                item.Detail = "Downloading";
                if (Directory.Exists(temp))
                    Directory.Delete(temp, true);
                Directory.CreateDirectory(temp);

                long received = 0;
                client.DownloadMod(mod.FolderName, temp, n =>
                {
                    received += n;
                    item.Progress = mod.Size > 0 ? (float)received / mod.Size : 1f;
                });

                var target = Path.Combine(modsDir, mod.FolderName);
                if (item.ExistingDir != null && Directory.Exists(item.ExistingDir))
                    MoveDirectory(item.ExistingDir, Path.Combine(backupDir, Path.GetFileName(item.ExistingDir)));
                if (Directory.Exists(target))
                    MoveDirectory(target, Path.Combine(backupDir, mod.FolderName));
                MoveDirectory(temp, target);

                item.State = ItemState.Done;
                item.Detail = "Installed";
                item.Progress = 1f;
            }
            catch (Exception ex)
            {
                Log.Error($"[ModSync] Failed to download {mod.Name}: {ex}");
                item.State = ItemState.Failed;
                item.Detail = ex.Message;
            }
        }

        private void WriteConfigs(string configDir, string backupDir)
        {
            foreach (var cfg in Manifest.Configs)
            {
                var name = Path.GetFileName(cfg.FileName); // strip any path the host may have sent
                if (!name.StartsWith("Mod_", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    continue;
                var path = Path.Combine(configDir, name);
                if (File.Exists(path))
                {
                    Directory.CreateDirectory(Path.Combine(backupDir, "Config"));
                    File.Copy(path, Path.Combine(backupDir, "Config", name), true);
                }
                File.WriteAllBytes(path, cfg.Data);
            }
        }

        /// <summary>Main thread: checks Steam download state, and finishes once everything is installed.</summary>
        private void PollSteam()
        {
            var waiting = 0;
            foreach (var item in Items.Where(i => i.Action == ItemAction.Subscribe && i.State == ItemState.Working))
            {
                var id = new PublishedFileId_t(item.Mod.WorkshopId);
                var state = (EItemState)SteamUGC.GetItemState(id);
                bool busy = (state & (EItemState.k_EItemStateDownloading | EItemState.k_EItemStateDownloadPending | EItemState.k_EItemStateNeedsUpdate)) != 0;
                if ((state & EItemState.k_EItemStateInstalled) != 0 && !busy)
                {
                    item.State = ItemState.Done;
                    item.Detail = "Installed";
                    item.Progress = 1f;
                    continue;
                }
                waiting++;
                if (SteamUGC.GetItemDownloadInfo(id, out ulong done, out ulong total) && total > 0)
                {
                    item.Progress = (float)done / total;
                    item.Detail = "Steam downloading";
                }
            }

            if (waiting > 0)
            {
                Message = $"Waiting for Steam to download {waiting} Workshop mod(s)...";
                return;
            }
            Finish();
        }

        /// <summary>Main thread: give up on remaining Steam downloads and apply what we have.</summary>
        public void SkipSteamWait()
        {
            if (Phase == JobPhase.WaitingForSteam)
                Finish();
        }

        private void Finish()
        {
            ModLister.RebuildModList();
            var installed = ModLister.AllInstalledMods.ToList();
            var ids = new List<string>();

            foreach (var item in Items)
            {
                var mod = item.Mod;
                ModMetaData match;
                if (mod.Kind == ModKind.Official || mod.PackageIdNonUnique == ModSyncMod.Instance.Content.PackageId)
                    match = ModLister.GetModWithIdentifier(mod.PackageIdNonUnique, ignorePostfix: true);
                else if (mod.Kind == ModKind.Workshop && !directWorkshop)
                    match = installed.FirstOrDefault(m => m.Source == ContentSource.SteamWorkshop && m.GetPublishedFileId().m_PublishedFileId == mod.WorkshopId);
                else
                    match = installed.FirstOrDefault(m => m.Source == ContentSource.ModsFolder && m.FolderName == mod.FolderName);

                if (match == null)
                {
                    if (item.Action != ItemAction.MissingDlc)
                        Warnings.Add($"{mod.Name} is not installed, so it was left out of the mod list.");
                    continue;
                }
                ids.Add(match.PackageId);
            }

            ModsConfig.SaveFromList(ids);
            Phase = JobPhase.Finished;
            Message = Items.Any(i => i.State == ItemState.Failed)
                ? "Finished with errors. Restart to load the mods that did sync."
                : "Sync complete. Restart to load the host's mods.";
        }

        // ---------- Helpers ----------

        private void Background(Action work)
        {
            new Thread(() =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    mainThread.Enqueue(() => Fail(ex));
                }
            }) { IsBackground = true, Name = "ModSync client" }.Start();
        }

        private void Fail(string message)
        {
            Phase = JobPhase.Failed;
            Message = message;
        }

        private void Fail(Exception ex)
        {
            Log.Warning("[ModSync] " + ex);
            Phase = JobPhase.Failed;
            Message = ex is IOException || ex is SocketException || ex is FormatException
                ? ex.Message
                : "Error: " + ex.Message;
        }

        /// <summary>Directory.Move, falling back to copy + delete when source and target are on different drives.</summary>
        private static void MoveDirectory(string source, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            if (Directory.Exists(target))
                target += "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                Directory.Move(source, target);
            }
            catch (IOException)
            {
                CopyDirectory(source, target);
                Directory.Delete(source, true);
            }
        }

        private static void CopyDirectory(string source, string target)
        {
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
