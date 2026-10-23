using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ModSync
{
    /// <summary>
    /// Optional integration with the Multiplayer mod. Its methods are patched by name at runtime,
    /// so Mod Sync needs Multiplayer neither to build nor to run.
    ///
    /// Host: Mod Sync hosts while a Multiplayer server runs, on Multiplayer's direct ports (as TCP) and our own port.
    /// Client: when Multiplayer reports a mod mismatch on join, Mod Sync opens on that server's address,
    /// and the restart rejoins the server.
    /// </summary>
    public static class MultiplayerCompat
    {
        private const string PackageId = "rwmt.Multiplayer";
        private const int MultiplayerDefaultPort = 30502;

        // Multiplayer joins the server named here on startup. Its own "Fix and restart" uses it the same way.
        private const string RejoinEnvVar = "MultiplayerRestartConnect";

        private static Type multiplayerType;

        // Multiplayer's data about the server we're syncing from, for handing its mod settings to the restarted game.
        private static object pendingRemote;

        // What we changed when the Multiplayer server started, so stopping it undoes only that.
        private static bool startedServer;
        private static readonly List<int> addedPorts = new List<int>();

        public static bool Active => multiplayerType != null;

        public static void Init(Harmony harmony)
        {
            if (ModLister.GetActiveModWithIdentifier(PackageId, ignorePostfix: true) == null)
                return;
            multiplayerType = AccessTools.TypeByName("Multiplayer.Client.Multiplayer");
            if (multiplayerType == null)
            {
                Log.Warning("[ModSync] Multiplayer is active but its code wasn't found, so Mod Sync won't integrate with it.");
                return;
            }
            IgnoreOwnSettings();
            Patch(harmony, "Multiplayer.Client.HostUtil:StartLocalServer", postfix: nameof(ServerStarted));
            Patch(harmony, "Multiplayer.Client.Multiplayer:StopMultiplayer", prefix: nameof(StoppingMultiplayer));
            Patch(harmony, "Multiplayer.Client.JoinDataWindow:PostOpen", postfix: nameof(MismatchWindowOpened));
        }

        private static void Patch(Harmony harmony, string target, string prefix = null, string postfix = null)
        {
            var method = AccessTools.Method(target);
            if (method == null)
            {
                Log.Warning($"[ModSync] Couldn't find {target} in Multiplayer; that part of the integration is off. Multiplayer may have changed.");
                return;
            }
            harmony.Patch(method,
                prefix: prefix != null ? new HarmonyMethod(typeof(MultiplayerCompat), prefix) : null,
                postfix: postfix != null ? new HarmonyMethod(typeof(MultiplayerCompat), postfix) : null);
        }

        /// <summary>
        /// Our settings (address, port, ...) belong to each machine, but Multiplayer compares every active mod's
        /// settings between host and client. Left in, they'd never match and every join would be a mismatch.
        /// </summary>
        private static void IgnoreOwnSettings()
        {
            var field = AccessTools.Field("Multiplayer.Client.Util.SyncConfigs:ignoredConfigsModIds");
            if (field == null)
            {
                Log.Warning("[ModSync] Couldn't find Multiplayer's ignored settings list; Mod Sync's settings may cause mod mismatches.");
                return;
            }
            var ignored = (string[])field.GetValue(null);
            var own = ModSyncMod.Instance.Content.ModMetaData.PackageIdNonUnique;
            if (!ignored.Contains(own))
                field.SetValue(null, ignored.Append(own).ToArray());
        }

        /// <summary>
        /// Sets the Multiplayer server for the restarted game to join. Null does nothing.
        /// Also hands over the server's mod settings the way Multiplayer's own "Fix and restart" does: Multiplayer
        /// compares settings Mod Sync doesn't copy (HugsLib's, for one), and uses the server's for the next session.
        /// </summary>
        public static void RejoinAfterRestart(string connection)
        {
            if (connection.NullOrEmpty())
                return;
            Environment.SetEnvironmentVariable(RejoinEnvVar, connection);
            try
            {
                var remote = Traverse.Create(pendingRemote);
                if (pendingRemote != null && remote.Field("hasConfigs").GetValue<bool>())
                {
                    var syncConfigs = AccessTools.TypeByName("Multiplayer.Client.Util.SyncConfigs");
                    Traverse.Create(syncConfigs).Method("SaveConfigs", remote.Field("remoteModConfigs").GetValue()).GetValue();
                    Traverse.Create(syncConfigs).Method("MarkApplicableForChildProcess").GetValue();
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[ModSync] Couldn't hand the server's mod settings to Multiplayer: " + ex);
            }
        }

        // ---------- Host ----------

        private static object LocalServer => Traverse.Create(multiplayerType).Property("LocalServer").GetValue();

        private static void ServerStarted()
        {
            try
            {
                if (!ModSyncMod.Settings.hostWithMultiplayer)
                    return;
                var settings = Traverse.Create(LocalServer).Field("settings");
                var ports = new List<int> { ModSyncMod.Settings.port };
                if (settings.Field("direct").GetValue<bool>())
                    ports.AddRange(DirectPorts(settings.Field("directAddress").GetValue<string>()));
                ports = ports.Distinct().ToList();

                // Snapshotting the mod list has to happen on the main thread, outside the hosting long event.
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    addedPorts.Clear();
                    startedServer = !SyncServer.Running;
                    if (startedServer)
                    {
                        SyncServer.Start(ports.ToArray());
                        addedPorts.AddRange(ports);
                    }
                    else
                        addedPorts.AddRange(ports.Where(SyncServer.Listen));
                });
            }
            catch (Exception ex)
            {
                Log.Error("[ModSync] Couldn't start hosting with Multiplayer: " + ex);
            }
        }

        private static void StoppingMultiplayer()
        {
            if (LocalServer == null || addedPorts.Count == 0)
                return;
            if (startedServer)
                SyncServer.Stop();
            else
                foreach (var port in addedPorts)
                    SyncServer.Unlisten(port);
            addedPorts.Clear();
            startedServer = false;
        }

        /// <summary>Ports from Multiplayer's direct address setting: endpoints joined by '&amp;', each "ip", "ip:port" or "[ipv6]:port".</summary>
        private static IEnumerable<int> DirectPorts(string directAddress)
        {
            foreach (var endpoint in (directAddress ?? "").Split('&'))
            {
                var s = endpoint.Trim();
                int colon = s.LastIndexOf(':');
                bool hasPort = colon > 0 && (s[colon - 1] == ']' || s.IndexOf(':') == colon);
                if (!hasPort)
                    yield return MultiplayerDefaultPort;
                else if (int.TryParse(s.Substring(colon + 1), out int port) && port > 0 && port <= 65535)
                    yield return port;
            }
        }

        // ---------- Client ----------

        private static void MismatchWindowOpened(Window __instance)
        {
            try
            {
                if (!ModSyncMod.Settings.syncOnMultiplayerJoin)
                    return;
                var remote = Traverse.Create(__instance).Field("remote").GetValue();
                var connector = Traverse.Create(remote).Field("connector").GetValue();
                var connection = connector == null ? null : Traverse.Create(connector).Method("GetConnectionString").GetValue<string>();
                if (connection.NullOrEmpty())
                    return;

                if (!TryParseLiteNet(connection, out string host, out int port))
                {
                    Messages.Message("Mod Sync can't sync from servers joined through Steam yet. Use Multiplayer's own fix, " +
                                     "or Mod Sync on the main menu with the host's address.", MessageTypeDefOf.RejectInput, historical: false);
                    return;
                }

                var ports = new[] { port, ModSyncMod.Settings.port }.Distinct().ToArray();
                var job = new SyncJob(new SyncClient(host, ports), ModSyncMod.Settings.directWorkshop) { Rejoin = connection };
                pendingRemote = remote;
                Dialog_ModSync.Open(job);
            }
            catch (Exception ex)
            {
                Log.Error("[ModSync] Couldn't start syncing with the Multiplayer server: " + ex);
            }
        }

        /// <summary>Multiplayer's direct and LAN connection string: "lnl:host:port", port optional.</summary>
        private static bool TryParseLiteNet(string connection, out string host, out int port)
        {
            host = null;
            port = MultiplayerDefaultPort;
            const string prefix = "lnl:";
            if (!connection.StartsWith(prefix))
                return false;
            var rest = connection.Substring(prefix.Length);
            int colon = rest.LastIndexOf(':');
            if (colon > 0 && int.TryParse(rest.Substring(colon + 1), out int parsed))
            {
                host = rest.Substring(0, colon);
                port = parsed;
            }
            else
                host = rest;
            host = host.Trim('[', ']');
            if (host.Length == 0)
                host = "127.0.0.1";
            return true;
        }
    }
}
