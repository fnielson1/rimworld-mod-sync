using System.Collections.Generic;
using System.IO;

namespace ModSync
{
    public enum ModKind : byte
    {
        Official = 0,
        Workshop = 1,
        Local = 2,
    }

    /// <summary>One active mod on the host, in load order.</summary>
    public class ManifestMod
    {
        public string PackageId;     // as it appears in the host's active list (may carry the _steam postfix)
        public string PackageIdNonUnique;
        public string Name;
        public ModKind Kind;
        public ulong WorkshopId;     // 0 unless Kind == Workshop
        public string FolderName;    // used for mod settings filenames, so the client installs under the same name
        public string Hash;          // content hash, only computed for transferable mods
        public long Size;

        public void Write(BinaryWriter w)
        {
            w.Write(PackageId);
            w.Write(PackageIdNonUnique);
            w.Write(Name ?? "");
            w.Write((byte)Kind);
            w.Write(WorkshopId);
            w.Write(FolderName);
            w.Write(Hash ?? "");
            w.Write(Size);
        }

        public static ManifestMod Read(BinaryReader r)
        {
            return new ManifestMod
            {
                PackageId = r.ReadString(),
                PackageIdNonUnique = r.ReadString(),
                Name = r.ReadString(),
                Kind = (ModKind)r.ReadByte(),
                WorkshopId = r.ReadUInt64(),
                FolderName = r.ReadString(),
                Hash = r.ReadString(),
                Size = r.ReadInt64(),
            };
        }
    }

    public class ConfigFile
    {
        public string FileName;
        public byte[] Data;
    }

    public class Manifest
    {
        public string GameVersion;
        public List<ManifestMod> Mods = new List<ManifestMod>();
        public List<ConfigFile> Configs = new List<ConfigFile>();

        public void Write(BinaryWriter w)
        {
            w.Write(GameVersion);
            w.Write(Mods.Count);
            foreach (var mod in Mods)
                mod.Write(w);
            w.Write(Configs.Count);
            foreach (var cfg in Configs)
            {
                w.Write(cfg.FileName);
                w.Write(cfg.Data.Length);
                w.Write(cfg.Data);
            }
        }

        public static Manifest Read(BinaryReader r)
        {
            var m = new Manifest { GameVersion = r.ReadString() };
            int modCount = r.ReadInt32();
            for (int i = 0; i < modCount; i++)
                m.Mods.Add(ManifestMod.Read(r));
            int cfgCount = r.ReadInt32();
            for (int i = 0; i < cfgCount; i++)
            {
                var name = r.ReadString();
                var data = r.ReadBytes(r.ReadInt32());
                m.Configs.Add(new ConfigFile { FileName = name, Data = data });
            }
            return m;
        }
    }

    /// <summary>What the host tells a client before anything else, so the client can update Mod Sync itself first.</summary>
    public class HostInfo
    {
        public int ProtocolVersion;
        public string ModVersion;
        public ulong WorkshopId; // 0 if the host's copy of Mod Sync isn't linked to a Workshop item
    }

    /// <summary>
    /// Wire protocol: one request per TCP connection.
    /// Request:  magic, version, command byte, [folder name for GetMod]
    /// Response: status byte (0 = ok, else error + message), then the payload.
    ///
    /// CmdHello and its response layout must never change: it's how an outdated client
    /// learns that it needs to update, whatever protocol version the host speaks.
    /// </summary>
    public static class Protocol
    {
        public const uint Magic = 0x534D5752; // "RWMS"
        public const int Version = 1;
        public const int DefaultPort = 28150;

        public const byte CmdHello = 0;
        public const byte CmdManifest = 1;
        public const byte CmdMod = 2;

        public const byte StatusOk = 0;
        public const byte StatusError = 1;
    }
}
