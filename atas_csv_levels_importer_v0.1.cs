// Version : atas_csv_levels_importer_v0.2_candidate
// Status  : candidate / experimental
// Objet   : lecture de niveaux depuis un CSV local et affichage dans ATAS
//
// CSV attendu :
// price,price2,note,color,lineType,lineWidth,textAlign
// 25000,,POC veille,gold,0,2,1
// 25120,25140,Zone volume,cyan,0,1,1
// 24950,,Support,green,0,2,1
//
// Dossier par défaut :
// C:\Users\phili\OneDrive\Traiding_PL\Codes\ATAS_lecture_csv

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
using OFT.Rendering.Settings;

namespace CustomIndicators
{
    [DisplayName("CSV Levels Importer Local")]
    public class CsvLevelsImporterLocal : Indicator
    {
        private readonly List<ImportedLevel> _levels = new();

        private DateTime _lastLoadTime = DateTime.MinValue;
        private bool _isLoading;

        private string _csvDirectory =
            @"C:\Users\phili\OneDrive\Traiding_PL\Codes\ATAS_lecture_csv";

        private string _csvFileName = "niveaux.csv";

        private int _recalcIntervalMinutes = 0;
        private int _transparency = 70;

        private bool _drawLinesOnChart = true;
        private bool _showPriceOnChart = false;
        private bool _showLabelsOnRight = true;

        private int _labelFontSize = 12;
        private int _labelXOffset = -80;
        private int _labelYOffset = -8;

        public CsvLevelsImporterLocal()
            : base(true)
        {
            Name = "CSV Levels Importer Local";

            EnableCustomDrawing = true;
            DrawAbovePrice = true;
            Panel = IndicatorDataProvider.CandlesPanel;

            SubscribeToDrawingEvents(DrawingLayouts.Final | DrawingLayouts.LatestBar);

            DataSeries[0].IsHidden = true;
        }

        [DisplayName("CSV Directory")]
        public string CsvDirectory
        {
            get => _csvDirectory;
            set
            {
                _csvDirectory = value;
                ForceReload();
            }
        }

        [DisplayName("CSV File Name")]
        public string CsvFileName
        {
            get => _csvFileName;
            set
            {
                _csvFileName = value;
                ForceReload();
            }
        }

        [DisplayName("Recalculation Interval Minutes")]
        public int RecalcIntervalMinutes
        {
            get => _recalcIntervalMinutes;
            set
            {
                _recalcIntervalMinutes = Math.Max(0, value);
                ForceReload();
            }
        }

        [DisplayName("Transparency")]
        public int Transparency
        {
            get => _transparency;
            set
            {
                _transparency = Math.Clamp(value, 0, 100);
                RedrawChart();
            }
        }

        [DisplayName("Draw Lines On Chart")]
        public bool DrawLinesOnChart
        {
            get => _drawLinesOnChart;
            set
            {
                _drawLinesOnChart = value;
                RedrawChart();
            }
        }

        [DisplayName("Show Price On Chart")]
        public bool ShowPriceOnChart
        {
            get => _showPriceOnChart;
            set
            {
                _showPriceOnChart = value;
                RedrawChart();
            }
        }

        [DisplayName("Show Labels On Right")]
        public bool ShowLabelsOnRight
        {
            get => _showLabelsOnRight;
            set
            {
                _showLabelsOnRight = value;
                RedrawChart();
            }
        }

        [DisplayName("Label Font Size")]
        public int LabelFontSize
        {
            get => _labelFontSize;
            set
            {
                _labelFontSize = Math.Max(6, value);
                RedrawChart();
            }
        }

        [DisplayName("Label X Offset")]
        public int LabelXOffset
        {
            get => _labelXOffset;
            set
            {
                _labelXOffset = value;
                RedrawChart();
            }
        }

        [DisplayName("Label Y Offset")]
        public int LabelYOffset
        {
            get => _labelYOffset;
            set
            {
                _labelYOffset = value;
                RedrawChart();
            }
        }

        protected override void OnCalculate(int bar, decimal value)
        {
            if (bar != CurrentBar - 1)
                return;

            if (_isLoading)
                return;

            if (_levels.Count == 0)
            {
                LoadLevelsFromFile();
                return;
            }

            if (_recalcIntervalMinutes <= 0)
                return;

            var elapsed = DateTime.Now - _lastLoadTime;

            if (elapsed.TotalMinutes >= _recalcIntervalMinutes)
                LoadLevelsFromFile();
        }

        protected override void OnRender(RenderContext context, DrawingLayouts layout)
        {
            if (!_drawLinesOnChart || ChartInfo == null || _levels.Count == 0)
                return;

            var chartRegion = ChartInfo.PriceChartContainer.Region;

            int x1 = chartRegion.Left;
            int x2 = chartRegion.Right;

            foreach (var level in _levels)
            {
                var color = ApplyTransparency(level.Color, _transparency);
                var pen = new RenderPen(color, level.LineWidth);

                if (level.Price2.HasValue && level.Price2.Value > 0)
                    DrawZone(context, level, color, pen, x1, x2);
                else
                    DrawHorizontalLine(context, level, pen, x1, x2);

                if (_showLabelsOnRight)
                    DrawLabel(context, level, color, x2);
            }
        }

        private void LoadLevelsFromFile()
        {
            try
            {
                _isLoading = true;

                string filePath = Path.Combine(_csvDirectory, _csvFileName);

                if (!File.Exists(filePath))
                    return;

                string csv = File.ReadAllText(filePath, Encoding.UTF8);

                var imported = ParseCsv(csv);

                _levels.Clear();
                _levels.AddRange(imported);

                _lastLoadTime = DateTime.Now;

                RedrawChart();
            }
            catch
            {
                // Version candidate :
                // ajouter ensuite un log fichier ou un message affiché sur le graphique.
            }
            finally
            {
                _isLoading = false;
            }
        }

        private void DrawHorizontalLine(
            RenderContext context,
            ImportedLevel level,
            RenderPen pen,
            int x1,
            int x2)
        {
            int y = ChartInfo.GetYByPrice(level.Price, false);

            context.DrawLine(pen, x1, y, x2, y);

            if (_showPriceOnChart)
            {
                string priceText = level.Price.ToString(CultureInfo.InvariantCulture);
                var font = new RenderFont("Arial", _labelFontSize);

                context.DrawString(
                    priceText,
                    font,
                    level.Color,
                    x1 + 5,
                    y + _labelYOffset);
            }
        }

        private void DrawZone(
            RenderContext context,
            ImportedLevel level,
            Color color,
            RenderPen pen,
            int x1,
            int x2)
        {
            decimal p1 = level.Price;
            decimal p2 = level.Price2!.Value;

            int y1 = ChartInfo.GetYByPrice(Math.Max(p1, p2), false);
            int y2 = ChartInfo.GetYByPrice(Math.Min(p1, p2), false);

            var rect = new Rectangle(
                x1,
                Math.Min(y1, y2),
                x2 - x1,
                Math.Abs(y2 - y1));

            context.FillRectangle(color, rect);
            context.DrawRectangle(pen, rect);
        }

        private void DrawLabel(
            RenderContext context,
            ImportedLevel level,
            Color color,
            int rightX)
        {
            string text = level.Note?.Replace("\\n", Environment.NewLine) ?? "";

            if (string.IsNullOrWhiteSpace(text))
                return;

            int y = ChartInfo.GetYByPrice(level.Price, false) + _labelYOffset;
            int x = rightX + _labelXOffset;

            var font = new RenderFont("Arial", _labelFontSize);

            context.DrawString(text, font, color, x, y);
        }

        private void ForceReload()
        {
            _levels.Clear();
            _lastLoadTime = DateTime.MinValue;

            RecalculateValues();
            RedrawChart();
        }

        private static List<ImportedLevel> ParseCsv(string csv)
        {
            var result = new List<ImportedLevel>();

            if (string.IsNullOrWhiteSpace(csv))
                return result;

            var lines = csv.Split(
                new[] { "\r\n", "\n" },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("#"))
                    continue;

                var fields = SplitCsvLine(line);

                if (fields.Count == 0)
                    continue;

                // Ignore l’en-tête : price,price2,note,...
                if (fields[0].Trim().Equals("price", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!TryParseDecimal(fields.ElementAtOrDefault(0), out var price))
                    continue;

                decimal? price2 = null;

                if (TryParseDecimal(fields.ElementAtOrDefault(1), out var p2) && p2 > 0)
                    price2 = p2;

                string note = fields.ElementAtOrDefault(2) ?? "";
                string colorName = fields.ElementAtOrDefault(3) ?? "white";

                int lineType = TryParseInt(fields.ElementAtOrDefault(4), 0);
                int lineWidth = TryParseInt(fields.ElementAtOrDefault(5), 1);
                int textAlign = TryParseInt(fields.ElementAtOrDefault(6), 1);

                result.Add(new ImportedLevel
                {
                    Price = price,
                    Price2 = price2,
                    Note = note,
                    Color = MapColor(colorName),
                    LineType = lineType,
                    LineWidth = Math.Max(1, lineWidth),
                    TextAlignment = textAlign
                });
            }

            return result;
        }

        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>();
            var current = new StringBuilder();

            bool inQuotes = false;

            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                    continue;
                }

                if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString().Trim());
                    current.Clear();
                    continue;
                }

                current.Append(c);
            }

            result.Add(current.ToString().Trim());

            return result;
        }

        private static bool TryParseDecimal(string? value, out decimal result)
        {
            result = 0;

            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim().Replace(",", ".");

            return decimal.TryParse(
                value,
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out result);
        }

        private static int TryParseInt(string? value, int defaultValue)
        {
            if (string.IsNullOrWhiteSpace(value))
                return defaultValue;

            if (int.TryParse(value, out int result))
                return result;

            return defaultValue;
        }

        private static Color MapColor(string color)
        {
            if (string.IsNullOrWhiteSpace(color))
                return Color.White;

            return color.Trim().ToLowerInvariant() switch
            {
                "red" => Color.Red,
                "green" => Color.Green,
                "blue" => Color.Blue,
                "white" => Color.White,
                "black" => Color.Black,
                "purple" => Color.Purple,
                "pink" => Color.Pink,
                "yellow" => Color.Yellow,
                "gold" => Color.Gold,
                "brown" => Color.Brown,
                "cyan" => Color.Cyan,
                "gray" => Color.Gray,
                "grey" => Color.Gray,
                "orange" => Color.Orange,
                "lime" => Color.Lime,
                "magenta" => Color.Magenta,
                _ => Color.White
            };
        }

        private static Color ApplyTransparency(Color color, int transparencyPercent)
        {
            transparencyPercent = Math.Clamp(transparencyPercent, 0, 100);

            int alpha = 255 - (int)(255 * transparencyPercent / 100.0);

            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        private sealed class ImportedLevel
        {
            public decimal Price { get; set; }
            public decimal? Price2 { get; set; }
            public string Note { get; set; } = "";
            public Color Color { get; set; } = Color.White;
            public int LineType { get; set; }
            public int LineWidth { get; set; } = 1;
            public int TextAlignment { get; set; }
        }
    }
}