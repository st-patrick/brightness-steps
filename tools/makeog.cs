// Renders the 1200x630 link-preview image. Reddit, Discord and the like show
// this rather than the page, so it is the first thing most people will see.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;

class MakeOg
{
    static void Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : ".";
        const int W = 1200, H = 630;

        using (var bmp = new Bitmap(W, H))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, W, H),
                       Color.FromArgb(28, 33, 48), Color.FromArgb(13, 15, 20), 70f))
                g.FillRectangle(bg, 0, 0, W, H);

            // Sun mark, same idea as the app icon.
            float cx = 168, cy = 168, discR = 46;
            using (var pen = new Pen(Color.FromArgb(235, 240, 201, 135), 9f))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4 - Math.PI / 2;
                    g.DrawLine(pen,
                        cx + (float)Math.Cos(a) * discR * 1.6f, cy + (float)Math.Sin(a) * discR * 1.6f,
                        cx + (float)Math.Cos(a) * discR * 2.3f, cy + (float)Math.Sin(a) * discR * 2.3f);
                }
            }
            var disc = new RectangleF(cx - discR, cy - discR, discR * 2, discR * 2);
            using (var b = new LinearGradientBrush(new RectangleF(disc.X, disc.Y - 1, disc.Width, disc.Height + 2),
                       Color.FromArgb(255, 255, 246, 224), Color.FromArgb(255, 26, 28, 34), 90f))
                g.FillEllipse(b, disc);
            using (var rim = new Pen(Color.FromArgb(150, 255, 220, 150), 3f))
                g.DrawEllipse(rim, disc);

            using (var title = new Font("Segoe UI", 62f, FontStyle.Bold))
            using (var w = new SolidBrush(Color.FromArgb(238, 242, 250)))
                g.DrawString("BrightnessSteps", title, w, 268, 108);

            using (var tag = new Font("Segoe UI", 26f))
            using (var gold = new SolidBrush(Color.FromArgb(240, 201, 135)))
                g.DrawString("Your laptop's brightness keys, with usable", tag, gold, 274, 208);
            using (var tag = new Font("Segoe UI", 26f))
            using (var gold = new SolidBrush(Color.FromArgb(240, 201, 135)))
                g.DrawString("steps at the dark end.", tag, gold, 274, 246);

            // The comparison, which is the whole pitch: same axis for both, so
            // it is obvious that Windows offers nothing where you need it.
            DrawRuler(g, 96, 396, W - 192, "WINDOWS  -  11 STOPS",
                new[] { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 }, 0);
            DrawRuler(g, 96, 506, W - 192, "BRIGHTNESS STEPS  -  26 STOPS",
                new[] { 0, 1, 2, 3, 4, 5, 6, 8, 10, 13, 16, 20, 25, 30, 37, 45, 55, 67, 82, 100 }, 6);

            using (var small = new Font("Segoe UI", 17f))
            using (var dim = new SolidBrush(Color.FromArgb(150, 160, 178)))
                g.DrawString("Free and open source  Â·  no admin, no telemetry", small, dim, 96, 578);

            bmp.Save(Path.Combine(outDir, "og.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        Console.WriteLine("wrote og.png");
    }

    /// <summary>A 0-100 brightness axis with a tick at every level the keys can reach.</summary>
    static void DrawRuler(Graphics g, int x, int y, int width, string label, int[] stops, int belowZero)
    {
        using (var f = new Font("Segoe UI", 13f, FontStyle.Bold))
        using (var b = new SolidBrush(Color.FromArgb(150, 160, 178)))
            g.DrawString(label, f, b, x, y - 28);

        int axisX = x + (belowZero > 0 ? 118 : 0);
        int axisW = width - (belowZero > 0 ? 118 : 0);

        using (var track = new SolidBrush(Color.FromArgb(46, 52, 66)))
            g.FillRectangle(track, axisX, y + 26, axisW, 3);

        foreach (int v in stops)
        {
            float px = axisX + axisW * (v / 100f);
            float h = 26f;
            using (var b = new SolidBrush(Color.FromArgb(240, 201, 135)))
                g.FillRectangle(b, px - 2f, y, 4f, h);
        }

        // The rungs that live below what the backlight alone can reach.
        if (belowZero > 0)
        {
            using (var b = new SolidBrush(Color.FromArgb(120, 240, 201, 135)))
                for (int i = 0; i < belowZero; i++)
                    g.FillRectangle(b, x + 4 + i * 18f, y + 6, 4f, 20f);
            using (var f = new Font("Segoe UI", 11f))
            using (var b = new SolidBrush(Color.FromArgb(130, 140, 158)))
                g.DrawString("below zero", f, b, x, y + 30);
        }
    }
}


