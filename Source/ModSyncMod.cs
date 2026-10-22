using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace ModSync
{
    public class ModSyncSettings : ModSettings
    {
        public string lastAddress = "";
        public int port = Protocol.DefaultPort;
        public bool autoHost;
        public bool directWorkshop;
        public bool resumeAfterRestart; // set when we restart to load an updated Mod Sync

        public override void ExposeData()
        {
            Scribe_Values.Look(ref lastAddress, "lastAddress", "");
            Scribe_Values.Look(ref port, "port", Protocol.DefaultPort);
            Scribe_Values.Look(ref autoHost, "autoHost");
            Scribe_Values.Look(ref directWorkshop, "directWorkshop");
            Scribe_Values.Look(ref resumeAfterRestart, "resumeAfterRestart");
        }
    }

    public class ModSyncMod : Mod
    {
        public static ModSyncMod Instance;
        public static ModSyncSettings Settings;

        private string portBuffer;

        public ModSyncMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<ModSyncSettings>();
            new Harmony("fnielson.modsync").PatchAll();

            if (Settings.autoHost)
                LongEventHandler.ExecuteWhenFinished(() => SyncServer.Start(Settings.port));

            if (Settings.resumeAfterRestart)
            {
                Settings.resumeAfterRestart = false;
                WriteSettings();
                LongEventHandler.ExecuteWhenFinished(() => Find.WindowStack.Add(new Dialog_ModSync(autoCheck: true)));
            }
        }

        public static HostInfo OwnInfo()
        {
            var meta = Instance.Content.ModMetaData;
            return new HostInfo
            {
                ProtocolVersion = Protocol.Version,
                ModVersion = meta?.ModVersion ?? "",
                WorkshopId = meta?.GetPublishedFileId().m_PublishedFileId ?? 0,
            };
        }

        /// <summary>Compares dotted version strings. Returns null if either can't be parsed.</summary>
        public static int? CompareVersions(string a, string b)
        {
            if (!System.Version.TryParse((a ?? "").Trim(), out var va) || !System.Version.TryParse((b ?? "").Trim(), out var vb))
                return null;
            return va.CompareTo(vb);
        }

        public override string SettingsCategory() => "Mod Sync";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var list = new Listing_Standard();
            list.Begin(inRect);
            list.TextFieldNumericLabeled("Host port ", ref Settings.port, ref portBuffer, 1, 65535);
            list.CheckboxLabeled("Start hosting automatically when the game launches", ref Settings.autoHost,
                "Useful if you're always the host: friends can sync any time the game is running.");
            list.CheckboxLabeled("Download Workshop mods from the host instead of subscribing", ref Settings.directWorkshop,
                "For players without Steam. Workshop mods are copied as local mods and won't auto-update.");
            list.End();
        }
    }

    /// <summary>Adds a "Mod Sync" button under "Mods" on the main menu.</summary>
    [HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
    public static class MainMenuButtonPatch
    {
        private const string Label = "Mod Sync";

        public static void Prefix(List<ListableOption> optList)
        {
            if (Current.ProgramState != ProgramState.Entry)
                return;
            var modsLabel = "Mods".Translate().ToString();
            int index = optList.FindIndex(o => o.label == modsLabel);
            if (index < 0 || optList.Exists(o => o.label == Label))
                return;
            optList.Insert(index + 1, new ListableOption(Label, () => Find.WindowStack.Add(new Dialog_ModSync(autoCheck: false))));
        }
    }
}
