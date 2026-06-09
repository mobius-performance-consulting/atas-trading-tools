// =============================================================================
// OhlcExporter.cs
// =============================================================================
// Version : 1.2.0
// Auteur  : Philippe L.
// Objet   : Export OHLC + Volume + Bid + Ask de toutes les bougies du graphique
//           vers un fichier CSV.
//
// Colonnes : DateTime;Open;High;Low;Close;Volume;Bid;Ask;Delta;Timeframe
//
// Declencheurs :
//   - Chargement initial             → export automatique
//   - Bouton "Recalculer" dans ATAS  → export automatique
//   - Toggle "Exporter maintenant"   → export manuel a la demande
//   - "Exporter a chaque bougie"     → export continu optionnel
//
// Timeframe :
//   - Detecte automatiquement depuis les ecarts entre bougies consecutives
//   - Le format datetime inclut les secondes si timeframe < 60s
//   - Le timeframe detecte est ecrit en entete du CSV (# Timeframe: 30s)
// =============================================================================

using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;

using ATAS.Indicators;

namespace CustomIndicators
{
    [DisplayName("OHLC + Volume + Bid/Ask Exporter")]
    [Category("Custom")]
    public class OhlcExporter : Indicator
    {
        // ── Parametres ────────────────────────────────────────────────────

        private string _outputDirectory =
            @"C:\Users\phili\OneDrive\Traiding_PL\Codes\ATAS_lecture_csv";
        private string _outputFileName  = "Chart.csv";
        private bool   _exportNow       = false;
        private bool   _exportOnEachBar = false;
        private char   _separator       = ';';

        // ── Etat interne ──────────────────────────────────────────────────

        // true = exporter au prochain bar==0 (recalcul complet).
        // Initialise a true → export automatique au premier chargement.
        private bool _exportArmed = true;

        // ── Constructeur ──────────────────────────────────────────────────

        public OhlcExporter() : base(false)
        {
            Name = "OHLC Exporter";
            DataSeries[0].IsHidden = true;
        }

        // ── Proprietes ────────────────────────────────────────────────────

        [DisplayName("Repertoire de sortie")]
        public string OutputDirectory
        {
            get => _outputDirectory;
            set => _outputDirectory = value;
        }

        [DisplayName("Nom du fichier CSV")]
        public string OutputFileName
        {
            get => _outputFileName;
            set => _outputFileName = value;
        }

        [DisplayName("Separateur (; ou ,)")]
        public string Separator
        {
            get => _separator.ToString();
            set => _separator = value.Length > 0 ? value[0] : ';';
        }

        [DisplayName("Exporter maintenant (toggle manuel)")]
        public bool ExportNow
        {
            get => _exportNow;
            set
            {
                _exportNow = value;
                if (value)
                    RecalculateValues();
            }
        }

        [DisplayName("Exporter a chaque nouvelle bougie fermee")]
        public bool ExportOnEachBar
        {
            get => _exportOnEachBar;
            set => _exportOnEachBar = value;
        }

        // ── Calcul ────────────────────────────────────────────────────────

        protected override void OnCalculate(int bar, decimal value)
        {
            // bar == 0 = debut d'un recalcul complet
            // (chargement initial OU clic "Recalculer" dans ATAS).
            // On exporte ICI : GetCandle(i) donne acces a tout l'historique
            // des maintenant, sans attendre la derniere bougie.
            // → fonctionne aussi le week-end quand le marche est ferme.
            if (bar == 0)
            {
                if (_exportArmed || _exportNow)
                {
                    RunExport();
                    _exportNow = false;
                }
                _exportArmed = true;
                return;
            }

            if (_exportOnEachBar && bar == CurrentBar - 1)
                RunExport();
        }

        // ── Timeframe ─────────────────────────────────────────────────────

        /// <summary>
        /// Detecte le timeframe en secondes en comparant les horodatages
        /// de bougies consecutives (prend le minimum non nul sur 20 bougies).
        /// </summary>
        private int DetectTimeframeSeconds()
        {
            int minDiff = int.MaxValue;
            int samples = Math.Min(CurrentBar, 20);

            for (int i = 1; i < samples; i++)
            {
                var c0 = GetCandle(i - 1);
                var c1 = GetCandle(i);
                if (c0 == null || c1 == null) continue;

                int diff = (int)Math.Round((c1.Time - c0.Time).TotalSeconds);
                if (diff > 0 && diff < minDiff)
                    minDiff = diff;
            }

            return minDiff == int.MaxValue ? 60 : minDiff;
        }

        private static string TimeframeLabel(int seconds)
        {
            if (seconds < 60)    return $"{seconds}s";
            if (seconds < 3600)  return $"{seconds / 60}m";
            if (seconds < 86400) return $"{seconds / 3600}h";
            return $"{seconds / 86400}d";
        }

        // ── Export ────────────────────────────────────────────────────────

        private void RunExport()
        {
            try
            {
                if (!Directory.Exists(_outputDirectory))
                    Directory.CreateDirectory(_outputDirectory);

                string filePath = Path.Combine(_outputDirectory, _outputFileName);

                int total = CurrentBar;
                if (total == 0) return;

                // Detection du timeframe
                int    tfSec   = DetectTimeframeSeconds();
                string tfLabel = TimeframeLabel(tfSec);

                // Format datetime : inclut les secondes si timeframe < 60s
                string dtFmt = tfSec < 60
                    ? "dd/MM/yyyy HH:mm:ss"
                    : "dd/MM/yyyy HH:mm";

                var  sb = new StringBuilder(total * 70);
                char s  = _separator;

                // Commentaire d'entete avec le timeframe detecte
                sb.AppendLine($"# Timeframe: {tfLabel}");
                sb.AppendLine($"DateTime{s}Open{s}High{s}Low{s}Close{s}Volume{s}Bid{s}Ask{s}Delta");

                for (int i = 0; i < total; i++)
                {
                    var candle = GetCandle(i);
                    if (candle == null) continue;

                    // candle.Time est en UTC dans l'API ATAS.
                    // On convertit en heure locale Windows (CEST/CET)
                    // pour correspondre a l'export natif ATAS.
                    string dt = candle.Time.ToLocalTime()
                                           .ToString(dtFmt, CultureInfo.InvariantCulture);

                    sb.AppendLine(string.Join(s.ToString(),
                        dt,
                        F(candle.Open),
                        F(candle.High),
                        F(candle.Low),
                        F(candle.Close),
                        F(candle.Volume),
                        F(candle.Bid),
                        F(candle.Ask),
                        F(candle.Delta)
                    ));
                }

                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                try
                {
                    string logPath = Path.Combine(_outputDirectory, "ohlc_export_errors.log");
                    File.AppendAllText(logPath,
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n", Encoding.UTF8);
                }
                catch { }
            }
        }

        private static string F(decimal v)
            => v.ToString(CultureInfo.InvariantCulture);
    }
}
