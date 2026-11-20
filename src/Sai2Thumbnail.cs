using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace QuickLook.Plugin.SaiViewer
{
    internal sealed class Sai2Entry
    {
        public string Type;
        public uint Layer;
        public long Offset;
    }

    internal sealed class Sai2Info
    {
        public uint CanvasWidth;
        public uint CanvasHeight;
        public int TableCount;
        public List<Sai2Entry> Entries = new List<Sai2Entry>();

        // 'thum' 块（有损缩略图，jssf）
        public long BlobStart;
        public long BlobEnd;
        public int ThumbWidth;
        public int ThumbHeight;
        public int ThumbChannels;
        public bool HasJssfThumb;
        public bool HasDpcmThumb;
        public string ThumbTag;
        public bool HasThumbnail => HasJssfThumb || HasDpcmThumb;

        // 'intg' 块（1:1 无损预览，dpcm）
        public bool HasIntg;
        public long IntgStart;
        public long IntgEnd;
        public int IntgChannels;
    }

    internal static class Sai2Thumbnail
    {
        public const string Magic = "SAI-CANVAS-TYPE0";
        public const string JssfTag = "jssf";
        public const string DpcmTag = "dpcm";

        // 头部 64 字节：0..15 魔数、16..19 四个 u8 标志、20 宽、24 高、28 打印分辨率、
        // 32 表项数、36 选中层，40..63 其余；表项紧随其后，每项 16 字节。
        public const int HeaderSize = 64;
        public const int EntrySize = 16;

        public static FileStream OpenRead(string path)
        {
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }

        public static bool IsSai2(string path)
        {
            try
            {
                if (!File.Exists(path)) return false;
                using (var fs = OpenRead(path))
                {
                    if (fs.Length < HeaderSize) return false;
                    var head = new byte[Magic.Length];
                    ReadExactly(fs, head, 0, head.Length);
                    return Encoding.ASCII.GetString(head, 0, head.Length) == Magic;
                }
            }
            catch
            {
                return false;
            }
        }

        public static Sai2Info ReadInfo(string path)
        {
            using (var fs = OpenRead(path))
            {
                if (fs.Length < HeaderSize) throw new InvalidDataException("文件小于 64 字节，不是 SAI2 画布");

                var head = new byte[HeaderSize];
                ReadExactly(fs, head, 0, HeaderSize);

                var magic = Encoding.ASCII.GetString(head, 0, 16);
                if (magic != Magic) throw new InvalidDataException("不是 SAI2 画布（文件标识：" + magic.Trim('\0') + "）");

                var info = new Sai2Info
                {
                    CanvasWidth = BitConverter.ToUInt32(head, 20),
                    CanvasHeight = BitConverter.ToUInt32(head, 24),
                    TableCount = (int)BitConverter.ToUInt32(head, 32),
                };

                if (info.CanvasWidth == 0 || info.CanvasHeight == 0)
                    throw new InvalidDataException("画布尺寸无效（0）");
                if (info.TableCount < 1 || info.TableCount > 200000)
                    throw new InvalidDataException("表项数异常：" + info.TableCount);

                var table = new byte[info.TableCount * EntrySize];
                ReadExactly(fs, table, 0, table.Length);

                for (int i = 0; i < info.TableCount; i++)
                {
                    int o = i * EntrySize;
                    info.Entries.Add(new Sai2Entry
                    {
                        Type = Encoding.ASCII.GetString(table, o, 4),
                        Layer = BitConverter.ToUInt32(table, o + 4),
                        Offset = (long)BitConverter.ToUInt64(table, o + 8),
                    });
                }

                ReadThumbnailInfo(fs, info);
                ReadIntgInfo(fs, info, head[17]);
                return info;
            }
        }

        private static void ReadThumbnailInfo(FileStream fs, Sai2Info info)
        {
            for (int i = 0; i < info.Entries.Count; i++)
            {
                if (info.Entries[i].Type != "thum") continue;

                long start = info.Entries[i].Offset;
                long end = i + 1 < info.Entries.Count ? info.Entries[i + 1].Offset : fs.Length;
                if (start < 0 || end > fs.Length || end - start < 18) return;

                info.BlobStart = start;
                info.BlobEnd = end;

                var buf = new byte[18];
                fs.Position = start;
                ReadExactly(fs, buf, 0, 18);

                info.ThumbWidth = (int)BitConverter.ToUInt32(buf, 0);
                info.ThumbHeight = (int)BitConverter.ToUInt32(buf, 4);
                info.ThumbTag = Encoding.ASCII.GetString(buf, 8, 4);

                if (info.ThumbTag == JssfTag)
                {
                    info.ThumbChannels = BitConverter.ToUInt16(buf, 16);
                    info.HasJssfThumb = info.ThumbChannels >= 1 && info.ThumbChannels <= 4;
                }
                else if (info.ThumbTag == DpcmTag)
                {
                    info.HasDpcmThumb = true;
                }
                return;
            }
        }

        /// <summary>
        ///     'intg' 块是整幅画布分辨率（1:1）的无损预览，压缩方式是 dpcm。
        /// </summary>
        private static void ReadIntgInfo(FileStream fs, Sai2Info info, byte backgroundFlags)
        {
            for (int i = 0; i < info.Entries.Count; i++)
            {
                if (info.Entries[i].Type != "intg") continue;

                long start = info.Entries[i].Offset;
                long end = i + 1 < info.Entries.Count ? info.Entries[i + 1].Offset : fs.Length;
                if (start < 0 || end > fs.Length || end - start < 8) return;

                var tag = new byte[4];
                fs.Position = start;
                ReadExactly(fs, tag, 0, 4);
                if (Encoding.ASCII.GetString(tag, 0, 4) != DpcmTag) return;

                info.HasIntg = true;
                info.IntgStart = start;
                info.IntgEnd = end;
                info.IntgChannels = (backgroundFlags & 7) == 0 ? 4 : 3;
                return;
            }
        }

        /// <summary>把 'thum' 数据块里的 JSSF 流重建成一份标准 JPEG。</summary>
        public static byte[] ExtractJssfJpeg(string path, Sai2Info info)
        {
            if (info == null || !info.HasJssfThumb) return null;

            long dataStart = info.BlobStart + 18;   // u32 宽 + u32 高 + 4 字节 tag + u16 宽 + u16 高 + u16 通道
            int dataLength = checked((int)(info.BlobEnd - dataStart));
            if (dataLength <= 0) return null;

            var data = new byte[dataLength];
            using (var fs = OpenRead(path))
            {
                fs.Position = dataStart;
                ReadExactly(fs, data, 0, dataLength);
            }

            return JssfToJpeg.Convert(data, info.ThumbWidth, info.ThumbHeight, info.ThumbChannels);
        }

        /// <summary>把 'intg' 块解成 BGRA32，长边最多 maxLongSide 像素。</summary>
        public static byte[] ExtractIntgScaled(string path, Sai2Info info, int maxLongSide, out int outWidth, out int outHeight)
        {
            outWidth = 0;
            outHeight = 0;
            if (info == null || !info.HasIntg) return null;

            long length = info.IntgEnd - info.IntgStart;
            if (length <= 8 || length > 512L * 1024 * 1024) return null;

            var blob = new byte[length];
            using (var fs = OpenRead(path))
            {
                fs.Position = info.IntgStart;
                ReadExactly(fs, blob, 0, blob.Length);
            }

            return DpcmDecoder.DecodeScaled(blob, 0, blob.Length, (int)info.CanvasWidth, (int)info.CanvasHeight,
                info.IntgChannels, maxLongSide, out outWidth, out outHeight);
        }

        private static void ReadExactly(Stream s, byte[] buffer, int offset, int count)
        {
            int done = 0;
            while (done < count)
            {
                int n = s.Read(buffer, offset + done, count - done);
                if (n <= 0) throw new EndOfStreamException("文件被截断");
                done += n;
            }
        }
    }
}
