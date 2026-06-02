using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SimpleDroneGCS.Helpers;
using static SimpleDroneGCS.Helpers.Loc;

namespace SimpleDroneGCS.Views
{
    /// <summary>
    /// Какая фигура выбрана пользователем.
    /// </summary>
    public enum MissionShapeType
    {
        Circle,
        Rectangle,
        Line,
        Lawnmower,
        ExpandingSquare,
        SectorSearch,
        Spiral
    }

    /// <summary>
    /// Параметры размещения фигуры (возвращаются из диалога).
    /// Заполняются только релевантные для выбранной Type поля.
    /// </summary>
    public class MissionShapeParams
    {
        public MissionShapeType Type;
        public double Altitude = 100;
        public bool Clockwise = true;

        // Круг, СекторныйПоиск
        public double Radius;
        public int NumPoints;          // Круг
        public double InitialBearing;  // СекторныйПоиск, РасшКвадрат, Линия
        public SectorMode SectorMode = SectorMode.Standard;

        // Прямоугольник, Лужайка
        public double Width;
        public double Height;
        public double Rotation;

        // Линия
        public double Length;
        public int NumSegments;
        public double EndAltitude;

        // Лужайка
        public double Spacing;
        public double Overshoot;

        // РасшКвадрат
        public double TrackSpacing;
        public int NumLoops;

        // Спираль
        public double StartRadius;
        public double SpacingPerLoop;
        public int PointsPerLoop;
        // NumLoops уже есть выше
    }

    public partial class MissionShapeDialog : Window
    {
        public MissionShapeParams Result { get; private set; }

        public MissionShapeDialog()
        {
            InitializeComponent();

            // По умолчанию выбираем Круг
            ShapeCombo.SelectedIndex = 0;

            // ESC закрывает диалог
            this.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    DialogResult = false;
                    Close();
                }
            };
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 1) DragMove();
        }

        private void ShapeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CirclePanel == null) return; // ещё не инициализирован

            // Скрываем все панели параметров
            CirclePanel.Visibility = Visibility.Collapsed;
            RectanglePanel.Visibility = Visibility.Collapsed;
            LinePanel.Visibility = Visibility.Collapsed;
            LawnmowerPanel.Visibility = Visibility.Collapsed;
            ExpSquarePanel.Visibility = Visibility.Collapsed;
            SectorPanel.Visibility = Visibility.Collapsed;
            SpiralPanel.Visibility = Visibility.Collapsed;

            // Направление CW/CCW по умолчанию видно
            DirectionLabel.Visibility = Visibility.Visible;
            DirectionCombo.Visibility = Visibility.Visible;

            var tag = (ShapeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Circle";
            switch (tag)
            {
                case "Circle":
                    CirclePanel.Visibility = Visibility.Visible;
                    DescriptionText.Text = Get("MissionShape_Desc_Circle");
                    break;
                case "Rectangle":
                    RectanglePanel.Visibility = Visibility.Visible;
                    DescriptionText.Text = Get("MissionShape_Desc_Rectangle");
                    break;
                case "Line":
                    LinePanel.Visibility = Visibility.Visible;
                    DescriptionText.Text = Get("MissionShape_Desc_Line");
                    // У линии нет CW/CCW — направление задаётся азимутом
                    DirectionLabel.Visibility = Visibility.Collapsed;
                    DirectionCombo.Visibility = Visibility.Collapsed;
                    break;
                case "Lawnmower":
                    LawnmowerPanel.Visibility = Visibility.Visible;
                    DescriptionText.Text = Get("MissionShape_Desc_Lawnmower");
                    // У лужайки CW/CCW не имеет смысла — направление через rotation
                    DirectionLabel.Visibility = Visibility.Collapsed;
                    DirectionCombo.Visibility = Visibility.Collapsed;
                    break;
                case "ExpandingSquare":
                    ExpSquarePanel.Visibility = Visibility.Visible;
                    DescriptionText.Text = Get("MissionShape_Desc_ExpSquare");
                    break;
                case "SectorSearch":
                    SectorPanel.Visibility = Visibility.Visible;
                    DescriptionText.Text = Get("MissionShape_Desc_Sector");
                    // У секторного нет CW/CCW (всегда CW по построению)
                    DirectionLabel.Visibility = Visibility.Collapsed;
                    DirectionCombo.Visibility = Visibility.Collapsed;
                    break;
                case "Spiral":
                    SpiralPanel.Visibility = Visibility.Visible;
                    DescriptionText.Text = Get("MissionShape_Desc_Spiral");
                    break;
            }
        }

        private bool TryParse(TextBox box, string name, out double value, double min = double.MinValue, double max = double.MaxValue)
        {
            value = 0;
            if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.TryParse(box.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                ShowError(Loc.Fmt("MissionShape_InvalidField", name));
                box.Focus();
                box.SelectAll();
                return false;
            }
            if (value < min || value > max)
            {
                ShowError(Loc.Fmt("MissionShape_OutOfRange", name, min, max));
                box.Focus();
                box.SelectAll();
                return false;
            }
            return true;
        }

        private bool TryParseInt(TextBox box, string name, out int value, int min = int.MinValue, int max = int.MaxValue)
        {
            value = 0;
            if (!int.TryParse(box.Text, out value))
            {
                ShowError(Loc.Fmt("MissionShape_InvalidField", name));
                box.Focus();
                box.SelectAll();
                return false;
            }
            if (value < min || value > max)
            {
                ShowError(Loc.Fmt("MissionShape_OutOfRange", name, min, max));
                box.Focus();
                box.SelectAll();
                return false;
            }
            return true;
        }

        private void ShowError(string msg)
        {
            MessageBox.Show(this, msg, Get("MsgBox_Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var p = new MissionShapeParams();
            var tag = (ShapeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Circle";

            // Общие
            if (!TryParse(AltitudeBox, Get("MissionShape_Altitude"), out double altitude, 5, 5000)) return;
            p.Altitude = altitude;
            p.Clockwise = ((DirectionCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "CW") == "CW";

            switch (tag)
            {
                case "Circle":
                    p.Type = MissionShapeType.Circle;
                    if (!TryParse(CircleRadius, Get("MissionShape_Radius"), out p.Radius, 1, 100_000)) return;
                    if (!TryParseInt(CirclePoints, Get("MissionShape_NumPoints"), out p.NumPoints, 3, 360)) return;
                    break;

                case "Rectangle":
                    p.Type = MissionShapeType.Rectangle;
                    if (!TryParse(RectWidth, Get("MissionShape_Width"), out p.Width, 1, 100_000)) return;
                    if (!TryParse(RectHeight, Get("MissionShape_Height"), out p.Height, 1, 100_000)) return;
                    if (!TryParse(RectRotation, Get("MissionShape_Rotation"), out p.Rotation, -360, 360)) return;
                    break;

                case "Line":
                    p.Type = MissionShapeType.Line;
                    if (!TryParse(LineLength, Get("MissionShape_Length"), out p.Length, 1, 100_000)) return;
                    if (!TryParse(LineBearing, Get("MissionShape_Bearing"), out p.InitialBearing, -360, 360)) return;
                    if (!TryParseInt(LineSegments, Get("MissionShape_Segments"), out p.NumSegments, 1, 100)) return;
                    if (!TryParse(LineEndAlt, Get("MissionShape_EndAltitude"), out p.EndAltitude, 5, 5000)) return;
                    break;

                case "Lawnmower":
                    p.Type = MissionShapeType.Lawnmower;
                    if (!TryParse(LawnWidth, Get("MissionShape_Width"), out p.Width, 1, 100_000)) return;
                    if (!TryParse(LawnLength, Get("MissionShape_Length"), out p.Height, 1, 100_000)) return;
                    if (!TryParse(LawnSpacing, Get("MissionShape_Spacing"), out p.Spacing, 1, 10_000)) return;
                    if (!TryParse(LawnRotation, Get("MissionShape_Rotation"), out p.Rotation, -360, 360)) return;
                    if (!TryParse(LawnOvershoot, Get("MissionShape_Overshoot"), out p.Overshoot, 0, 10_000)) return;
                    break;

                case "ExpandingSquare":
                    p.Type = MissionShapeType.ExpandingSquare;
                    if (!TryParse(ExpSqTrackSpacing, Get("MissionShape_TrackSpacing"), out p.TrackSpacing, 1, 10_000)) return;
                    if (!TryParseInt(ExpSqLoops, Get("MissionShape_NumLoops"), out p.NumLoops, 1, 20)) return;
                    if (!TryParse(ExpSqBearing, Get("MissionShape_InitialBearing"), out p.InitialBearing, -360, 360)) return;
                    break;

                case "SectorSearch":
                    p.Type = MissionShapeType.SectorSearch;
                    if (!TryParse(SectorRadius, Get("MissionShape_Radius"), out p.Radius, 1, 100_000)) return;
                    if (!TryParse(SectorBearing, Get("MissionShape_InitialBearing"), out p.InitialBearing, -360, 360)) return;
                    var modeTag = (SectorModeCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Standard";
                    p.SectorMode = (modeTag == "Dense")
                        ? SectorMode.Dense
                        : SectorMode.Standard;
                    break;

                case "Spiral":
                    p.Type = MissionShapeType.Spiral;
                    if (!TryParse(SpiralStartRadius, Get("MissionShape_StartRadius"), out p.StartRadius, 1, 100_000)) return;
                    if (!TryParse(SpiralSpacing, Get("MissionShape_SpacingPerLoop"), out p.SpacingPerLoop, 1, 10_000)) return;
                    if (!TryParseInt(SpiralLoops, Get("MissionShape_NumLoops"), out p.NumLoops, 1, 20)) return;
                    if (!TryParseInt(SpiralPPL, Get("MissionShape_PointsPerLoop"), out p.PointsPerLoop, 6, 36)) return;
                    break;
            }

            Result = p;

            double chord = EstimateMinChord(p);
            if (chord > 0 && chord < 150)
            {
                var res = MessageBox.Show(this,
                    Loc.Fmt("MissionShape_TooCloseWarning", chord.ToString("F0")),
                    Get("MissionShape_Title"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (res != MessageBoxResult.Yes)
                {
                    Result = null;
                    return;
                }
            }

            DialogResult = true;
            Close();
        }

        /// <summary>
        /// Оценка минимальной хорды (расстояния между соседними точками) для VTOL safety.
        /// Возвращает 0 если оценка неприменима.
        /// </summary>
        private static double EstimateMinChord(MissionShapeParams p)
        {
            switch (p.Type)
            {
                case MissionShapeType.Circle:
                    // 2·R·sin(π/N)
                    return p.NumPoints > 0 ? 2 * p.Radius * Math.Sin(Math.PI / p.NumPoints) : 0;
                case MissionShapeType.Rectangle:
                    return Math.Min(p.Width, p.Height);
                case MissionShapeType.Line:
                    return p.NumSegments > 0 ? p.Length / p.NumSegments : 0;
                case MissionShapeType.Lawnmower:
                    // Минимум из длины полосы (Height) и шага между полосами
                    return Math.Min(p.Height, p.Spacing);
                case MissionShapeType.ExpandingSquare:
                    // Первая нога самая короткая = trackSpacing
                    return p.TrackSpacing;
                case MissionShapeType.SectorSearch:
                    // Каждая нога = radius (центр → вершина)
                    return p.Radius;
                case MissionShapeType.Spiral:
                    // На стартовом радиусе chord ≈ 2·R0·sin(π/PPL)
                    return p.PointsPerLoop > 0 ? 2 * p.StartRadius * Math.Sin(Math.PI / p.PointsPerLoop) : 0;
                default:
                    return 0;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}