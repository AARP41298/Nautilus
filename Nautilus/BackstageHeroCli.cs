using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Nautilus.x360;
using NautilusFREE;

namespace Nautilus
{
    /// <summary>
    /// Headless commands used by BackstageHero to read packed CON/LIVE files without
    /// modifying them. Runs inside Nautilus.exe so mogg decryption works exactly as in the app.
    ///
    ///   Nautilus.exe -backstagehero list    &lt;con&gt; &lt;out.json&gt;
    ///       Writes [{"id","name","artist","path"}...] for every song in songs.dta.
    ///   Nautilus.exe -backstagehero extract &lt;con&gt; &lt;songid&gt; &lt;out.mogg&gt;
    ///       Writes that song's mogg, decrypted (still has its mogg header).
    ///
    /// Exit code 0 on success, 1 on failure (reason in &lt;out&gt;.error).
    /// </summary>
    internal static class BackstageHeroCli
    {
        public const string Switch = "-backstagehero";

        public static int Run(string[] args)
        {
            // args[0] is the switch
            string outPath = args.Length > 0 ? args[args.Length - 1] : null;
            try
            {
                if (args.Length == 4 && args[1].Equals("list", StringComparison.OrdinalIgnoreCase))
                {
                    return List(args[2], args[3]);
                }
                if (args.Length == 5 && args[1].Equals("extract", StringComparison.OrdinalIgnoreCase))
                {
                    return Extract(args[2], args[3], args[4]);
                }
                return Fail(outPath, "usage: -backstagehero list <con> <out.json> | extract <con> <songid> <out.mogg>");
            }
            catch (Exception ex)
            {
                return Fail(outPath, ex.Message);
            }
        }

        private static int Fail(string outPath, string message)
        {
            if (string.IsNullOrWhiteSpace(outPath)) return 1;
            try
            {
                File.WriteAllText(outPath + ".error", message ?? "unknown error", Encoding.UTF8);
            }
            catch (Exception)
            {
                // nowhere left to report it
            }
            return 1;
        }

        private static STFSPackage Open(string con)
        {
            if (!File.Exists(con)) throw new Exception("file not found: " + con);
            var pkg = new STFSPackage(con);
            if (!pkg.ParseSuccess)
            {
                pkg.CloseIO();
                throw new Exception("not a valid CON/LIVE file");
            }
            return pkg;
        }

        private static List<SongData> ReadSongs(STFSPackage pkg)
        {
            var parser = new DTAParser();
            if (!parser.ExtractDTA(pkg, false) || parser.Songs == null || parser.Songs.Count == 0)
            {
                throw new Exception("no songs.dta in package");
            }
            return parser.Songs;
        }

        private static string MoggPath(SongData song)
        {
            if (!string.IsNullOrWhiteSpace(song.FilePath)) return song.FilePath.Trim().TrimStart('/') + ".mogg";
            return "songs/" + song.InternalName + "/" + song.InternalName + ".mogg";
        }

        private static int List(string con, string outJson)
        {
            var pkg = Open(con);
            try
            {
                var songs = ReadSongs(pkg);
                var sb = new StringBuilder("[");
                var first = true;
                foreach (var s in songs)
                {
                    if (string.IsNullOrWhiteSpace(s.ShortName)) continue;
                    var mogg = MoggPath(s);
                    if (pkg.GetFile(mogg) == null) continue; // no audio, nothing to sync against
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append("{\"id\":").Append(Json(s.ShortName))
                      .Append(",\"name\":").Append(Json(s.Name))
                      .Append(",\"artist\":").Append(Json(s.Artist))
                      .Append(",\"path\":").Append(Json(mogg))
                      .Append('}');
                }
                sb.Append(']');
                File.WriteAllText(outJson, sb.ToString(), new UTF8Encoding(false));
                return 0;
            }
            finally
            {
                pkg.CloseIO();
            }
        }

        private static int Extract(string con, string songId, string outMogg)
        {
            var pkg = Open(con);
            byte[] data;
            try
            {
                var song = ReadSongs(pkg).FirstOrDefault(s => string.Equals(s.ShortName, songId, StringComparison.OrdinalIgnoreCase));
                if (song == null) return Fail(outMogg, "song not found in package: " + songId);
                var entry = pkg.GetFile(MoggPath(song));
                if (entry == null) return Fail(outMogg, "mogg not found: " + MoggPath(song));
                data = entry.Extract();
            }
            finally
            {
                pkg.CloseIO();
            }
            if (data == null || data.Length == 0) return Fail(outMogg, "could not extract mogg");

            var nautilus = new nTools();
            var tools = new NemoTools();
            if (File.Exists(outMogg)) File.Delete(outMogg);

            if (!nautilus.MoggIsEncrypted(data))
            {
                File.WriteAllBytes(outMogg, data);
                return 0;
            }
            if (nautilus.MoggIsObfuscated(data))
            {
                data = nautilus.DeObfM(data);
            }
            bool success;
            if (tools.isV17(data))
            {
                unsafe
                {
                    fixed (byte* ptr = data)
                    {
                        success = TheMethod3.decrypt_mogg(ptr, (uint)data.Length);
                    }
                }
                if (success) File.WriteAllBytes(outMogg, data);
            }
            else
            {
                success = nautilus.DecM(data, true, false, DecryptMode.ToFile, outMogg);
            }
            if (!success || !File.Exists(outMogg))
            {
                if (File.Exists(outMogg)) File.Delete(outMogg);
                return Fail(outMogg, "could not decrypt mogg");
            }
            return 0;
        }

        private static string Json(string value)
        {
            if (value == null) return "null";
            var sb = new StringBuilder("\"");
            foreach (var c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
