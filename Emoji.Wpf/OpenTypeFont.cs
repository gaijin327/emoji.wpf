﻿//
//  Emoji.Wpf — Emoji support for WPF
//
//  Copyright © 2017—2021 Sam Hocevar <sam@hocevar.net>
//
//  This library is free software. It comes without any warranty, to
//  the extent permitted by applicable law. You can redistribute it
//  and/or modify it under the terms of the Do What the Fuck You Want
//  to Public License, Version 2, as published by the WTFPL Task Force.
//  See http://www.wtfpl.net/ for more details.
//

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

using Typography.OpenFont;
using Typography.TextLayout;

namespace Emoji.Wpf
{
    /// <summary>
    /// The EmojiTypeface class exposes layout and rendering primitives from a
    /// ColorTypeface. In the future this object may use several ColorTypeFace
    /// for better coverage.
    /// </summary>
    public class EmojiTypeface
    {
        public EmojiTypeface(string font_name = null)
            => m_fonts.Add(new ColorTypeface(font_name));

        public double Baseline
            => m_fonts[0].Baseline;

        public bool CanRender(string s)
            => m_fonts[0].CanRender(s);

        public double GetScale(double point_size)
            => m_fonts[0].GetScale(point_size);

        public List<ushort> MakeGlyphIndexList(string s)
        {
            var ret = new List<ushort>();
            var gpl = MakeGlyphPlanList(s);
            for (int i = 0; i < gpl.Count; ++i)
                ret.Add(gpl[i].glyphIndex);
            return ret;
        }

        internal IUnscaledGlyphPlanList MakeGlyphPlanList(string s)
        {
            if (!m_cache.TryGetValue(s, out var ret))
            {
                m_cache[s] = ret = new UnscaledGlyphPlanList();
                foreach (var gp in m_fonts[0].StringToGlyphLayout(s))
                    ret.Append(gp);
            }
            return ret;
        }

        internal void RenderGlyph(DrawingContext dc, ushort gid, Point origin, double size, Brush fallback_brush)
            => m_fonts[0].RenderGlyph(dc, gid, origin, size, fallback_brush);

        /// <summary>
        /// A cache of GlyphPlanSequence objects, indexed by source strings. Should
        /// remain pretty lightweight because they are small objects.
        /// </summary>
        private IDictionary<string, IUnscaledGlyphPlanList> m_cache = new Dictionary<string, IUnscaledGlyphPlanList>();

        private IList<ColorTypeface> m_fonts = new List<ColorTypeface>();
    }

    internal class ColorTypeface
    {
        public ColorTypeface(string name)
        {
            m_gtf = GetGlyphTypeface(first_candidate: name);
            if (m_gtf == null)
                return;

            // Read the actual font data using Typography.OpenFont
            using (var s = m_gtf.GetFontStream())
            {
                var r = new OpenFontReader();
                m_openfont = r.Read(s, 0, ReadFlags.Full);
            }

            // Create a reusable layout for glyphs
            m_layout = new GlyphLayout()
            {
                Typeface = m_openfont,
                EnableBuiltinMathItalicCorrection = false, // not needed
                EnableComposition = true,
                EnableGpos = true,
                EnableGsub = true,
                EnableLigature = true,
                PositionTechnique = PositionTechnique.OpenFont,
            };

            // Cache the glyph index for the zero-width joiner
            foreach (var g in StringToGlyphLayout("\u200d", use_gpos: false))
                m_zwj_glyph = g.glyphIndex;
        }

        private GlyphTypeface GetGlyphTypeface(string first_candidate)
        {
            IList<string> all_candidates = new List<string>();

            if (first_candidate != null)
                all_candidates.Add(first_candidate);

            // Some good Emoji font candidates
            all_candidates.Add("Segoe UI Emoji");
            all_candidates.Add(@"c:\Windows\Fonts\seguiemj.ttf");

            // Maybe try the Firefox EmojiOne font?
            var firefox_key = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\firefox.exe";
            var firefox_path = Microsoft.Win32.Registry.GetValue(firefox_key, "Path", null);
            if (firefox_path is string s)
                all_candidates.Add($@"{s}\fonts\EmojiOneMozilla.ttf");

            // Last resort fallbacks
            all_candidates.Add("Segoe UI Symbol"); // for older versions of Windows
            all_candidates.Add("Arial"); // available since Windows 3.1!

            foreach (var name in all_candidates)
            {
                var typeface = new System.Windows.Media.Typeface(name);
                if (typeface.TryGetGlyphTypeface(out var gtf))
                    return gtf;

                try
                {
                    return new GlyphTypeface(new Uri(name));
                }
                catch {}
            }

            return null;
        }

        /// <summary>
        /// Return whether the font can render the given string entirely
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        public bool CanRender(string s)
        {
            foreach (var g in StringToGlyphLayout(s, use_gpos: false))
                if (g.glyphIndex == 0 || g.glyphIndex == m_zwj_glyph)
                    return false;
            return true;
        }

        internal IEnumerable<UnscaledGlyphPlan> StringToGlyphLayout(string s, bool use_gpos = true)
        {
            lock (m_layout)
            {
                m_layout.EnableGpos = use_gpos;
                m_layout.Layout(s.ToCharArray(), 0, s.Length);
                return m_layout.GetUnscaledGlyphPlanIter();
            }
        }

        public double GetScale(double point_size)
            => m_openfont.CalculateScaleToPixelFromPointSize((float)point_size);

        public IDictionary<ushort, double> AdvanceWidths => m_gtf.AdvanceWidths;
        public IDictionary<ushort, double> AdvanceHeights => m_gtf.AdvanceHeights;
        public double Height => m_gtf.Height;
        public double Baseline => m_gtf.Baseline;

        public void RenderGlyph(DrawingContext dc, ushort gid, Point origin, double size, Brush fallback_brush)
        {
            ushort layer_index;
            if (m_openfont.COLRTable != null && m_openfont.CPALTable != null
                 && m_openfont.COLRTable.LayerIndices.TryGetValue(gid, out layer_index))
            {
                int start = layer_index, stop = layer_index + m_openfont.COLRTable.LayerCounts[gid];
                int palette = 0; // FIXME: support multiple palettes?

                for (int i = start; i < stop; ++i)
                {
                    ushort sub_gid = m_openfont.COLRTable.GlyphLayers[i];
                    // We do not need to provide advances since we only render one glyph.
                    GlyphRun r = new GlyphRun(m_gtf, 0, false, size,
                                              new ushort[] { sub_gid },
                                              origin, new double[] { 0 },
                                              null, null, null, // FIXME: check what this is?
                                              null, null, null);
                    int cid = m_openfont.CPALTable.Palettes[palette] + m_openfont.COLRTable.GlyphPalettes[i];
                    byte R, G, B, A;
                    m_openfont.CPALTable.GetColor(cid, out R, out G, out B, out A);
                    if (fallback_brush is SolidColorBrush brush)
                    {
                        R = (byte)(R + (255 - R) * brush.Color.R / 255);
                        G = (byte)(G + (255 - G) * brush.Color.G / 255);
                        B = (byte)(B + (255 - B) * brush.Color.B / 255);
                    }
                    Brush b = new SolidColorBrush(Color.FromArgb(A, R, G, B));

                    dc.DrawGlyphRun(b, r);
                }
            }
            else
            {
                GlyphRun r = new GlyphRun(m_gtf, 0, false, size,
                                          new ushort[] { gid },
                                          origin, new double[] { 0 },
                                          null, null, null,
                                          null, null, null);
                dc.DrawGlyphRun(fallback_brush, r);
            }
        }

        protected GlyphTypeface m_gtf;
        protected Typography.OpenFont.Typeface m_openfont;
        protected GlyphLayout m_layout;

        protected ushort m_zwj_glyph;
    }
}

