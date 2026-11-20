using System;
using System.IO;

// Derived from libsai (MIT, Copyright (c) 2017-2023 Wunkolo):
// sai2::ConvertJssfToJpeg / sai2::ExtractJssfToJpeg. See the THIRD-PARTY NOTICES
// section of the repository LICENSE.

namespace QuickLook.Plugin.SaiViewer
{
    /// <summary>
    /// SAI2 的 JSSF 缩略图是"按 MCU 行切段、每行带一个重启标记"的基线 JPEG 熵编码流，
    /// 缺的是 SOI/DQT/DHT/SOF0/DRI/SOS 这些头部。这里按 libsai 的 ConvertJssfToJpeg 重建它们。
    /// </summary>
    internal static class JssfToJpeg
    {
        public static byte[] Convert(byte[] data, int width, int height, int channels)
        {
            if (data == null || width <= 0 || height <= 0 || channels < 1 || channels > 4) return null;

            int p = 0;
            if (data.Length < 64) return null;

            var lumaQuant = new byte[64];
            Buffer.BlockCopy(data, p, lumaQuant, 0, 64);
            p += 64;

            byte[] chromaQuant = null;
            if (channels > 1)
            {
                if (data.Length < p + 64) return null;
                chromaQuant = new byte[64];
                Buffer.BlockCopy(data, p, chromaQuant, 0, 64);
                p += 64;
            }

            var o = new MemoryStream(1 << 16);

            void U8(byte v) => o.WriteByte(v);
            void U16(int v)
            {
                o.WriteByte((byte)(v >> 8));
                o.WriteByte((byte)v);
            }
            void Bytes(byte[] b) => o.Write(b, 0, b.Length);

            U16(0xFFD8);                                   // SOI

            // DQT
            U16(0xFFDB);
            U16((channels > 1 ? 65 : 0) + 67);
            U8(0x00);
            Bytes(lumaQuant);
            if (channels > 1)
            {
                U8(0x01);
                Bytes(chromaQuant);
            }

            // DHT
            int dhtLength = 2 + HuffmanTables.Lut0.Length + HuffmanTables.Lut1.Length
                + (channels > 1 ? HuffmanTables.Lut2.Length + HuffmanTables.Lut3.Length : 0);
            U16(0xFFC4);
            U16(dhtLength);
            Bytes(HuffmanTables.Lut0);
            Bytes(HuffmanTables.Lut1);
            if (channels > 1)
            {
                Bytes(HuffmanTables.Lut2);
                Bytes(HuffmanTables.Lut3);
            }

            // SOF0
            U16(0xFFC0);
            U16(8 + channels * 3);
            U8(0x08);
            U16(height);
            U16(width);
            U8((byte)channels);
            for (int i = 0; i < channels; i++)
            {
                U8((byte)(i + 1));
                U8(0x11);
                U8((byte)(i != 0 ? 1 : 0));
            }

            // DRI：每条 MCU 行一个重启间隔
            U16(0xFFDD);
            U16(0x0004);
            U16((width + 7) / 8);

            // SOS
            U16(0xFFDA);
            U16(6 + channels * 2);
            U8((byte)channels);
            for (int i = 0; i < channels; i++)
            {
                U8((byte)(i + 1));
                U8((byte)(i == 0 ? 0x00 : 0x11));
            }
            U8(0x00);
            U8(0x3F);
            U8(0x00);

            // MCU 行：u16 长度 + 熵编码数据 + RSTn
            int mcuRows = (height + 7) / 8;
            for (int row = 0; row < mcuRows; row++)
            {
                if (p + 2 > data.Length) return null;
                int size = data[p] | (data[p + 1] << 8);   // 行长度是小端 u16
                p += 2;
                if (size <= 0 || p + size > data.Length) return null;

                o.Write(data, p, size);
                p += size;

                if (row < mcuRows - 1) U16(0xFFD0 | (row & 0x07));
            }

            U16(0xFFD9);                                   // EOI

            return o.ToArray();
        }
    }
}
