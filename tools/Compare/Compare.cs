using System;
using System.IO;
using QuickLook.Plugin.SaiViewer;

internal static class Compare
{
    private static int Main(string[] args)
    {
        try
        {
            var path = args[0];
            var info = Sai2Thumbnail.ReadInfo(path);

            long length = info.IntgEnd - info.IntgStart;
            var blob = new byte[length];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.Position = info.IntgStart;
                var done = 0;
                while (done < blob.Length) done += fs.Read(blob, done, blob.Length - done);
            }

            var w = (int)info.CanvasWidth;
            var h = (int)info.CanvasHeight;

            var full = DpcmDecoder.Decode(blob, 0, blob.Length, w, h, info.IntgChannels);
            int sw, sh;
            var scaled = DpcmDecoder.DecodeScaled(blob, 0, blob.Length, w, h, info.IntgChannels, Math.Max(w, h), out sw, out sh);

            Console.WriteLine("file=" + Path.GetFileName(path) + " canvas=" + w + "x" + h + " ch=" + info.IntgChannels);
            if (full == null) { Console.WriteLine("RESULT: full decode null"); return 2; }
            if (scaled == null) { Console.WriteLine("RESULT: scaled decode null"); return 3; }
            if (sw != w || sh != h) { Console.WriteLine("RESULT: size mismatch " + sw + "x" + sh); return 4; }

            long diff = 0;
            var worst = 0;
            for (var i = 0; i < full.Length; i++)
            {
                var d = Math.Abs(full[i] - scaled[i]);
                if (d != 0) { diff++; if (d > worst) worst = d; }
            }

            Console.WriteLine("mismatched bytes=" + diff + " worst=" + worst + " of " + full.Length);
            Console.WriteLine(diff == 0 ? "RESULT: identical" : "RESULT: differs");
            return diff == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("EX: " + ex);
            return 9;
        }
    }
}
