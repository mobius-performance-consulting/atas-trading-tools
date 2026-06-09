// =============================================================================
// CsvLevelsImporter.cs
// =============================================================================
// Version : 1.0.1
// Status  : production
// Auteur  : Philippe L.
// Objet   : Lecture de niveaux / zones depuis un CSV local et tracé sur ATAS
//
// Améliorations v0.2.0 vs v0.1 :
//   - Correction détection séparateur CSV (auto ; ou ,)
//   - Correction décimales françaises (25030,5 → 25030.5)
//   - Implémentation LineType (0=solide, 1=tirets, 2=pointillés)
//   - Support couleurs hex (#RRGGBB) et nommées
//   - Thread-safety sur _levels avec lock
//   - FileSystemWatcher : rechargement automatique à la sauvegarde du CSV
//   - Gestion des erreurs : log dans fichier + message sur graphique
//   - Label zone positionné au centre vertical de la zone
//   - TextAlignment pris en compte (gauche/centre/droite)
//   - ShowPriceOnChart affiche les deux bornes pour les zones
//   - Suppression de la dépendance Class1.cs
//
// Format CSV attendu (séparateur ; ou ,) :
//   Price;Price2;Note;Color;LineType;LineWidth;TextAlign
//   25000;;POC veille;gold;0;2;1
//   25120;25140;Zone volume;cyan;0;1;1        ← zone si Price2 non vide
//   24950;;Support;#00FF88;1;2;0              ← tirets, couleur hex
//   # ligne commentée (ignorée)
//
// LineType :  0 = solide  |  1 = tirets  |  2 = pointillés
// TextAlign : 0 = gauche  |  1 = droite  |  2 = centre
// =============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using ATAS.Indicators;
using OFT.Rendering.Context;
using OFT.Rendering.Tools;

namespace CustomIndicators
{
    [DisplayName("CSV Levels Importer")]
    [Category("Custom")]
    public class CsvLevelsImporter : Indicator
    {
        // ── État interne ───────────────────────────────────────────────────
        private readonly List<ImportedLevel> _levels = new();
        private readonly object _levelsLock = new();

        private DateTime         _lastLoadTime   = DateTime.MinValue;
        private volatile bool    _isLoading;
        private FileSystemWatcher? _watcher;
        private string?          _lastError;

        // ── Paramètres utilisateur ────────────────────────────────────────
        private string _csvDirectory =
            @"C:\Users\phili\OneDrive\Traiding_PL\Codes\ATAS_lecture_csv";
        private string _csvFileName          = "niveaux.csv";
        private int    _recalcIntervalMinutes = 5;
        private bool   _useFileWatcher        = true;
        private int    _transparency          = 70;
        private bool   _drawLinesOnChart      = true;
        private bool   _showPriceOnChart      = false;
        private bool   _showLabelsOnRight     = true;
        private int    _labelFontSize         = 11;
        private int    _labelXOffset          = -80;
        private int    _labelYOffset          = -8;

        // ── Constructeur ──────────────────────────────────────────────────
        public CsvLevelsImporter() : base(true)
        {
            Name = "CSV Levels Importer";
            EnableCustomDrawing = true;
            DrawAbovePrice      = true;
            Panel               = IndicatorDataProvider.CandlesPanel;
            SubscribeToDrawingEvents(DrawingLayouts.Final | DrawingLayouts.LatestBar);
            DataSeries[0].IsHidden = true;
        }

        // ── Propriétés ────────────────────────────────────────────────────

        [DisplayName("Répertoire CSV")]
        public string CsvDirectory
        {
            get => _csvDirectory;
            set { _csvDirectory = value; ForceReload(); }
        }

        [DisplayName("Nom du fichier CSV")]
        public string CsvFileName
        {
            get => _csvFileName;
            set { _csvFileName = value; ForceReload(); }
        }

        [DisplayName("Intervalle de rechargement (minutes, 0=désactivé)")]
        public int RecalcIntervalMinutes
        {
            get => _recalcIntervalMinutes;
            set { _recalcIntervalMinutes = Math.Max(0, value); ForceReload(); }
        }

        [DisplayName("Rechargement automatique (FileWatcher)")]
        public bool UseFileWatcher
        {
            get => _useFileWatcher;
            set { _useFileWatcher = value; SetupFileWatcher(); }
        }

        [DisplayName("Transparence (0-100%)")]
        public int Transparency
        {
            get => _transparency;
            set { _transparency = Math.Clamp(value, 0, 100); RedrawChart(); }
        }

        [DisplayName("Afficher les lignes")]
        public bool DrawLinesOnChart
        {
            get => _drawLinesOnChart;
            set { _drawLinesOnChart = value; RedrawChart(); }
        }

        [DisplayName("Afficher le prix sur la ligne")]
        public bool ShowPriceOnChart
        {
            get => _showPriceOnChart;
            set { _showPriceOnChart = value; RedrawChart(); }
        }

        [DisplayName("Afficher les labels à droite")]
        public bool ShowLabelsOnRight
        {
            get => _showLabelsOnRight;
            set { _showLabelsOnRight = value; RedrawChart(); }
        }

        [DisplayName("Taille de la police (labels)")]
        public int LabelFontSize
        {
            get => _labelFontSize;
            set { _labelFontSize = Math.Max(6, value); RedrawChart(); }
        }

        [DisplayName("Décalage X du label (pixels)")]
        public int LabelXOffset
        {
            get => _labelXOffset;
            set { _labelXOffset = value; RedrawChart(); }
        }

        [DisplayName("Décalage Y du label (pixels)")]
        public int LabelYOffset
        {
            get => _labelYOffset;
            set { _labelYOffset = value; RedrawChart(); }
        }

        // ── Cycle de vie ──────────────────────────────────────────────────

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar != CurrentBar - 1) return;
            if (_isLoading) return;

            bool empty;
            lock (_levelsLock) { empty = _levels.Count == 0; }

            if (empty)
            {
                LoadLevelsFromFile();
                return;
            }

            if (_recalcIntervalMinutes > 0)
            {
                var elapsed = DateTime.Now - _lastLoadTime;
                if (elapsed.TotalMinutes >= _recalcIntervalMinutes)
                    LoadLevelsFromFile();
            }
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (!_drawLinesOnChart || ChartInfo == null) return;

            List<ImportedLevel> snapshot;
            lock (_levelsLock) { snapshot = new List<ImportedLevel>(_levels); }

            if (snapshot.Count == 0 && _lastError == null) return;

            var region = ChartInfo.PriceChartContainer.Region;
            int x1 = region.Left;
            int x2 = region.Right;

            foreach (var level in snapshot)
            {
                var drawColor = ApplyTransparency(level.Color, _transparency);
                var pen       = MakePen(drawColor, level.LineWidth, level.LineType);

                if (level.Price2.HasValue)
                    DrawZone(context, level, drawColor, pen, x1, x2);
                else
                    DrawHorizontalLine(context, level, drawColor, pen, x1, x2);

                if (_showLabelsOnRight && !string.IsNullOrWhiteSpace(level.Note))
                    DrawLabel(context, level, drawColor, x2);
            }

            // Afficher le message d'erreur éventuel
            if (_lastError != null)
            {
                var errFont = new RenderFont("Arial", 10);
                context.DrawString(
                    $"[CSV Levels] Erreur : {_lastError}",
                    errFont, Color.OrangeRed, region.Left + 4, region.Top + 4);
            }
        }

        protected override void OnDispose()
        {
            _watcher?.Dispose();
            _watcher = null;
            base.OnDispose();
        }

        // ── Chargement ────────────────────────────────────────────────────

        private void LoadLevelsFromFile()
        {
            _isLoading = true;
            _lastError = null;

            // ── Étape 1 : effacer immédiatement les anciens niveaux ───────────
            // On vide la liste et on force un rendu "à blanc" avant de lire le
            // nouveau fichier. Ainsi les niveaux supprimés du CSV disparaissent
            // du graphique sans attendre la fin du chargement.
            lock (_levelsLock) { _levels.Clear(); }
            RedrawChart();

            try
            {
                string filePath = Path.Combine(_csvDirectory, _csvFileName);

                if (!File.Exists(filePath))
                {
                    _lastError = $"Fichier introuvable : {filePath}";
                    RedrawChart();   // afficher le message d'erreur
                    return;
                }

                // ── Étape 2 : lecture avec retry (fichier peut être verrouillé)
                string csv      = ReadFileWithRetry(filePath);
                var    imported = ParseCsv(csv);

                // ── Étape 3 : charger les nouveaux niveaux et redessiner ──────
                lock (_levelsLock)
                {
                    _levels.Clear();           // sécurité : vider à nouveau sous lock
                    _levels.AddRange(imported);
                }

                _lastLoadTime = DateTime.Now;
                RedrawChart();

                SetupFileWatcher();   // (re)armer le watcher si besoin
            }
            catch (Exception ex)
            {
                _lastError = ex.Message.Length > 80
                    ? ex.Message[..80] + "..."
                    : ex.Message;

                LogError(ex);
                RedrawChart();   // afficher le message d'erreur sur le graphique
            }
            finally
            {
                _isLoading = false;
            }
        }

        private static string ReadFileWithRetry(string path, int maxAttempts = 3)
        {
            for (int i = 0; i < maxAttempts; i++)
            {
                try
                {
                    using var fs = new FileStream(
                        path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    return sr.ReadToEnd();
                }
                catch (IOException) when (i < maxAttempts - 1)
                {
                    System.Threading.Thread.Sleep(200);
                }
            }
            return File.ReadAllText(path, Encoding.UTF8);
        }

        private void SetupFileWatcher()
        {
            _watcher?.Dispose();
            _watcher = null;

            if (!_useFileWatcher || !Directory.Exists(_csvDirectory))
                return;

            try
            {
                _watcher = new FileSystemWatcher(_csvDirectory, _csvFileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += OnFileChanged;
            }
            catch
            {
                // FileSystemWatcher non critique — échec silencieux
            }
        }

        private void OnFileChanged(object sender, FileSystemEventArgs e)
        {
            if (!_isLoading)
                LoadLevelsFromFile();
        }

        private void ForceReload()
        {
            lock (_levelsLock) { _levels.Clear(); }
            _lastLoadTime = DateTime.MinValue;
            _lastError    = null;
            RecalculateValues();
            RedrawChart();
        }

        // ── Dessin ────────────────────────────────────────────────────────

        private void DrawHorizontalLine(
            RenderContext context, ImportedLevel level,
            Color color, RenderPen pen, int x1, int x2)
        {
            int y = ChartInfo!.GetYByPrice(level.Price, false);
            context.DrawLine(pen, x1, y, x2, y);

            if (_showPriceOnChart)
            {
                var font = new RenderFont("Arial", _labelFontSize);
                string txt = level.Price.ToString("F2", CultureInfo.InvariantCulture);
                context.DrawString(txt, font, color, x1 + 4, y + _labelYOffset);
            }
        }

        private void DrawZone(
            RenderContext context, ImportedLevel level,
            Color color, RenderPen pen, int x1, int x2)
        {
            decimal hi = Math.Max(level.Price, level.Price2!.Value);
            decimal lo = Math.Min(level.Price, level.Price2!.Value);

            int yTop    = ChartInfo!.GetYByPrice(hi, false);
            int yBottom = ChartInfo!.GetYByPrice(lo, false);

            int yMin = Math.Min(yTop, yBottom);
            int yMax = Math.Max(yTop, yBottom);
            int h    = Math.Max(1, yMax - yMin);

            var rect = new Rectangle(x1, yMin, x2 - x1, h);
            context.FillRectangle(color, rect);
            context.DrawRectangle(pen, rect);

            if (_showPriceOnChart)
            {
                var font = new RenderFont("Arial", _labelFontSize);
                context.DrawString(hi.ToString("F2", CultureInfo.InvariantCulture),
                    font, color, x1 + 4, yMin + _labelYOffset);
                context.DrawString(lo.ToString("F2", CultureInfo.InvariantCulture),
                    font, color, x1 + 4, yMax + _labelYOffset);
            }
        }

        private void DrawLabel(
            RenderContext context, ImportedLevel level, Color color, int rightX)
        {
            string text = level.Note.Replace("\\n", "\n");
            if (string.IsNullOrWhiteSpace(text)) return;

            var font = new RenderFont("Arial", _labelFontSize);

            // Y centré sur la zone, ou sur la ligne
            int yCenter;
            if (level.Price2.HasValue)
            {
                int yTop    = ChartInfo!.GetYByPrice(
                    Math.Max(level.Price, level.Price2.Value), false);
                int yBottom = ChartInfo!.GetYByPrice(
                    Math.Min(level.Price, level.Price2.Value), false);
                yCenter = (Math.Min(yTop, yBottom) + Math.Max(yTop, yBottom)) / 2;
            }
            else
            {
                yCenter = ChartInfo!.GetYByPrice(level.Price, false);
            }

            int x = level.TextAlignment switch
            {
                0 => rightX - 200 + _labelXOffset,   // gauche (décalé)
                2 => rightX - 100 + _labelXOffset,   // centre
                _ => rightX + _labelXOffset           // droite (défaut)
            };

            context.DrawString(text, font, color, x, yCenter + _labelYOffset);
        }

        // ── Parsing CSV ───────────────────────────────────────────────────

        private static List<ImportedLevel> ParseCsv(string csv)
        {
            var result = new List<ImportedLevel>();
            if (string.IsNullOrWhiteSpace(csv)) return result;

            var lines = csv.Split(new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);

            // Détection automatique du séparateur sur la première ligne de données
            char sep = DetectSeparator(lines);

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                var fields = SplitLine(line, sep);
                if (fields.Count == 0) continue;

                // Ignorer la ligne d'en-tête
                if (fields[0].Trim().Equals("price", StringComparison.OrdinalIgnoreCase) ||
                    fields[0].Trim().Equals("prix",  StringComparison.OrdinalIgnoreCase))
                    continue;

                // Price obligatoire
                if (!TryParseDecimal(fields.ElementAtOrDefault(0), out decimal price))
                    continue;

                // Price2 optionnel (zone si présent)
                decimal? price2 = null;
                if (TryParseDecimal(fields.ElementAtOrDefault(1), out decimal p2) && p2 > 0)
                    price2 = p2;

                string note      = UnquoteField(fields.ElementAtOrDefault(2) ?? "");
                string colorName = UnquoteField(fields.ElementAtOrDefault(3) ?? "white");
                int lineType     = ParseInt(fields.ElementAtOrDefault(4), 0);
                int lineWidth    = Math.Max(1, ParseInt(fields.ElementAtOrDefault(5), 1));
                int textAlign    = ParseInt(fields.ElementAtOrDefault(6), 1);

                result.Add(new ImportedLevel
                {
                    Price         = price,
                    Price2        = price2,
                    Note          = note,
                    Color         = ParseColor(colorName),
                    LineType      = lineType,
                    LineWidth     = lineWidth,
                    TextAlignment = textAlign,
                });
            }

            return result;
        }

        private static char DetectSeparator(string[] lines)
        {
            foreach (var line in lines)
            {
                string t = line.Trim();
                if (string.IsNullOrEmpty(t) || t.StartsWith("#")) continue;
                int semis  = t.Count(c => c == ';');
                int commas = t.Count(c => c == ',');
                // Si plus de ';' que de ',' → séparateur ';'
                if (semis > commas) return ';';
                if (commas > semis) return ',';
            }
            return ';'; // défaut
        }

        private static List<string> SplitLine(string line, char sep)
        {
            var result  = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            foreach (char c in line)
            {
                if (c == '"')        { inQuotes = !inQuotes; continue; }
                if (c == sep && !inQuotes)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            result.Add(current.ToString().Trim());
            return result;
        }

        private static string UnquoteField(string s)
            => s.Trim().Trim('"').Trim('\'');

        private static bool TryParseDecimal(string? value, out decimal result)
        {
            result = 0;
            if (string.IsNullOrWhiteSpace(value)) return false;

            // Normaliser décimale : remplacer ',' par '.' SAUF si c'est un
            // séparateur de milliers (ex: 1,000,000 → on garde l'invariant)
            string v = value.Trim();

            // Cas français : "25030,5" ou "25030,25" → remplacer ',' par '.'
            // seulement si la virgule est suivie de 1 ou 2 chiffres en fin de chaîne
            v = System.Text.RegularExpressions.Regex.Replace(
                v, @",(\d{1,4})$", ".$1");

            // Supprimer les espaces insécables éventuels
            v = v.Replace(" ", "").Replace(" ", "");

            return decimal.TryParse(v, NumberStyles.Any,
                CultureInfo.InvariantCulture, out result);
        }

        private static int ParseInt(string? value, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value)) return defaultValue;
            return int.TryParse(value.Trim(), out int r) ? r : defaultValue;
        }

        // ── Couleurs ──────────────────────────────────────────────────────

        private static Color ParseColor(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return Color.White;
            string s = raw.Trim();

            // Hex #RRGGBB ou #AARRGGBB
            if (s.StartsWith("#"))
            {
                try
                {
                    s = s.TrimStart('#');
                    if (s.Length == 6)
                    {
                        int r = Convert.ToInt32(s[0..2], 16);
                        int g = Convert.ToInt32(s[2..4], 16);
                        int b = Convert.ToInt32(s[4..6], 16);
                        return Color.FromArgb(r, g, b);
                    }
                    if (s.Length == 8)
                    {
                        int a = Convert.ToInt32(s[0..2], 16);
                        int r = Convert.ToInt32(s[2..4], 16);
                        int g = Convert.ToInt32(s[4..6], 16);
                        int b = Convert.ToInt32(s[6..8], 16);
                        return Color.FromArgb(a, r, g, b);
                    }
                }
                catch { /* fallback */ }
            }

            // Noms courants (insensible à la casse)
            return s.ToLowerInvariant() switch
            {
                "red"       => Color.Red,
                "green"     => Color.LimeGreen,
                "blue"      => Color.RoyalBlue,
                "white"     => Color.White,
                "black"     => Color.Black,
                "purple"    => Color.MediumPurple,
                "pink"      => Color.HotPink,
                "yellow"    => Color.Yellow,
                "gold"      => Color.Gold,
                "brown"     => Color.SaddleBrown,
                "cyan"      => Color.Cyan,
                "teal"      => Color.Teal,
                "gray"      => Color.Gray,
                "grey"      => Color.Gray,
                "orange"    => Color.Orange,
                "lime"      => Color.Lime,
                "magenta"   => Color.Magenta,
                "salmon"    => Color.Salmon,
                "turquoise" => Color.Turquoise,
                "violet"    => Color.Violet,
                "silver"    => Color.Silver,
                // Fallback : essai via KnownColor
                _ => TryKnownColor(s),
            };
        }

        private static Color TryKnownColor(string name)
        {
            try
            {
                var c = Color.FromName(name);
                return c.IsKnownColor ? c : Color.White;
            }
            catch { return Color.White; }
        }

        // ── Stylo (ligne solide / tirets / pointillés) ────────────────────

        private static RenderPen MakePen(Color color, int width, int lineType)
        {
            // lineType : 0=solide, 1=tirets, 2=pointillés
            // RenderPen(Color, float, DashStyle) si disponible dans l'API ATAS
            // Fallback : RenderPen(Color, float) si la surcharge n'existe pas
            try
            {
                var dash = lineType switch
                {
                    1 => DashStyle.Dash,
                    2 => DashStyle.Dot,
                    _ => DashStyle.Solid,
                };
                return new RenderPen(color, width, dash);
            }
            catch
            {
                return new RenderPen(color, width);
            }
        }

        // ── Transparence ──────────────────────────────────────────────────

        private static Color ApplyTransparency(Color color, int transparencyPercent)
        {
            int alpha = 255 - (int)(255 * Math.Clamp(transparencyPercent, 0, 100) / 100.0);
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        // ── Log d'erreurs ─────────────────────────────────────────────────

        private void LogError(Exception ex)
        {
            try
            {
                string logPath = Path.Combine(_csvDirectory, "csv_levels_errors.log");
                string entry   = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n";
                File.AppendAllText(logPath, entry, Encoding.UTF8);
            }
            catch { /* log non critique */ }
        }

        // ── Modèle de données ─────────────────────────────────────────────

        private sealed class ImportedLevel
        {
            public decimal  Price         { get; set; }
            public decimal? Price2        { get; set; }
            public string   Note          { get; set; } = "";
            public Color    Color         { get; set; } = Color.White;
            public int      LineType      { get; set; } = 0;
            public int      LineWidth     { get; set; } = 1;
            public int      TextAlignment { get; set; } = 1;
        }
    }
}
