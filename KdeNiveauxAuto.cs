// =============================================================================
// KdeNiveauxAuto.cs
// =============================================================================
// Version : 1.0.3
// Auteur  : Philippe L.
// Objet   : Calcul KDE Bid/Ask/Volume + affichage des niveaux directement
//           dans ATAS. Calcul unique au chargement de l'indicateur.
//
// Parametres :
//   K Days             : nb de journees d'entrainement (defaut 3)
//   Tick Size          : pas de cotation (defaut 0.25)
//   KDE Bandwidth Ticks: largeur bande KDE en ticks (defaut 60)
//   Zone Ticks         : distance max fusion en zone (defaut 30)
//   Archive CSV        : ecriture Chart.csv + niveaux.csv (defaut true)
//   Repertoire archive : chemin des fichiers CSV
//
// Logique KDE :
//   Bid    : LOW  pondere par Bid volume
//   Ask    : HIGH pondere par Ask volume
//   Volume : HL combine pondere par Volume total
//   Zones  : confluence multi-type (>= 2 types proches)
// =============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace CustomIndicators
{
    [DisplayName("KDE Niveaux Auto")]
    [Category("Custom")]
    public class KdeNiveauxAuto : Indicator
    {
        // ── Parametres ────────────────────────────────────────────────────

        private int    _kDays             = 3;
        private float  _kdePriceStep      = 0.25f;
        private float  _kdeBandwidthTicks = 60f;
        private int    _zoneTicks         = 30;
        private int    _maxNiveaux        = 20;
        private float  _minDensityPct     = 10f;
        private bool   _saveArchive       = true;
        private string _archiveDir        =
            @"C:\Users\phili\OneDrive\Traiding_PL\Codes\ATAS_lecture_csv";
        private int    _transparency      = 70;
        private int    _labelFontSize     = 11;
        private bool   _showLabels        = true;
        private int    _labelXOffset      = -80;
        private int    _labelYOffsetTicks = 0;

        // ── Etat interne ──────────────────────────────────────────────────

        private readonly List<KdeLevel> _levels = new();
        private readonly object _lock = new();
        private bool _levelsComputed = false;
        private string? _statusMsg = null;

        // ── Constructeur ──────────────────────────────────────────────────

        public KdeNiveauxAuto() : base(true)
        {
            Name = "KDE Niveaux Auto";
            EnableCustomDrawing = true;
            DrawAbovePrice      = true;
            Panel               = IndicatorDataProvider.CandlesPanel;
            SubscribeToDrawingEvents(DrawingLayouts.Final | DrawingLayouts.LatestBar);
            DataSeries[0].IsHidden = true;
        }

        // ── Proprietes ────────────────────────────────────────────────────

        [DisplayName("Nombre de journees (K Days)")]
        public int KDays
        {
            get => _kDays;
            set { _kDays = Math.Max(1, value); Reset(); }
        }

        [DisplayName("Tick Size (pas de cotation)")]
        public float KdePriceStep
        {
            get => _kdePriceStep;
            set { _kdePriceStep = Math.Max(0.01f, value); Reset(); }
        }

        [DisplayName("KDE Bandwidth (ticks)")]
        public float KdeBandwidthTicks
        {
            get => _kdeBandwidthTicks;
            set { _kdeBandwidthTicks = Math.Max(1f, value); Reset(); }
        }

        [DisplayName("Zone Ticks (fusion confluence)")]
        public int ZoneTicks
        {
            get => _zoneTicks;
            set { _zoneTicks = Math.Max(1, value); Reset(); }
        }

        [DisplayName("Nombre max de niveaux")]
        public int MaxNiveaux
        {
            get => _maxNiveaux;
            set { _maxNiveaux = Math.Max(1, value); Reset(); }
        }

        [DisplayName("Densité min (% du pic max)")]
        public float MinDensityPct
        {
            get => _minDensityPct;
            set { _minDensityPct = Math.Clamp(value, 0f, 100f); Reset(); }
        }

        [DisplayName("Sauvegarder CSV (archive)")]
        public bool SaveArchive
        {
            get => _saveArchive;
            set => _saveArchive = value;
        }

        [DisplayName("Repertoire archive")]
        public string ArchiveDir
        {
            get => _archiveDir;
            set => _archiveDir = value;
        }

        [DisplayName("Transparence (0-100%)")]
        public int Transparency
        {
            get => _transparency;
            set { _transparency = Math.Clamp(value, 0, 100); RedrawChart(); }
        }

        [DisplayName("Afficher les labels")]
        public bool ShowLabels
        {
            get => _showLabels;
            set { _showLabels = value; RedrawChart(); }
        }

        [DisplayName("Taille police labels")]
        public int LabelFontSize
        {
            get => _labelFontSize;
            set { _labelFontSize = Math.Max(6, value); RedrawChart(); }
        }

        [DisplayName("Decalage X label (pixels)")]
        public int LabelXOffset
        {
            get => _labelXOffset;
            set { _labelXOffset = value; RedrawChart(); }
        }

        [DisplayName("Decalage Y label (ticks, + haut / - bas)")]
        public int LabelYOffsetTicks
        {
            get => _labelYOffsetTicks;
            set { _labelYOffsetTicks = value; RedrawChart(); }
        }

        // ── Cycle de vie ──────────────────────────────────────────────────

        protected override void OnCalculate(int bar, decimal value)
        {
            // Calcul unique sur la derniere barre, au premier chargement
            if (_levelsComputed) return;
            if (bar != CurrentBar - 1) return;

            ComputeKde();
            _levelsComputed = true;
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (ChartInfo == null) return;

            List<KdeLevel> snapshot;
            lock (_lock) { snapshot = new List<KdeLevel>(_levels); }

            var region = ChartInfo.PriceChartContainer.Region;
            int x1 = region.Left;
            int x2 = region.Right;

            foreach (var level in snapshot)
            {
                var color = ApplyTransparency(level.Color, _transparency);
                var pen   = new RenderPen(color, 2);

                if (level.Price2.HasValue)
                    DrawZone(context, level, color, pen, x1, x2);
                else
                    DrawLine(context, level, color, pen, x1, x2);

                if (_showLabels)
                    DrawLabel(context, level, color, x2);
            }
        }

        protected override void OnDispose()
        {
            base.OnDispose();
        }

        // ── Reset ─────────────────────────────────────────────────────────

        private void Reset()
        {
            _levelsComputed = false;
            // Effacer immédiatement les anciens niveaux et forcer un rendu vide
            // avant de relancer le calcul KDE (même logique que CsvLevelsImporter)
            lock (_lock) { _levels.Clear(); }
            RedrawChart();
            RecalculateValues();
        }

        // ── Calcul KDE ────────────────────────────────────────────────────

        private void ComputeKde()
        {
            _statusMsg = "KDE : calcul en cours...";
            RedrawChart();

            try
            {
                int total = CurrentBar;
                if (total == 0) { _statusMsg = "KDE : aucune bougie."; return; }

                // 1. Grouper les bougies par session (date)
                var byDay = new SortedDictionary<DateTime, List<CandleData>>();

                for (int i = 0; i < total; i++)
                {
                    var c = GetCandle(i);
                    if (c == null) continue;
                    var day = c.Time.ToLocalTime().Date;
                    if (!byDay.ContainsKey(day)) byDay[day] = new List<CandleData>();
                    byDay[day].Add(new CandleData
                    {
                        High   = (double)c.High,
                        Low    = (double)c.Low,
                        Volume = (double)c.Volume,
                        Bid    = (double)c.Bid,
                        Ask    = (double)c.Ask,
                    });
                }

                var days = byDay.Keys.ToList();
                if (days.Count == 0) { _statusMsg = "KDE : pas de sessions."; return; }

                // 2. Fenetre d'entrainement : J-k .. J-1 (exclut le jour courant)
                var trainDays = days.Count <= _kDays
                    ? days.ToHashSet()
                    : days.Skip(Math.Max(0, days.Count - _kDays - 1)).Take(_kDays).ToHashSet();

                var trainCandles = byDay
                    .Where(kv => trainDays.Contains(kv.Key))
                    .SelectMany(kv => kv.Value)
                    .ToList();

                if (trainCandles.Count == 0) { _statusMsg = "KDE : pas de bougies d'entrainement."; return; }

                // 3. KDE
                double bw = _kdeBandwidthTicks * _kdePriceStep;
                var levels = new Dictionary<string, List<double>>
                {
                    ["volume"] = KdePeaks(
                        trainCandles.SelectMany(c => new[] { c.High, c.Low }).ToList(),
                        trainCandles.SelectMany(c => new[] { c.Volume, c.Volume }).ToList(),
                        bw),
                    ["bid"] = KdePeaks(
                        trainCandles.Select(c => c.Low).ToList(),
                        trainCandles.Select(c => c.Bid).ToList(),
                        bw),
                    ["ask"] = KdePeaks(
                        trainCandles.Select(c => c.High).ToList(),
                        trainCandles.Select(c => c.Ask).ToList(),
                        bw),
                };

                // 4. Clustering + build niveaux
                var clusters = BuildClusters(levels, _kdePriceStep, _zoneTicks);
                var newLevels = ClustersToLevels(clusters);

                lock (_lock)
                {
                    _levels.Clear();
                    _levels.AddRange(newLevels);
                }

                // 5. Archive CSV
                if (_saveArchive)
                {
                    SaveChartCsv(total);
                    SaveNiveauxCsv(newLevels);
                }

                string trainRange = $"{trainDays.Min():dd/MM} - {trainDays.Max():dd/MM}";
                _statusMsg = $"KDE OK  |  {trainDays.Count}j ({trainRange})  |  {newLevels.Count} niveaux";
            }
            catch (Exception ex)
            {
                _statusMsg = $"KDE erreur : {ex.Message[..Math.Min(80, ex.Message.Length)]}";
                LogError(ex);
            }

            RedrawChart();
        }

        // ── Algorithme KDE (pur C#) ───────────────────────────────────────

        private List<double> KdePeaks(List<double> prices, List<double> weights, double bandwidth)
        {
            if (prices.Count == 0) return new List<double>();

            int gridSize = 1000;
            double pMin = prices.Min();
            double pMax = prices.Max();
            if (pMin >= pMax) return new List<double> { pMin };

            double step = (pMax - pMin) / (gridSize - 1);
            var grid    = Enumerable.Range(0, gridSize).Select(i => pMin + i * step).ToList();

            double totalW = weights.Sum();
            if (totalW <= 0) return new List<double>();
            var normW = weights.Select(w => w / totalW).ToList();

            var density = grid.Select(gp =>
                prices.Zip(normW, (px, w) =>
                    w * Math.Exp(-0.5 * Math.Pow((gp - px) / bandwidth, 2))
                ).Sum()
            ).ToList();

            // Maxima locaux
            var peakIdxs = new List<int>();
            for (int i = 1; i < density.Count - 1; i++)
                if (density[i] > density[i - 1] && density[i] >= density[i + 1])
                    peakIdxs.Add(i);

            if (peakIdxs.Count == 0) return new List<double>();

            // Filtre 1 : densité minimum (% du pic le plus haut)
            double maxDensity = peakIdxs.Max(i => density[i]);
            double threshold  = maxDensity * (_minDensityPct / 100.0);
            var filtered = peakIdxs
                .Where(i => density[i] >= threshold)
                .OrderByDescending(i => density[i])
                .ToList();

            // Filtre 2 : top N niveaux par densité
            var topN = filtered.Take(_maxNiveaux).Select(i => grid[i]).ToList();

            return topN;
        }

        // ── Clustering ────────────────────────────────────────────────────

        private record PricePoint(double Price, string Kind);

        private List<(double PLo, double PHi, HashSet<string> Types)> BuildClusters(
            Dictionary<string, List<double>> levels, float tickSize, int zoneTicks)
        {
            double gap = tickSize * zoneTicks;
            var all = levels
                .SelectMany(kv => kv.Value.Select(p => new PricePoint(p, kv.Key)))
                .OrderBy(p => p.Price)
                .ToList();

            if (all.Count == 0) return new();

            var clusters = new List<(double PLo, double PHi, HashSet<string> Types)>();
            var curPrices = new List<double> { all[0].Price };
            var curTypes  = new HashSet<string> { all[0].Kind };

            foreach (var pt in all.Skip(1))
            {
                if (pt.Price - curPrices.Last() <= gap)
                {
                    curPrices.Add(pt.Price);
                    curTypes.Add(pt.Kind);
                }
                else
                {
                    clusters.Add((curPrices.Min(), curPrices.Max(), curTypes));
                    curPrices = new List<double> { pt.Price };
                    curTypes  = new HashSet<string> { pt.Kind };
                }
            }
            clusters.Add((curPrices.Min(), curPrices.Max(), curTypes));
            return clusters;
        }

        private List<KdeLevel> ClustersToLevels(
            List<(double PLo, double PHi, HashSet<string> Types)> clusters)
        {
            var result = new List<KdeLevel>();
            int i = 1;
            foreach (var (pLo, pHi, types) in clusters)
            {
                bool isZone = types.Count >= 2;
                if (isZone)
                {
                    string label = string.Join("+", types.OrderBy(t => t)
                        .Select(t => t == "volume" ? "VOL" : t.ToUpper()));
                    result.Add(new KdeLevel
                    {
                        Price  = (decimal)Math.Round(pLo, 4),
                        Price2 = (decimal)Math.Round(pHi, 4),
                        Note   = $"Zone {label} #{i}",
                        Color  = Color.Gold,
                    });
                }
                else
                {
                    string kind = types.First();
                    result.Add(new KdeLevel
                    {
                        Price  = (decimal)Math.Round(pLo, 4),
                        Price2 = null,
                        Note   = $"{(kind == "volume" ? "VOL" : kind.ToUpper())} N{i}",
                        Color  = kind switch
                        {
                            "bid"    => Color.LimeGreen,
                            "ask"    => Color.Tomato,
                            _        => Color.Cyan,
                        },
                    });
                }
                i++;
            }
            return result;
        }

        // ── Dessin ────────────────────────────────────────────────────────

        private void DrawLine(RenderContext ctx, KdeLevel level, Color color, RenderPen pen, int x1, int x2)
        {
            int y = ChartInfo!.GetYByPrice(level.Price, false);
            ctx.DrawLine(pen, x1, y, x2, y);
        }

        private void DrawZone(RenderContext ctx, KdeLevel level, Color color, RenderPen pen, int x1, int x2)
        {
            decimal hi = Math.Max(level.Price, level.Price2!.Value);
            decimal lo = Math.Min(level.Price, level.Price2!.Value);
            int yTop    = ChartInfo!.GetYByPrice(hi, false);
            int yBottom = ChartInfo!.GetYByPrice(lo, false);
            int yMin = Math.Min(yTop, yBottom);
            int h    = Math.Max(1, Math.Abs(yTop - yBottom));
            var rect = new Rectangle(x1, yMin, x2 - x1, h);
            ctx.FillRectangle(color, rect);
            ctx.DrawRectangle(pen, rect);
        }

        private void DrawLabel(RenderContext ctx, KdeLevel level, Color color, int rightX)
        {
            if (string.IsNullOrWhiteSpace(level.Note)) return;
            var font = new RenderFont("Arial", _labelFontSize);

            // Position Y de base : milieu de la zone ou niveau
            int y;
            if (level.Price2.HasValue)
            {
                int yTop    = ChartInfo!.GetYByPrice(Math.Max(level.Price, level.Price2.Value), false);
                int yBottom = ChartInfo!.GetYByPrice(Math.Min(level.Price, level.Price2.Value), false);
                y = (Math.Min(yTop, yBottom) + Math.Max(yTop, yBottom)) / 2;
            }
            else
            {
                y = ChartInfo!.GetYByPrice(level.Price, false);
            }

            // Decalage Y en ticks (+ = vers le haut, - = vers le bas)
            // GetYByPrice : prix plus haut = pixel plus petit (Y croit vers le bas)
            if (_labelYOffsetTicks != 0)
            {
                decimal refPrice    = level.Price;
                decimal offsetPrice = refPrice + (decimal)_labelYOffsetTicks * (decimal)_kdePriceStep;
                int yRef    = ChartInfo!.GetYByPrice(refPrice, false);
                int yOff    = ChartInfo!.GetYByPrice(offsetPrice, false);
                y += yOff - yRef;   // negatif si tick>0 (monte), positif si tick<0 (descend)
            }

            ctx.DrawString(level.Note, font, color, rightX + _labelXOffset, y - _labelFontSize / 2);
        }

        // ── Archive CSV ───────────────────────────────────────────────────

        private void SaveChartCsv(int total)
        {
            try
            {
                string path = Path.Combine(_archiveDir, "Chart.csv");
                var sb = new StringBuilder(total * 60);
                sb.AppendLine("DateTime;Open;High;Low;Close;Volume;Bid;Ask;Delta");
                for (int i = 0; i < total; i++)
                {
                    var c = GetCandle(i);
                    if (c == null) continue;
                    sb.AppendLine(string.Join(";",
                        c.Time.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                        F(c.Open), F(c.High), F(c.Low), F(c.Close),
                        F(c.Volume), F(c.Bid), F(c.Ask), F(c.Delta)));
                }
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { LogError(ex); }
        }

        private void SaveNiveauxCsv(List<KdeLevel> levels)
        {
            try
            {
                string path = Path.Combine(_archiveDir, "niveaux.csv");
                var sb = new StringBuilder();
                sb.AppendLine("Price;Price2;Note;Color;Line Type;Line Width;Text Alignment");
                foreach (var l in levels)
                {
                    string colorName = ColorToName(l.Color);
                    string p2 = l.Price2.HasValue ? l.Price2.Value.ToString(CultureInfo.InvariantCulture) : "";
                    sb.AppendLine(string.Join(";",
                        l.Price.ToString(CultureInfo.InvariantCulture),
                        p2, l.Note, colorName,
                        l.Price2.HasValue ? "0" : "1", "2", "1"));
                }
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { LogError(ex); }
        }

        // ── Helpers ───────────────────────────────────────────────────────

        private static Color ApplyTransparency(Color c, int pct)
        {
            int alpha = 255 - (int)(255 * Math.Clamp(pct, 0, 100) / 100.0);
            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }

        private static string F(decimal v) => v.ToString(CultureInfo.InvariantCulture);

        private static string ColorToName(Color c)
        {
            if (c == Color.Gold)      return "yellow";
            if (c == Color.LimeGreen) return "green";
            if (c == Color.Tomato)    return "red";
            return "cyan";
        }

        private void LogError(Exception ex)
        {
            try
            {
                string log = Path.Combine(_archiveDir, "kde_errors.log");
                File.AppendAllText(log,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n", Encoding.UTF8);
            }
            catch { }
        }

        // ── Modele de donnees ─────────────────────────────────────────────

        private sealed class CandleData
        {
            public double High, Low, Volume, Bid, Ask;
        }

        private sealed class KdeLevel
        {
            public decimal  Price  { get; set; }
            public decimal? Price2 { get; set; }
            public string   Note   { get; set; } = "";
            public Color    Color  { get; set; } = Color.Cyan;
        }
    }
}
