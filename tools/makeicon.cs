// Generates app.ico. A brightness glyph whose disc runs black-to-white, which
// is what the tool is actually about: the dark end of the range. Drawn at every
// size rather than scaled, so the 16px tray icon stays legible.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

class MakeIcon
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };

    static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            float pad = s * 0.055f;
            var plate = new RectangleF(pad, pad, s - 2 * pad, s - 2 * pad);
            float radius = s * 0.22f;

            // Rounded plate so it reads as an app icon on any wallpaper.
            using (var path = Rounded(plate, radius))
            using (var bg = new LinearGradientBrush(plate, Color.FromArgb(255, 32, 36, 44),
                                                    Color.FromArgb(255, 16, 18, 23), 90f))
                g.FillPath(bg, path);

            float cx = s / 2f, cy = s / 2f;
            float discR = s * 0.185f;

            // Rays. Dropped at the smallest sizes, where they turn to mush.
            if (s >= 20)
            {
                int rays = 8;
                float inner = discR * 1.55f, outer = discR * 2.25f;
                float thickness = Math.Max(1f, s * 0.052f);
                using (var pen = new Pen(Color.FromArgb(235, 255, 214, 140), thickness))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    for (int i = 0; i < rays; i++)
                    {
                        double a = i * Math.PI * 2 / rays - Math.PI / 2;
                        g.DrawLine(pen,
                            cx + (float)Math.Cos(a) * inner, cy + (float)Math.Sin(a) * inner,
                            cx + (float)Math.Cos(a) * outer, cy + (float)Math.Sin(a) * outer);
                    }
                }
            }

            // The disc: dark at the bottom, bright at the top - the ladder.
            var disc = new RectangleF(cx - discR, cy - discR, discR * 2, discR * 2);
            using (var brush = new LinearGradientBrush(
                       new RectangleF(disc.X, disc.Y - 1, disc.Width, disc.Height + 2),
                       Color.FromArgb(255, 255, 246, 224), Color.FromArgb(255, 26, 28, 34), 90f))
                g.FillEllipse(brush, disc);

            // Keeps the dark half of the disc from vanishing into the plate.
            using (var rim = new Pen(Color.FromArgb(150, 255, 220, 150), Math.Max(1f, s * 0.022f)))
                g.DrawEllipse(rim, disc);
        }
        return bmp;
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        var pngs = new byte[Sizes.Length][];

        for (int i = 0; i < Sizes.Length; i++)
        {
            using (var bmp = Render(Sizes[i]))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                pngs[i] = ms.ToArray();
                if (Sizes[i] == 256 || Sizes[i] == 48)
                    File.WriteAllBytes(Path.Combine(outDir, "icon-" + Sizes[i] + ".png"), pngs[i]);
            }
        }

        // PNG-in-ICO, understood by every Windows since Vista.
        string icoPath = Path.Combine(outDir, "app.ico");
        using (var fs = new FileStream(icoPath, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0);                 // reserved
            w.Write((ushort)1);                 // type: icon
            w.Write((ushort)Sizes.Length);

            int offset = 6 + 16 * Sizes.Length;
            for (int i = 0; i < Sizes.Length; i++)
            {
                w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                w.Write((byte)0);               // palette
                w.Write((byte)0);               // reserved
                w.Write((ushort)1);             // colour planes
                w.Write((ushort)32);            // bits per pixel
                w.Write(pngs[i].Length);
                w.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var png in pngs) w.Write(png);
        }

        Console.WriteLine("wrote {0} ({1} sizes, {2:N0} bytes)", icoPath, Sizes.Length, new FileInfo(icoPath).Length);
    }
}
