using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace LiteMonitor.src.Core
{
    public static class UIUtils
    {
        private static readonly Dictionary<string, string> _stringPool = new(StringComparer.Ordinal);
        private static readonly object _poolLock = new object();

        public static string Intern(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            lock (_poolLock)
            {
                if (_stringPool.TryGetValue(str, out var pooled)) return pooled;
                _stringPool[str] = str;
                return str;
            }
        }

        public static void ClearStringPool()
        {
            lock (_poolLock) _stringPool.Clear();
        }

        public static float ScaleFactor { get; set; } = 1.0f;

        public static int S(int px) => (int)(px * ScaleFactor);
        public static float S(float px) => px * ScaleFactor;
        public static Size S(Size size) => new Size(S(size.Width), S(size.Height));
        public static Padding S(Padding p) => new Padding(S(p.Left), S(p.Top), S(p.Right), S(p.Bottom));

        private static readonly Dictionary<string, SolidBrush> _brushCache = new(16);
        private static readonly Dictionary<string, Pen> _penCache = new(16);
        private static readonly Dictionary<string, Font> _fontCache = new(16);
        private static readonly object _brushLock = new object();
        private const int MAX_BRUSH_CACHE = 32;

        public static Pen GetPen(Color color, float width = 1.0f)
        {
            string key = $"{color.ToArgb()}_{width:F1}";
            lock (_brushLock)
            {
                if (!_penCache.TryGetValue(key, out var pen))
                {
                    if (_penCache.Count >= MAX_BRUSH_CACHE)
                    {
                        foreach (var p in _penCache.Values) p.Dispose();
                        _penCache.Clear();
                    }
                    pen = new Pen(color, width);
                    _penCache[key] = pen;
                }
                return pen;
            }
        }

        public static SolidBrush GetBrush(string color)
        {
            if (string.IsNullOrEmpty(color)) return (SolidBrush)Brushes.Transparent;

            lock (_brushLock)
            {
                if (!_brushCache.TryGetValue(color, out var br))
                {
                    if (_brushCache.Count >= MAX_BRUSH_CACHE)
                    {
                        foreach (var k in new List<string>(_brushCache.Keys))
                        {
                            if (_brushCache.TryGetValue(k, out var oldBrush))
                            {
                                oldBrush.Dispose();
                                _brushCache.Remove(k);
                            }
                        }
                    }

                    br = new SolidBrush(ParseColor(color));
                    _brushCache[color] = br;
                }
                return br;
            }
        }

        public static Color ParseColor(string hex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex)) return Color.White;
                return ColorTranslator.FromHtml(hex);
            }
            catch
            {
                return Color.White;
            }
        }

        public static Font GetFont(string familyName, float size, bool bold)
        {
            string key = $"{familyName}_{size}_{bold}";
            lock (_brushLock)
            {
                if (!_fontCache.TryGetValue(key, out var font))
                {
                    try 
                    {
                        var style = bold ? FontStyle.Bold : FontStyle.Regular;
                        font = new Font(familyName, size, style);
                    }
                    catch
                    {
                        font = new Font(SystemFonts.DefaultFont.FontFamily, size, bold ? FontStyle.Bold : FontStyle.Regular);
                    }
                    _fontCache[key] = font;
                }
                return font;
            }
        }

        public static void ClearBrushCache()
        {
            lock (_brushLock)
            {
                foreach (var b in _brushCache.Values) b.Dispose();
                _brushCache.Clear();

                foreach (var p in _penCache.Values) p.Dispose();
                _penCache.Clear();
                
                foreach (var f in _fontCache.Values) f.Dispose();
                _fontCache.Clear();
            }
        }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            GraphicsPath p = new GraphicsPath();
            if (r.Width <= 0 || r.Height <= 0) return p;

            if (radius <= 0)
            {
                p.AddRectangle(r);
                return p;
            }

            int d = radius * 2;
            if (d > r.Width) d = r.Width;
            if (d > r.Height) d = r.Height;

            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRoundRect(Graphics g, Rectangle r, int radius, Color c)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            using var brush = new SolidBrush(c);
            using var path = RoundRect(r, radius);
            g.FillPath(brush, path);
        }
    }
}
