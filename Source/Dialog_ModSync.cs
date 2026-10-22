using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using UnityEngine;
using RimWorld;
using Verse;

namespace ModSync
{
    public class Dialog_ModSync : Window
    {
        // Kept across openings so closing the dialog mid-sync doesn't lose progress.
        private static SyncJob job;

        private readonly string localAddresses;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(720f, 640f);

        public Dialog_ModSync(bool autoCheck)
        {
            doCloseX = true;
            doCloseButton = false;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            localAddresses = GetLocalAddresses();
            if (autoCheck && !ModSyncMod.Settings.lastAddress.NullOrEmpty())
                Connect(ModSyncMod.Settings);
        }

        public override void PostClose()
        {
            ModSyncMod.Instance.WriteSettings();
        }

        public override void DoWindowContents(Rect inRect)
        {
            job?.Tick();
            var settings = ModSyncMod.Settings;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 35f), "Mod Sync");
            Text.Font = GameFont.Small;
            float y = inRect.y + 45f;

            // ---- Host ----
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "<b>Host</b> - let other players copy your mods");
            y += 28f;
            var buttonRect = new Rect(inRect.x, y, 160f, 32f);
            if (SyncServer.Running)
            {
                if (Widgets.ButtonText(buttonRect, "Stop hosting"))
                    SyncServer.Stop();
            }
            else if (Widgets.ButtonText(buttonRect, "Start hosting"))
            {
                SyncServer.Start(settings.port);
            }
            Widgets.Label(new Rect(buttonRect.xMax + 10f, y + 6f, inRect.width - buttonRect.width - 10f, 24f), SyncServer.Status);
            y += 36f;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 44f),
                $"Your LAN address: {localAddresses}   Port: {settings.port} (TCP, change in Mod Settings)\n" +
                "Players outside your network need your public IP and the port forwarded, or a VPN such as Tailscale.");
            GUI.color = Color.white;
            y += 52f;
            Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
            y += 10f;

            // ---- Client ----
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "<b>Join</b> - copy the host's mods, load order and mod settings");
            y += 28f;
            bool busy = job != null && (job.Phase == JobPhase.Syncing || job.Phase == JobPhase.WaitingForSteam || job.Phase == JobPhase.UpdatingSelf);
            Widgets.Label(new Rect(inRect.x, y + 5f, 110f, 24f), "Host address:");
            var fieldRect = new Rect(inRect.x + 110f, y, 300f, 32f);
            if (busy)
                Widgets.Label(new Rect(fieldRect.x, y + 5f, fieldRect.width, 24f), settings.lastAddress);
            else
                settings.lastAddress = Widgets.TextField(fieldRect, settings.lastAddress);
            if (!busy && Widgets.ButtonText(new Rect(fieldRect.xMax + 10f, y, 140f, 32f), "Check"))
                Connect(settings);
            y += 40f;

            if (job == null)
            {
                GUI.color = Color.gray;
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 40f), "Enter an address like 192.168.1.20 or 192.168.1.20:28150, then click Check to see what would change.");
                GUI.color = Color.white;
                return;
            }

            DrawJob(new Rect(inRect.x, y, inRect.width, inRect.yMax - y));
        }

        private void Connect(ModSyncSettings settings)
        {
            try
            {
                job = new SyncJob(settings.lastAddress, settings.directWorkshop);
            }
            catch (FormatException ex)
            {
                Messages.Message(ex.Message, MessageTypeDefOf.RejectInput, historical: false);
            }
        }

        private void DrawJob(Rect rect)
        {
            float y = rect.y;
            float messageHeight = Text.CalcHeight(job.Message, rect.width);
            Widgets.Label(new Rect(rect.x, y, rect.width, messageHeight), job.Message);
            y += messageHeight + 2f;
            if (job.Phase == JobPhase.UpdatingSelf)
            {
                Widgets.FillableBar(new Rect(rect.x, y, rect.width, 20f), Mathf.Clamp01(job.SelfUpdateProgress));
                y += 26f;
            }
            foreach (var warning in job.Warnings)
            {
                GUI.color = ColorLibrary.Gold;
                float h = Text.CalcHeight(warning, rect.width);
                Widgets.Label(new Rect(rect.x, y, rect.width, h), warning);
                y += h + 2f;
            }
            GUI.color = Color.white;

            // Action buttons along the bottom.
            var buttonRect = new Rect(rect.x, rect.yMax - 36f, 200f, 36f);
            switch (job.Phase)
            {
                case JobPhase.Ready:
                    string label = !job.HasChanges ? "Apply mod list & settings"
                        : job.FromHostCount > 0 && !job.TrustHost ? $"Sync, skip {job.FromHostCount} host mods"
                        : "Sync";
                    if (Widgets.ButtonText(new Rect(buttonRect.x, buttonRect.y, 260f, buttonRect.height), label))
                        job.Start();
                    break;
                case JobPhase.WaitingForSteam:
                    if (Widgets.ButtonText(buttonRect, "Stop waiting for Steam"))
                        job.SkipSteamWait();
                    break;
                case JobPhase.Finished:
                    if (Widgets.ButtonText(buttonRect, "Restart now"))
                        GenCommandLine.Restart();
                    break;
                case JobPhase.SelfUpdated:
                    if (Widgets.ButtonText(buttonRect, "Restart and continue"))
                    {
                        ResumeAfterRestart();
                        GenCommandLine.Restart();
                    }
                    break;
                case JobPhase.UpdatingSelf:
                    // Steam may not be able to replace a DLL the running game has loaded; quitting lets it finish.
                    if (Widgets.ButtonText(buttonRect, "Exit so Steam can update"))
                    {
                        ResumeAfterRestart();
                        Root.Shutdown();
                    }
                    TooltipHandler.TipRegion(buttonRect, "Quits RimWorld. Wait for Steam to finish updating Mod Sync, then start the game: the sync continues automatically.");
                    break;
                case JobPhase.Failed when job.UpdateWorkshopId != 0:
                    if (Widgets.ButtonText(buttonRect, "Open Workshop page"))
                        SteamUtility.OpenWorkshopPage(new Steamworks.PublishedFileId_t(job.UpdateWorkshopId));
                    break;
            }

            if (job.Items.Count == 0)
                return;

            float listBottom = buttonRect.y;
            if (job.Phase == JobPhase.Ready && job.FromHostCount > 0)
            {
                var explanation = $"{job.FromHostCount} mod(s) would be copied straight from the host. Unlike Steam Workshop mods, " +
                                  "these can't be verified and can contain anything the host puts in them, including harmful code.";
                float h = Text.CalcHeight(explanation, rect.width);
                listBottom -= h + 36f;
                GUI.color = ColorLibrary.Gold;
                Widgets.Label(new Rect(rect.x, listBottom, rect.width, h), explanation);
                GUI.color = Color.white;
                Widgets.CheckboxLabeled(new Rect(rect.x, listBottom + h + 4f, 360f, 26f), "I trust and know the host personally", ref job.TrustHost);
            }

            // Mod list in host load order.
            var outRect = new Rect(rect.x, y + 4f, rect.width, listBottom - y - 12f);
            var visible = job.Items.Where(i => i.Action != ItemAction.UpToDate).ToList();
            int upToDate = job.Items.Count - visible.Count;
            const float rowHeight = 26f;
            var viewRect = new Rect(0f, 0f, outRect.width - 16f, (visible.Count + 1) * rowHeight);
            Widgets.DrawMenuSection(outRect);
            Widgets.BeginScrollView(outRect.ContractedBy(2f), ref scroll, viewRect);
            float rowY = 0f;
            foreach (var item in visible)
            {
                DrawRow(new Rect(4f, rowY, viewRect.width - 8f, rowHeight), item, job.TrustHost);
                rowY += rowHeight;
            }
            GUI.color = Color.gray;
            Widgets.Label(new Rect(4f, rowY + 3f, viewRect.width, rowHeight), $"{upToDate} of {job.Items.Count} mods already match the host. {job.Manifest?.Configs.Count ?? 0} mod settings files will be copied.");
            GUI.color = Color.white;
            Widgets.EndScrollView();
        }

        private static void ResumeAfterRestart()
        {
            ModSyncMod.Settings.resumeAfterRestart = true;
            ModSyncMod.Instance.WriteSettings();
        }

        private static void DrawRow(Rect rect, PlanItem item, bool trustHost)
        {
            var nameRect = new Rect(rect.x, rect.y, rect.width * 0.5f, rect.height);
            var statusRect = new Rect(nameRect.xMax + 6f, rect.y + 3f, rect.width - nameRect.width - 6f, rect.height - 6f);

            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(nameRect, item.Mod.Name.Truncate(nameRect.width));
            if (item.State == ItemState.Working && item.Progress > 0f)
                Widgets.FillableBar(statusRect, Mathf.Clamp01(item.Progress));

            string text;
            switch (item.State)
            {
                case ItemState.Working:
                case ItemState.Done:
                    text = item.Detail;
                    break;
                case ItemState.Failed:
                    GUI.color = ColorLibrary.RedReadable;
                    text = "Failed: " + item.Detail;
                    break;
                case ItemState.Skipped:
                    GUI.color = Color.gray;
                    text = item.Detail;
                    break;
                default:
                    text = ActionLabel(item);
                    if (SyncJob.IsFromHost(item) && !trustHost)
                    {
                        GUI.color = ColorLibrary.Gold;
                        text = "Skipped unless you trust the host";
                    }
                    break;
            }
            Widgets.Label(statusRect, text.Truncate(statusRect.width));
            if (item.State == ItemState.Failed)
                TooltipHandler.TipRegion(rect, item.Detail);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static string ActionLabel(PlanItem item)
        {
            switch (item.Action)
            {
                case ItemAction.Checking: return "Checking...";
                case ItemAction.Subscribe: return "Subscribe on Workshop";
                case ItemAction.Download: return $"Download from host ({FormatSize(item.Mod.Size)})";
                case ItemAction.Replace: return $"Update from host ({FormatSize(item.Mod.Size)})";
                case ItemAction.MissingDlc: return "DLC not owned - skipped";
                default: return "Up to date";
            }
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
            if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0.0} MB";
            return $"{bytes / 1024.0:0} KB";
        }

        private static string GetLocalAddresses()
        {
            try
            {
                var addresses = Dns.GetHostAddresses(Dns.GetHostName())
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                    .Select(a => a.ToString())
                    .ToList();
                return addresses.Count > 0 ? string.Join(", ", addresses) : "unknown";
            }
            catch (Exception)
            {
                return "unknown";
            }
        }
    }
}
