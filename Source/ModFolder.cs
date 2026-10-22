using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ModSync
{
    /// <summary>Hashing, packing and unpacking of mod folders.</summary>
    public static class ModFolder
    {
        // Development clutter that never needs to reach other players.
        private static readonly HashSet<string> IgnoredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", ".vscode",
        };

        /// <summary>Files under a mod folder, as (relative path with '/' separators, full path), sorted for stable hashing.</summary>
        public static List<KeyValuePair<string, string>> ListFiles(string root)
        {
            var result = new List<KeyValuePair<string, string>>();
            Collect(new DirectoryInfo(root), "", result);
            result.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            return result;
        }

        private static void Collect(DirectoryInfo dir, string prefix, List<KeyValuePair<string, string>> result)
        {
            foreach (var file in dir.GetFiles())
                result.Add(new KeyValuePair<string, string>(prefix + file.Name, file.FullName));
            foreach (var sub in dir.GetDirectories())
            {
                if (IgnoredDirs.Contains(sub.Name) || (sub.Attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                Collect(sub, prefix + sub.Name + "/", result);
            }
        }

        /// <summary>
        /// Identifies a mod folder's contents. Local mods get a full content hash; Workshop mods are
        /// versioned by Steam, so file names and sizes are enough and avoid reading gigabytes on start.
        /// </summary>
        public static string Identify(ModKind kind, string root, out long size)
        {
            return kind == ModKind.Workshop ? Fingerprint(root, out size) : Hash(root, out size);
        }

        public static string Fingerprint(string root, out long size)
        {
            size = 0;
            using (var sha = SHA256.Create())
            {
                foreach (var file in ListFiles(root))
                {
                    long length = new FileInfo(file.Value).Length;
                    size += length;
                    var bytes = Encoding.UTF8.GetBytes(file.Key + "\n" + length + "\n");
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return "f:" + BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        public static string Hash(string root, out long size)
        {
            size = 0;
            using (var sha = SHA256.Create())
            {
                var buffer = new byte[81920];
                foreach (var file in ListFiles(root))
                {
                    var nameBytes = Encoding.UTF8.GetBytes(file.Key + "\n");
                    sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
                    using (var fs = File.OpenRead(file.Value))
                    {
                        size += fs.Length;
                        int read;
                        while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                            sha.TransformBlock(buffer, 0, read, null, 0);
                    }
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>Stream format (gzipped): repeated [true, relPath, length, bytes], terminated by false.</summary>
        public static void Pack(string root, Stream output)
        {
            using (var gz = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var w = new BinaryWriter(gz, Encoding.UTF8))
            {
                foreach (var file in ListFiles(root))
                {
                    using (var fs = File.OpenRead(file.Value))
                    {
                        w.Write(true);
                        w.Write(file.Key);
                        w.Write(fs.Length);
                        fs.CopyTo(gz);
                    }
                }
                w.Write(false);
            }
        }

        /// <summary>Extracts a packed stream into targetDir. Rejects any path that escapes targetDir.</summary>
        public static void Unpack(Stream input, string targetDir, Action<long> onBytes)
        {
            var rootFull = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var buffer = new byte[81920];
            using (var gz = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true))
            using (var r = new BinaryReader(gz, Encoding.UTF8))
            {
                while (r.ReadBoolean())
                {
                    var rel = r.ReadString();
                    long remaining = r.ReadInt64();
                    if (rel.Length == 0 || Path.IsPathRooted(rel) || rel.Split('/', '\\').Any(p => p == ".." || p == "."))
                        throw new InvalidDataException("Host sent an unsafe file path: " + rel);
                    var dest = Path.GetFullPath(Path.Combine(targetDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!dest.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Host sent an unsafe file path: " + rel);

                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    using (var fs = File.Create(dest))
                    {
                        while (remaining > 0)
                        {
                            int read = gz.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                            if (read <= 0)
                                throw new EndOfStreamException("Connection closed mid-file: " + rel);
                            fs.Write(buffer, 0, read);
                            remaining -= read;
                            onBytes?.Invoke(read);
                        }
                    }
                }
            }
        }
    }
}
