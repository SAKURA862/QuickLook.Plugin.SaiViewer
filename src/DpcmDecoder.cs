using System;

// Derived from libsai (MIT, Copyright (c) 2017-2023 Wunkolo):
// sai2::ExtractDpcmToBGRA / UnpackDeltaRLE16 / DeltaUnpackRow16Bpc / Pixel16Bpc
// operations. See the THIRD-PARTY NOTICES section of the repository LICENSE.

namespace QuickLook.Plugin.SaiViewer
{
    /// <summary>
    ///     Decoder for the lossless "intg" (dpcm) canvas preview blob of a SAI2 file.
    ///     Port of sai2::ExtractDpcmToBGRA / UnpackDeltaRLE16 / DeltaUnpackRow16Bpc
    ///     from Wunkolo/libsai (MIT).
    /// </summary>
    internal static class DpcmDecoder
    {
        private const int TileSize = 256;

        internal static Action<string> Trace;
        internal static bool Verbose;

        private static byte[] Fail(string reason)
        {
            var t = Trace;
            if (t != null) t(reason);
            return null;
        }

        /// <summary>
        ///     Decodes the dpcm blob into a BGRA32 buffer of <paramref name="width" /> x
        ///     <paramref name="height" /> pixels. Returns null when the blob is malformed.
        /// </summary>
        public static byte[] Decode(byte[] data, int start, int end, int width, int height, int channels)
        {
            if (start + 4 > end || width <= 0 || height <= 0)
            {
                return Fail("header: start=" + start + " end=" + end + " " + width + "x" + height);
            }

            var p = start + 4; // skip the "dpcm" tag

            var tilesX = (width + TileSize - 1) / TileSize;
            var tilesY = (height + TileSize - 1) / TileSize;
            var tilesCount = tilesX * tilesY;

            if (p + tilesCount * 4 > end)
            {
                return Fail("sizes overflow: tiles=" + tilesCount + " p=" + p + " end=" + end);
            }

            var tileSizes = new int[tilesCount];
            for (var i = 0; i < tilesCount; i++)
            {
                tileSizes[i] = (int)ReadU32(data, p);
                p += 4;
            }

            var image = new byte[width * height * 4];
            var rowDelta = new short[TileSize * 4];
            var sum = new ushort[4];
            var prevPixel16 = new ushort[4];
            var curPixel16 = new ushort[4];

            for (var ty = 0; ty < tilesY; ty++)
            {
                var begY = ty * TileSize;
                var endY = Math.Min(begY + TileSize, height);
                var sizeY = endY - begY;

                for (var tx = 0; tx < tilesX; tx++)
                {
                    var dataSize = tileSizes[ty * tilesX + tx];
                    if (dataSize < 2 || p + dataSize > end)
                    {
                        return Fail("tile " + tx + "," + ty + " size=" + dataSize + " p=" + p + " end=" + end);
                    }

                    var tileStart = p;
                    var tileEnd = p + dataSize;
                    p = tileEnd;

                    var cur = tileStart + 2; // the leading u16 checksum

                    var begX = tx * TileSize;
                    var endX = Math.Min(begX + TileSize, width);
                    var sizeX = endX - begX;

                    for (var row = 0; row < sizeY; row++)
                    {
                        var used = UnpackRowBits(data, cur, tileEnd, rowDelta, sizeX, 4, channels);
                        if (used <= 0)
                        {
                            return Fail("row " + row + " of tile " + tx + "," + ty + " at " + cur + " of " + tileEnd);
                        }

                        cur += used;

                        var dest = (begY + row) * width + begX;
                        var prevDest = row == 0 ? -1 : dest - width;
                        UnpackRow(image, dest, prevDest, rowDelta, sizeX, channels, sum, prevPixel16, curPixel16);
                    }

                    if (cur != tileEnd)
                    {
                        var t = Trace;
                        if (t != null)
                        {
                            t("tile " + tx + "," + ty + " ended at " + cur + " expected " + tileEnd + " (diff " + (tileEnd - cur) + ")");
                        }
                    }
                }

                p += 2; // per tile-row checksum
            }

            return image;
        }

        /// <summary>
        ///     Decodes the dpcm blob and boxes it down so that the long side is at most
        ///     <paramref name="maxLongSide" /> pixels. Tiles are independent (every tile
        ///     predicts its first row from zeroes), so they are decoded in parallel.
        /// </summary>
        public static byte[] DecodeScaled(byte[] data, int start, int end, int width, int height, int channels,
            int maxLongSide, out int outWidth, out int outHeight)
        {
            outWidth = 0;
            outHeight = 0;

            if (start + 4 > end || width <= 0 || height <= 0)
            {
                return Fail("header: start=" + start + " end=" + end + " " + width + "x" + height);
            }

            var scale = Math.Max(1, (int)Math.Ceiling(Math.Max(width, height) / (double)Math.Max(1, maxLongSide)));
            outWidth = (width + scale - 1) / scale;
            outHeight = (height + scale - 1) / scale;

            var p = start + 4;

            var tilesX = (width + TileSize - 1) / TileSize;
            var tilesY = (height + TileSize - 1) / TileSize;
            var tilesCount = tilesX * tilesY;

            if (p + tilesCount * 4 > end)
            {
                return Fail("sizes overflow: tiles=" + tilesCount);
            }

            var tileSizes = new int[tilesCount];
            for (var i = 0; i < tilesCount; i++)
            {
                tileSizes[i] = (int)ReadU32(data, p);
                p += 4;
            }

            // Data blocks are laid out tile by tile, with a u16 checksum after each band.
            var offsets = new long[tilesCount];
            long cursor = p;
            for (var ty = 0; ty < tilesY; ty++)
            {
                for (var tx = 0; tx < tilesX; tx++)
                {
                    var index = ty * tilesX + tx;
                    offsets[index] = cursor;
                    cursor += tileSizes[index];
                }

                cursor += 2;
            }

            if (cursor > end)
            {
                return Fail("tiles overflow: needed " + cursor + " of " + end);
            }

            var image = new byte[outWidth * outHeight * 4];
            var context = new TileContext
            {
                Data = data,
                Image = image,
                Width = width,
                Height = height,
                Channels = channels,
                Scale = scale,
                OutWidth = outWidth,
                OutHeight = outHeight,
                TilesX = tilesX,
                End = end,
            };

            var failed = 0;
            try
            {
                System.Threading.Tasks.Parallel.For(0, tilesCount, index =>
                {
                    if (System.Threading.Volatile.Read(ref failed) != 0) return;
                    if (!DecodeTile(context, index, offsets[index], tileSizes[index]))
                    {
                        System.Threading.Interlocked.Exchange(ref failed, 1);
                    }
                });
            }
            catch (Exception ex)
            {
                return Fail("tile decode threw: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (failed != 0)
            {
                return Fail("tile decode failed");
            }

            return image;
        }

        private sealed class TileContext
        {
            public byte[] Data;
            public byte[] Image;
            public int Width;
            public int Height;
            public int Channels;
            public int Scale;
            public int OutWidth;
            public int OutHeight;
            public int TilesX;
            public int End;
        }

        private static bool DecodeTile(TileContext ctx, int index, long tileStart, int dataSize)
        {
            if (dataSize < 2 || tileStart + dataSize > ctx.End) return false;

            var ty = index / ctx.TilesX;
            var tx = index - ty * ctx.TilesX;

            var tileEnd = (int)(tileStart + dataSize);
            var cur = (int)tileStart + 2;

            var begX = tx * TileSize;
            var sizeX = Math.Min(TileSize, ctx.Width - begX);
            var begY = ty * TileSize;
            var sizeY = Math.Min(TileSize, ctx.Height - begY);
            if (sizeX <= 0 || sizeY <= 0) return true;

            var delta = new short[TileSize * 4];
            var curRow = new byte[TileSize * 4];
            var prevRow = new byte[TileSize * 4];

            var channels = ctx.Channels;
            var scale = ctx.Scale;
            var outWidth = ctx.OutWidth;
            var outHeight = ctx.OutHeight;
            var image = ctx.Image;
            var data = ctx.Data;

            var oxStart = begX / scale;
            var oxEnd = Math.Min((begX + sizeX - 1) / scale, outWidth - 1);

            for (var row = 0; row < sizeY; row++)
            {
                var used = UnpackRowBits(data, cur, tileEnd, delta, sizeX, 4, channels);
                if (used <= 0) return false;
                cur += used;

                var hasPrev = row > 0;
                int s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                int p0 = 0, p1 = 0, p2 = 0, p3 = 0;

                for (var px = 0; px < sizeX; px++)
                {
                    var q = px << 2;
                    var c0 = hasPrev ? prevRow[q] : 0;
                    var c1 = hasPrev ? prevRow[q + 1] : 0;
                    var c2 = hasPrev ? prevRow[q + 2] : 0;
                    var c3 = hasPrev ? prevRow[q + 3] : 0;

                    curRow[q] = (byte)Chan(s0, c0, p0, delta[q], out s0);
                    curRow[q + 1] = (byte)Chan(s1, c1, p1, delta[q + 1], out s1);
                    curRow[q + 2] = (byte)Chan(s2, c2, p2, delta[q + 2], out s2);
                    curRow[q + 3] = channels < 4 ? (byte)0xFF : (byte)Chan(s3, c3, p3, delta[q + 3], out s3);

                    p0 = c0;
                    p1 = c1;
                    p2 = c2;
                    p3 = c3;
                }

                var canvasY = begY + row;
                if (canvasY % scale == 0)
                {
                    var outY = canvasY / scale;
                    if (outY < outHeight)
                    {
                        var o = outY * outWidth * 4;
                        for (var ox = oxStart; ox <= oxEnd; ox++)
                        {
                            var from = Math.Max(ox * scale, begX) - begX;
                            var to = Math.Min(ox * scale + scale, begX + sizeX) - begX;
                            var n = to - from;
                            if (n < 1) continue;

                            int r = 0, g = 0, b = 0, a = 0;
                            for (var i = from; i < to; i++)
                            {
                                var q = i << 2;
                                r += curRow[q];
                                g += curRow[q + 1];
                                b += curRow[q + 2];
                                a += curRow[q + 3];
                            }

                            var oo = o + (ox << 2);
                            image[oo] = (byte)(r / n);
                            image[oo + 1] = (byte)(g / n);
                            image[oo + 2] = (byte)(b / n);
                            image[oo + 3] = (byte)(a / n);
                        }
                    }
                }

                var swap = prevRow;
                prevRow = curRow;
                curRow = swap;
            }

            return true;
        }

        /// <summary>
        ///     libsai's add16 / subSat / addSat(0xFF00) / subSat(0xFF00) chain reduces to
        ///     "clamp to 0..255"; the caller clamps the result of adding the delta too.
        /// </summary>
        private static int Chan(int sum, int curPixel, int prevPixel, int delta, out int newSum)
        {
            var t = (sum + curPixel) & 0xFFFF;
            if (t < prevPixel) t = 0; else t -= prevPixel;
            if (t > 0xFF) t = 0xFF;
            var next = (t + delta) & 0xFFFF;
            newSum = next;
            return next > 0xFF ? 0xFF : next;
        }
        /// <summary>
        ///     Bit reader over the tile bytes: a 64-bit window built from two consecutive
        ///     little-endian 32-bit words, so peeking never runs past the end.
        /// </summary>
        private sealed class BitReader
        {
            private readonly byte[] _data;
            private readonly int _start;
            private readonly int _end;
            private readonly int _wordCount;
            private long _bitPos;

            public BitReader(byte[] data, int start, int end)
            {
                _data = data;
                _start = start;
                _end = end;
                _wordCount = (end - start + 3) / 4;
            }

            public long BitPos
            {
                get { return _bitPos; }
            }

            public void Skip(int bits)
            {
                _bitPos += bits;
            }

            private ulong Word(int index)
            {
                if (index < 0 || index >= _wordCount)
                {
                    return 0;
                }

                var pos = _start + index * 4;
                ulong b0 = pos < _end ? _data[pos] : 0UL;
                ulong b1 = pos + 1 < _end ? _data[pos + 1] : 0UL;
                ulong b2 = pos + 2 < _end ? _data[pos + 2] : 0UL;
                ulong b3 = pos + 3 < _end ? _data[pos + 3] : 0UL;
                return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
            }

            public ulong Peek()
            {
                var index = (int)(_bitPos >> 5);
                var offset = (int)(_bitPos & 31);
                if (index >= _wordCount)
                {
                    return 0;
                }

                var lo = Word(index);
                var hi = Word(index + 1);
                return offset == 0 ? (lo | (hi << 32)) : ((lo >> offset) | (hi << (32 - offset)));
            }

            public bool PastEnd()
            {
                return _bitPos > (long)(_end - _start) * 8 + 64;
            }
        }

        /// <summary>
        ///     Decodes one decompressed row: every channel of <paramref name="pixelCount" />
        ///     pixels, in channel-major order. Returns the number of bytes consumed.
        /// </summary>
        private static int UnpackRowBits(byte[] data, int start, int end, short[] output, int pixelCount, int outputChannels, int inputChannels)
        {
            var reader = new BitReader(data, start, end);

            for (var channel = 0; channel < inputChannels; channel++)
            {
                var count = 0;

                while (count < pixelCount)
                {
                    var mask = reader.Peek();
                    if (mask == 0)
                    {
                        return 0;
                    }

                    var firstSet = 0;
                    {
                        var tmp = mask;
                        while ((tmp & 1) == 0)
                        {
                            tmp >>= 1;
                            firstSet++;
                        }
                    }

                    var next = mask >> (firstSet + 1);
                    var opCode = (2 * firstSet) | (int)(next & 1);
                    reader.Skip(2 + firstSet);

                    if (opCode == 0)
                    {
                        output[channel + outputChannels * count] = 0;
                        count++;
                    }
                    else if (opCode <= 0xE)
                    {
                        var afterPrefix = reader.Peek();
                        var negative = (afterPrefix & (1UL << opCode)) != 0;
                        var magnitude = (long)(1UL << opCode) + (long)(afterPrefix & ((1UL << opCode) - 1)) - 1;
                        reader.Skip(opCode + 1);

                        output[channel + outputChannels * count] = negative ? (short)-magnitude : (short)magnitude;
                        count++;
                    }
                    else
                    {
                        var afterPrefix = reader.Peek();
                        var zeroFill = (int)(afterPrefix & 0x7F) + 8;
                        reader.Skip(7);

                        for (var i = 0; i < zeroFill; i++)
                        {
                            if (count < pixelCount)
                            {
                                output[channel + outputChannels * count] = 0;
                            }

                            count++;
                        }
                    }

                    if (reader.PastEnd())
                    {
                        return 0;
                    }
                }
            }

            return (int)((reader.BitPos + 7) / 8);
        }

        private static void UnpackRow(byte[] image, int dest, int prevDest, short[] delta, int pixelCount,
            int inputChannels, ushort[] sum, ushort[] prevPixel16, ushort[] curPixel16)
        {
            for (var i = 0; i < 4; i++)
            {
                sum[i] = 0;
                prevPixel16[i] = 0;
            }

            for (var px = 0; px < pixelCount; px++)
            {
                var dp = px << 2;
                if (prevDest < 0)
                {
                    curPixel16[0] = 0;
                    curPixel16[1] = 0;
                    curPixel16[2] = 0;
                    curPixel16[3] = 0;
                }
                else
                {
                    var pp = (prevDest + px) << 2;
                    curPixel16[0] = image[pp];
                    curPixel16[1] = image[pp + 1];
                    curPixel16[2] = image[pp + 2];
                    curPixel16[3] = image[pp + 3];
                }

                var o = (dest + px) << 2;
                for (var ch = 0; ch < 4; ch++)
                {
                    var s = (ushort)(sum[ch] + curPixel16[ch]); // wrapping add
                    s = SubSat(s, prevPixel16[ch]);
                    s = AddSat(s, 0xFF00);
                    s = SubSat(s, 0xFF00);
                    s = (ushort)(s + (ch < inputChannels ? (ushort)delta[dp + ch] : (ushort)0)); // wrapping add
                    sum[ch] = s;
                    prevPixel16[ch] = curPixel16[ch];
                    image[o + ch] = s > 0xFF ? (byte)0xFF : (byte)s;
                }

                if (inputChannels < 4)
                {
                    image[o + 3] = 0xFF;
                }
            }
        }

        private static ushort AddSat(ushort a, ushort b)
        {
            var r = a + b;
            return r > 0xFFFF ? (ushort)0xFFFF : (ushort)r;
        }

        private static ushort SubSat(ushort a, ushort b)
        {
            return a < b ? (ushort)0 : (ushort)(a - b);
        }

        private static uint ReadU32(byte[] data, int offset)
        {
            return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
        }
    }
}


