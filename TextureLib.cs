// Texture decoding for the FX previewer.
//  - DDS: DXT1 / DXT2 / DXT3 / DXT4 / DXT5 (BC1..BC3) plus uncompressed 32/24/16/8-bit
//  - TGA: uncompressed + RLE, true colour / grayscale / colour mapped
//  - PNG / JPG / BMP / GIF: via System.Drawing
//  - Procedural proxy textures for referenced-but-unavailable base-game art
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace FxPreview
{
    public class DecodedImage
    {
        public int Width;
        public int Height;
        public byte[] Rgba;      // straight (non-premultiplied) RGBA, row 0 = top
    }

    public static class TextureLib
    {
        public static readonly string[] ImageExts = new string[] { ".dds", ".tga", ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

        public static bool IsImageExt(string ext)
        {
            ext = ext.ToLowerInvariant();
            for (int i = 0; i < ImageExts.Length; i++) if (ImageExts[i] == ext) return true;
            return false;
        }

        public static int ExtRank(string ext)
        {
            switch (ext.ToLowerInvariant())
            {
                case ".dds": return 0;
                case ".tga": return 1;
                case ".png": return 2;
                case ".jpg": return 3;
                case ".jpeg": return 4;
                case ".bmp": return 5;
                default: return 9;
            }
        }

        public static DecodedImage Load(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".dds") return FromDds(b);
            if (ext == ".tga") return FromTga(b);
            return FromGdi(b);
        }

        public static DecodedImage FromGdi(byte[] b)
        {
            using (MemoryStream ms = new MemoryStream(b))
            using (Bitmap src = new Bitmap(ms))
            {
                int w = src.Width, h = src.Height;
                DecodedImage img = new DecodedImage();
                img.Width = w; img.Height = h;
                img.Rgba = new byte[w * h * 4];
                using (Bitmap bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.Clear(Color.Transparent);
                        g.DrawImage(src, new Rectangle(0, 0, w, h));
                    }
                    BitmapData bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    try
                    {
                        for (int y = 0; y < h; y++)
                            System.Runtime.InteropServices.Marshal.Copy(IntPtr.Add(bd.Scan0, y * bd.Stride), img.Rgba, y * w * 4, w * 4);
                    }
                    finally { bmp.UnlockBits(bd); }
                }
                // System.Drawing gives BGRA on little endian for Format32bppArgb? It gives BGRA byte order.
                SwapRb(img.Rgba);
                return img;
            }
        }

        private static void SwapRb(byte[] rgba)
        {
            for (int i = 0; i + 3 < rgba.Length; i += 4)
            {
                byte t = rgba[i]; rgba[i] = rgba[i + 2]; rgba[i + 2] = t;
            }
        }

        private static uint RdU32(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        // ---------------------------------------------------------------- DDS
        public static DecodedImage FromDds(byte[] b)
        {
            if (b.Length < 128 || b[0] != 'D' || b[1] != 'D' || b[2] != 'S' || b[3] != ' ')
                throw new Exception("not a DDS file");
            int h = (int)RdU32(b, 12);
            int w = (int)RdU32(b, 16);
            int pfFlags = (int)RdU32(b, 80);
            string fourCC = Encoding.ASCII.GetString(b, 84, 4);
            int rgbBits = (int)RdU32(b, 88);
            uint rMask = RdU32(b, 92), gMask = RdU32(b, 96), bMask = RdU32(b, 100), aMask = RdU32(b, 104);
            const int dataOff = 128;
            if (w <= 0 || h <= 0 || (long)w * h > 64L * 1024 * 1024) throw new Exception("bad DDS dimensions");

            DecodedImage img = new DecodedImage();
            img.Width = w; img.Height = h;
            img.Rgba = new byte[w * h * 4];

            bool hasFourCC = (pfFlags & 0x4) != 0;
            if (hasFourCC && fourCC.Length == 4 && fourCC[0] == 'D')
            {
                bool dxt1 = fourCC == "DXT1";
                bool dxt3 = fourCC == "DXT3" || fourCC == "DXT2";
                bool dxt5 = fourCC == "DXT5" || fourCC == "DXT4";
                if (!dxt1 && !dxt3 && !dxt5) throw new Exception("unsupported DDS fourCC " + fourCC);
                int bw = (w + 3) / 4, bh = (h + 3) / 4;
                int blockBytes = dxt1 ? 8 : 16;
                int off = dataOff;
                byte[] blk = new byte[16];
                for (int by = 0; by < bh; by++)
                {
                    for (int bx = 0; bx < bw; bx++)
                    {
                        if (off + blockBytes > b.Length) break;
                        Buffer.BlockCopy(b, off, blk, 0, blockBytes);
                        off += blockBytes;

                        int co = dxt1 ? 0 : 8;
                        int c0 = blk[co] | (blk[co + 1] << 8);
                        int c1 = blk[co + 2] | (blk[co + 3] << 8);
                        int[] cr = new int[4], cg = new int[4], cb = new int[4];
                        From565(c0, out cr[0], out cg[0], out cb[0]);
                        From565(c1, out cr[1], out cg[1], out cb[1]);
                        bool punch = dxt1 && c0 <= c1;
                        for (int k = 2; k <= 3; k++)
                        {
                            int wa = (k == 2) ? 1 : 2, wb = (k == 2) ? 2 : 1;
                            cr[k] = (cr[0] * wa + cr[1] * wb) / 3;
                            cg[k] = (cg[0] * wa + cg[1] * wb) / 3;
                            cb[k] = (cb[0] * wa + cb[1] * wb) / 3;
                        }
                        uint bits = (uint)(blk[co + 4] | (blk[co + 5] << 8) | (blk[co + 6] << 16) | (blk[co + 7] << 24));
                        for (int py = 0; py < 4; py++)
                        {
                            for (int px = 0; px < 4; px++)
                            {
                                int x = bx * 4 + px, y = by * 4 + py;
                                if (x >= w || y >= h) continue;
                                int ci = (int)((bits >> (2 * (py * 4 + px))) & 3);
                                int a;
                                if (dxt1) a = (punch && ci == 3) ? 0 : 255;
                                else if (dxt3) a = ((blk[py] >> (px * 4)) & 0xF) * 17;
                                else
                                {
                                    int ai = AlphaIndex3(blk, py * 4 + px);
                                    a = Alpha5(blk[0], blk[1], ai);
                                }
                                int o = (y * w + x) * 4;
                                img.Rgba[o] = (byte)cr[ci];
                                img.Rgba[o + 1] = (byte)cg[ci];
                                img.Rgba[o + 2] = (byte)cb[ci];
                                img.Rgba[o + 3] = (byte)a;
                            }
                        }
                    }
                }
                return img;
            }

            if (rgbBits == 32 || rgbBits == 24 || rgbBits == 16 || rgbBits == 8)
            {
                int srcBytes = rgbBits / 8;
                if (srcBytes < 1) srcBytes = 1;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int so = dataOff + (y * w + x) * srcBytes;
                        if (so + srcBytes > b.Length) break;
                        uint pxv = 0;
                        for (int k = 0; k < srcBytes; k++) pxv |= (uint)b[so + k] << (8 * k);
                        int o = (y * w + x) * 4;
                        img.Rgba[o] = (byte)ExtractChannel(pxv, rMask, rgbBits);
                        img.Rgba[o + 1] = (byte)ExtractChannel(pxv, gMask, rgbBits);
                        img.Rgba[o + 2] = (byte)ExtractChannel(pxv, bMask, rgbBits);
                        img.Rgba[o + 3] = aMask == 0 ? (byte)255 : (byte)ExtractChannel(pxv, aMask, rgbBits);
                    }
                }
                return img;
            }
            // D3D 的数字格式码（FourCC 字段里塞的是 D3DFORMAT 枚举值），
            // 典型例子：113 = D3DFMT_A16B16G16R16F —— RA3 的 HDR 光晕贴图用的就是这个。
            if (hasFourCC)
            {
                int code = b[84] | (b[85] << 8) | (b[86] << 16) | (b[87] << 24);
                if (DecodeByD3dFormat(code, b, dataOff, w, h, img)) return img;
                throw new Exception("unsupported DDS format code " + code);
            }
            throw new Exception("unsupported DDS pixel format");
        }

        /// <summary>Decode DDS whose header carries a D3DFORMAT enum value instead of an ASCII FourCC.</summary>
        private static bool DecodeByD3dFormat(int code, byte[] b, int dataOff, int w, int h, DecodedImage img)
        {
            int bpp, kind;
            switch (code)
            {
                case 113: bpp = 8; kind = 0; break;    // A16B16G16R16F
                case 112: bpp = 4; kind = 1; break;    // G16R16F
                case 111: bpp = 2; kind = 2; break;    // R16F
                case 116: bpp = 16; kind = 3; break;   // A32B32G32R32F
                case 26: bpp = 8; kind = 4; break;     // A16B16G16R16
                case 34: bpp = 4; kind = 5; break;     // G16R16
                case 21: case 22: bpp = 4; kind = 6; break;   // A8R8G8B8 / X8R8G8B8（内存里是 BGRA）
                case 28: case 29: bpp = 4; kind = 7; break;   // A8B8G8R8 / X8B8G8R8
                default: return false;
            }
            if (dataOff + (long)w * h * bpp > b.Length) return false;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int so = dataOff + (y * w + x) * bpp;
                    int o = (y * w + x) * 4;
                    byte r = 0, g = 0, bl = 0, a = 255;
                    switch (kind)
                    {
                        case 0:
                            r = F16Byte(b, so); g = F16Byte(b, so + 2); bl = F16Byte(b, so + 4); a = F16Byte(b, so + 6);
                            break;
                        case 1:
                            r = F16Byte(b, so); g = F16Byte(b, so + 2); bl = g; a = 255;
                            break;
                        case 2:
                            r = g = bl = F16Byte(b, so); a = 255;
                            break;
                        case 3:
                            r = F32Byte(b, so); g = F32Byte(b, so + 4); bl = F32Byte(b, so + 8); a = F32Byte(b, so + 12);
                            break;
                        case 4:
                            r = (byte)(b[so] >> 8); g = (byte)(b[so + 2] >> 8); bl = (byte)(b[so + 4] >> 8); a = (byte)(b[so + 6] >> 8);
                            break;
                        case 5:
                            r = b[so]; g = b[so + 2]; bl = g; a = 255;
                            break;
                        case 6:
                            bl = b[so]; g = b[so + 1]; r = b[so + 2]; a = b[so + 3];
                            break;
                        default:
                            r = b[so]; g = b[so + 1]; bl = b[so + 2]; a = b[so + 3];
                            break;
                    }
                    img.Rgba[o] = r; img.Rgba[o + 1] = g; img.Rgba[o + 2] = bl; img.Rgba[o + 3] = a;
                }
            }
            return true;
        }

        private static byte F16Byte(byte[] b, int off)
        {
            float f = HalfToFloat((ushort)(b[off] | (b[off + 1] << 8)));
            if (f < 0) f = 0; else if (f > 1) f = 1;   // HDR 存成 8bit，超 1 的部分截断
            return (byte)(f * 255f + 0.5f);
        }

        private static byte F32Byte(byte[] b, int off)
        {
            float f = BitConverter.ToSingle(b, off);
            if (f < 0) f = 0; else if (f > 1) f = 1;
            return (byte)(f * 255f + 0.5f);
        }

        private static float HalfToFloat(ushort v)
        {
            int s = (v >> 15) & 1, e = (v >> 10) & 0x1F, m = v & 0x3FF;
            float f;
            if (e == 0) f = (m / 1024f) * (float)Math.Pow(2, -14);
            else if (e == 31) f = m == 0 ? 0f : 1f;
            else f = (1f + m / 1024f) * (float)Math.Pow(2, e - 15);
            return s == 1 ? -f : f;
        }

        private static void From565(int c, out int r, out int g, out int b)
        {
            int r5 = (c >> 11) & 0x1F, g6 = (c >> 5) & 0x3F, b5 = c & 0x1F;
            r = (r5 << 3) | (r5 >> 2);
            g = (g6 << 2) | (g6 >> 4);
            b = (b5 << 3) | (b5 >> 2);
        }

        private static int AlphaIndex3(byte[] blk, int i)
        {
            int bit = i * 3;
            int by = 2 + (bit >> 3);
            int sh = bit & 7;
            int val = blk[by] | (blk[by + 1] << 8);
            return (val >> sh) & 7;
        }

        private static int Alpha5(int a0, int a1, int idx)
        {
            int[] at = new int[8];
            at[0] = a0; at[1] = a1;
            if (a0 > a1) { for (int k = 1; k <= 6; k++) at[k + 1] = ((7 - k) * a0 + k * a1) / 7; }
            else { for (int k = 1; k <= 4; k++) at[k + 1] = ((5 - k) * a0 + k * a1) / 5; at[6] = 0; at[7] = 255; }
            return at[idx & 7];
        }

        private static int ExtractChannel(uint pxv, uint mask, int bits)
        {
            if (mask == 0) return 255;
            int shift = 0;
            uint m = mask;
            while ((m & 1) == 0 && shift < 32) { m >>= 1; shift++; }
            int max = (int)m;
            if (max == 0) return 255;
            int v = (int)((pxv & mask) >> shift);
            return (v * 255) / max;
        }

        // ---------------------------------------------------------------- TGA
        public static DecodedImage FromTga(byte[] b)
        {
            if (b.Length < 18) throw new Exception("not a TGA file");
            int idLen = b[0];
            int cmapType = b[1];
            int imgType = b[2];
            int cmapLen = b[5] | (b[6] << 8);
            int cmapBits = b[7];
            int w = b[12] | (b[13] << 8);
            int h = b[14] | (b[15] << 8);
            int bpp = b[16];
            int desc = b[17];
            bool topDown = (desc & 0x20) != 0;
            int off = 18 + idLen;
            if (w <= 0 || h <= 0 || (long)w * h > 64L * 1024 * 1024) throw new Exception("bad TGA dimensions");

            int[][] cmap = null;
            if (cmapType == 1)
            {
                int cb = (cmapBits + 7) / 8;
                cmap = new int[Math.Max(1, cmapLen)][];
                for (int i = 0; i < cmapLen; i++)
                {
                    int o = off + i * cb;
                    if (o + cb > b.Length) break;
                    cmap[i] = ReadTgaPixel(b, o, cmapBits);
                }
                off += cmapLen * cb;
            }

            int px = (bpp + 7) / 8;
            if (px < 1) px = 1;
            int count = w * h;
            int[][] pixels = new int[count][];
            bool rle = imgType >= 8;
            int i2 = 0;

            if (!rle)
            {
                for (int i = 0; i < count; i++)
                {
                    int o = off + i * px;
                    pixels[i] = (o + px <= b.Length) ? ReadTgaPixelEx(b, o, bpp, cmapType, cmap) : Opaque();
                }
            }
            else
            {
                while (i2 < count && off < b.Length)
                {
                    int hdr = b[off++];
                    int n = (hdr & 0x7F) + 1;
                    if ((hdr & 0x80) != 0)
                    {
                        int[] v = (off + px <= b.Length) ? ReadTgaPixelEx(b, off, bpp, cmapType, cmap) : Opaque();
                        off += px;
                        for (int k = 0; k < n && i2 < count; k++) pixels[i2++] = v;
                    }
                    else
                    {
                        for (int k = 0; k < n && i2 < count; k++)
                        {
                            if (off + px > b.Length) { pixels[i2++] = Opaque(); continue; }
                            pixels[i2++] = ReadTgaPixelEx(b, off, bpp, cmapType, cmap);
                            off += px;
                        }
                    }
                }
            }

            DecodedImage img = new DecodedImage();
            img.Width = w; img.Height = h;
            img.Rgba = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                int srcRow = topDown ? y : (h - 1 - y);
                for (int x = 0; x < w; x++)
                {
                    int[] c = pixels[srcRow * w + x];
                    if (c == null) c = Opaque();
                    int o = (y * w + x) * 4;
                    img.Rgba[o] = (byte)c[0];
                    img.Rgba[o + 1] = (byte)c[1];
                    img.Rgba[o + 2] = (byte)c[2];
                    img.Rgba[o + 3] = (byte)c[3];
                }
            }
            return img;
        }

        private static int[] Opaque() { return new int[] { 0, 0, 0, 255 }; }

        private static int[] ReadTgaPixelEx(byte[] b, int o, int bpp, int cmapType, int[][] cmap)
        {
            if (bpp == 8 && cmapType == 1 && cmap != null)
            {
                int idx = b[o];
                if (idx >= 0 && idx < cmap.Length && cmap[idx] != null) return cmap[idx];
                return Opaque();
            }
            return ReadTgaPixel(b, o, bpp);
        }

        private static int[] ReadTgaPixel(byte[] b, int o, int bpp)
        {
            if (bpp == 8) return new int[] { b[o], b[o], b[o], 255 };
            if (bpp == 15 || bpp == 16)
            {
                int v = b[o] | (b[o + 1] << 8);
                int r = (v >> 10) & 0x1F, g = (v >> 5) & 0x1F, bl = v & 0x1F;
                return new int[] { (r << 3) | (r >> 2), (g << 3) | (g >> 2), (bl << 3) | (bl >> 2), 255 };
            }
            if (bpp == 24) return new int[] { b[o + 2], b[o + 1], b[o], 255 };
            if (bpp == 32) return new int[] { b[o + 2], b[o + 1], b[o], b[o + 3] };
            return new int[] { 255, 0, 255, 255 };
        }

        // ---------------------------------------------------------------- output
        public static byte[] ToPng(DecodedImage img)
        {
            using (Bitmap bmp = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppArgb))
            {
                BitmapData bd = bmp.LockBits(new Rectangle(0, 0, img.Width, img.Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    byte[] row = new byte[img.Width * 4];
                    for (int y = 0; y < img.Height; y++)
                    {
                        Buffer.BlockCopy(img.Rgba, y * img.Width * 4, row, 0, row.Length);
                        SwapRb(row);
                        System.Runtime.InteropServices.Marshal.Copy(row, 0, IntPtr.Add(bd.Scan0, y * bd.Stride), row.Length);
                    }
                }
                finally { bmp.UnlockBits(bd); }
                using (MemoryStream ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    return ms.ToArray();
                }
            }
        }

        public static void SavePng(DecodedImage img, string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, ToPng(img));
        }

        // ------------------------------------------------------- proxy textures
        /// <summary>
        /// Generate a stand-in so an effect still previews when a base-game texture is not
        /// present in the workspace.  The look is chosen from the texture name so that
        /// flares read as flares and smoke reads as smoke.
        /// </summary>
        public static DecodedImage Proxy(string name, int size) { return ProxySheet(name, 1, 1, size); }

        /// <summary>
        /// Build a proxy sprite sheet matching the flipbook layout of the real texture
        /// (FramesPerRow x rows), with a simple animation so previews still move correctly.
        /// </summary>
        public static DecodedImage ProxySheet(string name, int cols, int rows, int cell)
        {
            if (cols < 1) cols = 1; if (rows < 1) rows = 1;
            if (cols > 16) cols = 16; if (rows > 16) rows = 16;
            if (cell < 16) cell = 16; if (cell > 512) cell = 512;
            while ((long)cols * cell * rows * cell > 4L * 1024 * 1024 && cell > 32) cell /= 2;

            DecodedImage img = new DecodedImage();
            img.Width = cols * cell; img.Height = rows * cell;
            img.Rgba = new byte[img.Width * img.Height * 4];
            int total = cols * rows;
            uint baseSeed = 2166136261u;
            for (int i = 0; i < name.Length; i++) baseSeed = (baseSeed ^ (byte)name[i]) * 16777619u;

            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int idx = r * cols + c;
                    float phase = total <= 1 ? 0f : (float)idx / (float)(total - 1);
                    RenderCell(name, img, c * cell, r * cell, cell, baseSeed + (uint)(idx + 1) * 2654435761u, phase);
                }
            return img;
        }

        private static void RenderCell(string name, DecodedImage img, int ox, int oy, int size, uint seed, float phase)
        {
            string n = name.ToLowerInvariant();
            int mode;
            if (n.Contains("lenz") || n.Contains("flare") || n.Contains("glow") || n.Contains("light") || n.Contains("bloom")) mode = 0;
            else if (n.Contains("smoke") || n.Contains("cloud") || n.Contains("steam") || n.Contains("dust") || n.Contains("vapor") || n.Contains("plume")) mode = 1;
            else if (n.Contains("fire") || n.Contains("flame") || n.Contains("ember") || n.Contains("blast") || n.Contains("burn")) mode = 2;
            else if (n.Contains("ring") || n.Contains("wave") || n.Contains("shock")) mode = 3;
            else if (n.Contains("streak") || n.Contains("tracer") || n.Contains("trail") || n.Contains("laser") || n.Contains("beam")) mode = 4;
            else if (n.Contains("bubble") || n.Contains("wake") || n.Contains("ripple") || n.Contains("water")) mode = 5;
            else if (n.Contains("grid") || n.Contains("scroll") || n.Contains("noise") || n.Contains("nrm") || n.Contains("fractal") || n.Contains("mask")) mode = 6;
            else mode = 7;

            Random rnd = new Random((int)(seed & 0x7FFFFFFF));
            int gs = 17;
            float[] grid = new float[gs * gs];
            for (int i = 0; i < grid.Length; i++) grid[i] = (float)rnd.NextDouble();

            // animation: smoke/fire drift upward and grow, flares pulse, others rotate
            float drift = phase * 0.75f;
            float grow = 0.55f + 0.75f * phase;
            float pulse = 0.45f + 0.95f * (float)Math.Sin(Math.PI * clampf(phase, 0f, 1f));
            float rot = phase * 1.2f;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = (x + 0.5f) / size * 2f - 1f;
                    float v = (y + 0.5f) / size * 2f - 1f;
                    float su = u, sv = v;
                    if (mode == 6) { su = u * 2f + drift * 2f; sv = v * 2f; }
                    else if (mode == 1 || mode == 2) { su = u / grow; sv = v / grow - drift * 0.9f; }
                    else if (rot != 0f && mode != 0 && mode != 4)
                    {
                        float ca = (float)Math.Cos(rot), sa = (float)Math.Sin(rot);
                        su = u * ca - v * sa; sv = u * sa + v * ca;
                    }
                    float r = (float)Math.Sqrt(su * su + sv * sv);
                    float nz = Noise(grid, gs, su * 0.5f + 0.5f, sv * 0.5f + 0.5f);
                    float nz2 = Noise(grid, gs, su * 1.7f + 0.5f, sv * 1.7f + 0.5f);
                    // 贴图边界窗口：保证 alpha 在四边形边缘平滑归零，否则旋转后会露出直边。
                    // 径向光晕/圆环用"圆形窗口"，条纹/噪声用"方形窗口"（它们本来就要铺满）。
                    float edge = Math.Max(Math.Abs(su), Math.Abs(sv));
                    float win;
                    if (mode == 4) win = SmoothStep(1.0f, 0.84f, edge);
                    else if (mode == 6) win = SmoothStep(1.0f, 0.94f, edge);
                    else win = SmoothStep(1.0f, 0.78f, r);
                    float t, a;
                    float rr = 1, gg = 1, bb = 1;
                    switch (mode)
                    {
                        case 0:
                            // 纯径向光晕：核心 + 大范围柔光，不做任何十字/星芒结构
                            // （假贴图里加"结构"会被 AngleZ 转成 X 形交叉线，反而误导判断）
                            a = (float)Math.Pow(Math.Max(0, 1 - r), 3.0) * 1.25f
                              + (float)Math.Pow(Math.Max(0, 1 - r), 1.25) * 0.55f;
                            a *= 0.86f + 0.28f * nz;
                            a *= pulse;
                            // 略微偏暖的白：原版光晕贴图基本都是暖白，
                            // 纯白会让爆炸/火焰的亮部在叠加后变成中性白（游戏里是橙色）
                            rr = 1.0f; gg = 0.90f; bb = 0.74f;
                            break;
                        case 1:
                            t = nz * 0.7f + nz2 * 0.3f;
                            a = t * (float)Math.Pow(Math.Max(0, 1 - r), 1.1f) * 1.7f * (0.35f + 0.8f * pulse);
                            // 烟雾类贴图多为中深灰（对照游戏内截图，烟团亮度约 0.23），
                            // 之前做成接近白色会让烟比游戏亮一倍以上、还会把画面糊成中性灰
                            rr = gg = bb = 0.24f + 0.26f * t;
                            break;
                        case 2:
                            t = Math.Max(0, 1 - r) * (0.55f + 0.65f * nz);
                            a = (float)Math.Pow(Math.Max(0, 1 - r), 1.4f) * (0.45f + 0.75f * t) * (0.3f + 0.9f * pulse);
                            rr = Math.Min(1, 0.55f + 1.8f * t);
                            gg = Math.Min(1, 0.9f * t * t + 0.15f * t);
                            bb = Math.Max(0, 0.35f * t * t * t);
                            break;
                        case 3:
                            t = 1f - Math.Min(1, Math.Abs(r - (0.30f + 0.42f * phase)) * 6.5f);
                            a = (float)Math.Pow(t, 2) * (0.75f + 0.35f * nz);
                            break;
                        case 4:
                            t = Math.Max(0, 1 - Math.Abs(sv) * 2.6f);
                            a = (float)Math.Pow(t, 1.6f) * (float)Math.Pow(Math.Max(0, 1 - Math.Abs(su)), 0.35f);
                            break;
                        case 5:
                            t = 1f - Math.Min(1, Math.Abs(r - (0.26f + 0.40f * phase)) * 4.5f);
                            a = (float)Math.Pow(t, 1.5f) * (0.6f + 0.5f * nz);
                            rr = 0.85f; gg = 0.95f; bb = 1f;
                            break;
                        case 6:
                            t = nz * 0.6f + nz2 * 0.4f;
                            a = Math.Min(1, t * 1.3f);
                            break;
                        default:
                            t = nz * 0.65f + 0.35f * Math.Max(0, 1 - r);
                            a = Math.Min(1, (float)Math.Pow(Math.Max(0, 1 - r), 1.5f) * 1.6f * (0.5f + 0.6f * t));
                            break;
                    }
                    a *= win;
                    if (mode != 2)
                    {
                        rr = Math.Min(1, rr * (0.75f + 0.45f * nz));
                        gg = Math.Min(1, gg * (0.75f + 0.45f * nz));
                        bb = Math.Min(1, bb * (0.75f + 0.45f * nz));
                    }
                    if (a < 0) a = 0; else if (a > 1) a = 1;
                    int o = ((oy + y) * img.Width + (ox + x)) * 4;
                    img.Rgba[o] = (byte)(Math.Min(1, rr) * 255);
                    img.Rgba[o + 1] = (byte)(Math.Min(1, gg) * 255);
                    img.Rgba[o + 2] = (byte)(Math.Min(1, bb) * 255);
                    img.Rgba[o + 3] = (byte)(a * 255);
                }
            }
        }

        private static float clampf(float v, float a, float b) { return v < a ? a : (v > b ? b : v); }

        /// <summary>1 when edge &lt;= inner, 0 when edge &gt;= outer, smooth in between.</summary>
        private static float SmoothStep(float outer, float inner, float edge)
        {
            if (edge <= inner) return 1f;
            if (edge >= outer) return 0f;
            float t = (edge - inner) / (outer - inner);
            return 1f - t * t * (3f - 2f * t);
        }

        private static float Noise(float[] grid, int gs, float u, float v)
        {
            float fx = u * (gs - 1), fy = v * (gs - 1);
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            float tx = fx - x0, ty = fy - y0;
            tx = tx * tx * (3 - 2 * tx); ty = ty * ty * (3 - 2 * ty);
            int x1 = x0 + 1, y1 = y0 + 1;
            if (x0 < 0) x0 = 0; if (x0 > gs - 1) x0 = gs - 1;
            if (y0 < 0) y0 = 0; if (y0 > gs - 1) y0 = gs - 1;
            if (x1 < 0) x1 = 0; if (x1 > gs - 1) x1 = gs - 1;
            if (y1 < 0) y1 = 0; if (y1 > gs - 1) y1 = gs - 1;
            float a = grid[y0 * gs + x0], b = grid[y0 * gs + x1], c = grid[y1 * gs + x0], d = grid[y1 * gs + x1];
            return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
        }
    }
}
