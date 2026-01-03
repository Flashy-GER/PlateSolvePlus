using System;
using System.IO;
using System.Text;

namespace NINA.Plugins.PlateSolvePlus.SecondaryCamera {
    /// <summary>
    /// Minimal FITS writer for 16-bit monochrome frames (writes BITPIX=16).
    /// Stores unsigned 0..65535 using BZERO=32768, BSCALE=1 and signed data (value-32768).
    /// Big-endian data as required by FITS.
    /// </summary>
    public static class Fits16Writer {
        public static void Write(string filePath, int[,] pixels, int width, int height) {
            if (pixels == null) throw new ArgumentNullException(nameof(pixels));
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("Invalid dimensions.");
            if (pixels.GetLength(0) != height || pixels.GetLength(1) != width)
                throw new ArgumentException("Pixel array dimensions do not match width/height.");

            Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

            using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.Read);

            // --- Header cards (80 chars each), padded to 2880-byte blocks ---
            void Card(string key, string value, string? comment = null) {
                // FITS card: KEYWORD= value / comment
                string left = key.PadRight(8, ' ');
                string body = "= " + value;
                string line = comment == null ? (left + body) : (left + body + " / " + comment);
                if (line.Length > 80) line = line.Substring(0, 80);
                if (line.Length < 80) line = line.PadRight(80, ' ');
                var bytes = Encoding.ASCII.GetBytes(line);
                fs.Write(bytes, 0, bytes.Length);
            }

            Card("SIMPLE", "T", "file conforms to FITS standard");
            Card("BITPIX", "16", "number of bits per data pixel");
            Card("NAXIS", "2", "number of data axes");
            Card("NAXIS1", width.ToString(), "axis 1 length");
            Card("NAXIS2", height.ToString(), "axis 2 length");
            Card("BSCALE", "1", "physical value = BSCALE * array + BZERO");
            Card("BZERO", "32768", "offset data range to unsigned");

            // Minimal WCS placeholders could come later (after solve / for debugging)

            // END card
            var end = Encoding.ASCII.GetBytes("END".PadRight(80, ' '));
            fs.Write(end, 0, end.Length);

            // Pad header to 2880-byte boundary
            PadToBlock(fs, 2880);

            // --- Data: signed 16-bit big-endian ---
            // Store signed short = (unsigned_value - 32768)
            // FITS expects big-endian.
            Span<byte> buf = stackalloc byte[2];

            for (int y = 0; y < height; y++) {
                for (int x = 0; x < width; x++) {
                    int v = pixels[y, x];
                    if (v < 0) v = 0;
                    if (v > 65535) v = 65535;

                    short s = (short)(v - 32768);
                    buf[0] = (byte)((s >> 8) & 0xFF);
                    buf[1] = (byte)(s & 0xFF);
                    fs.Write(buf);
                }
            }

            // Pad data to 2880-byte boundary
            PadToBlock(fs, 2880);
        }

        private static void PadToBlock(Stream s, int blockSize) {
            long mod = s.Position % blockSize;
            if (mod == 0) return;

            int pad = (int)(blockSize - mod);
            byte[] zeros = new byte[pad];
            s.Write(zeros, 0, zeros.Length);
        }
    }
}

