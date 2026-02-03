using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SimpleDroneGCS.UI.Dialogs
{
    public partial class AppMessageBox : Window
    {
        public string TitleText { get; }
        public string SubTitleText { get; }
        public Visibility SubTitleVisibility { get; }

        public string MessageText { get; }
        public string HintText { get; }
        public Visibility HintVisibility { get; }

        public string IconText { get; }
        public SolidColorBrush AccentBrush { get; }
        public Color AccentColor { get; }

        public string OkText { get; }
        public string CancelText { get; }

        private AppMessageBox(string title, string message, AppMessageBoxType type,
                              string okText, string cancelText, bool showCancel,
                              string subtitle = null, string hint = null)
        {
            InitializeComponent();

            TitleText = title;
            MessageText = message;

            SubTitleText = subtitle ?? "";
            SubTitleVisibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;

            HintText = hint ?? "";
            HintVisibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;

            (IconText, AccentColor) = GetVisuals(type);
            AccentBrush = new SolidColorBrush(AccentColor);
            AccentBrush.Freeze();

            OkText = okText;
            CancelText = cancelText;

            DataContext = this;

            CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

            // фокус на OK (Enter работает)
            Loaded += (_, __) => OkButton.Focus();
        }

        private static (string icon, Color accent) GetVisuals(AppMessageBoxType type)
        {
            // Под твой "космо" стиль
            Color green = (Color)ColorConverter.ConvertFromString("#98F019");
            Color red = (Color)ColorConverter.ConvertFromString("#EF4444");
            Color yellow = (Color)ColorConverter.ConvertFromString("#FFB800");
            Color blue = (Color)ColorConverter.ConvertFromString("#3B82F6");
            Color purple = (Color)ColorConverter.ConvertFromString("#A855F7");

            return type switch
            {
                AppMessageBoxType.Success => ("✔", green),
                AppMessageBoxType.Warning => ("⚠", yellow),
                AppMessageBoxType.Error => ("⛔", red),
                AppMessageBoxType.Confirm => ("❓", purple),
                _ => ("ℹ", blue),
            };
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
        private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // Результат для YesNoCancel
        public AppMessageBoxResult Result { get; private set; } = AppMessageBoxResult.Cancel;

        private void Yes_Click(object sender, RoutedEventArgs e)
        {
            Result = AppMessageBoxResult.Yes;
            DialogResult = true;
        }

        private void No_Click(object sender, RoutedEventArgs e)
        {
            Result = AppMessageBoxResult.No;
            DialogResult = true;
        }

        // Enter/Esc
        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                DialogResult = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                DialogResult = true;
                e.Handled = true;
            }
        }

        // ---------------- Public API ----------------

        public static bool Show(Window owner, string title, string message, AppMessageBoxType type,
                                string okText = "OK", string cancelText = "Отмена", bool showCancel = false,
                                string subtitle = null, string hint = null)
        {
            var dlg = new AppMessageBox(title, message, type, okText, cancelText, showCancel, subtitle, hint)
            {
                Owner = owner ?? Application.Current?.MainWindow
            };

            return dlg.ShowDialog() == true;
        }

        public static void ShowError(string message, Window owner = null, string subtitle = null, string hint = null)
            => Show(owner, "Ошибка", message, AppMessageBoxType.Error, okText: "OK", showCancel: false, subtitle: subtitle, hint: hint);

        public static void ShowWarning(string message, Window owner = null, string subtitle = null, string hint = null)
            => Show(owner, "Предупреждение", message, AppMessageBoxType.Warning, okText: "OK", showCancel: false, subtitle: subtitle, hint: hint);

        public static void ShowInfo(string message, Window owner = null, string subtitle = null, string hint = null)
            => Show(owner, "Информация", message, AppMessageBoxType.Info, okText: "OK", showCancel: false, subtitle: subtitle, hint: hint);

        public static void ShowSuccess(string message, Window owner = null, string subtitle = null, string hint = null)
            => Show(owner, "Готово", message, AppMessageBoxType.Success, okText: "OK", showCancel: false, subtitle: subtitle, hint: hint);

        public static bool ShowConfirm(string message, Window owner = null, string subtitle = null, string hint = null)
            => Show(owner, "Подтверждение", message, AppMessageBoxType.Confirm,
                    okText: "Да", cancelText: "Нет", showCancel: true, subtitle: subtitle, hint: hint);
        public static AppMessageBoxResult ShowYesNoCancel(string message, Window owner = null,
            string subtitle = null, string yesText = "Да", string noText = "Нет", string cancelText = "Отмена")
        {
            var dlg = new AppMessageBox("Выбор действия", message, AppMessageBoxType.Confirm,
                yesText, cancelText, showCancel: true, subtitle: subtitle)
            {
                Owner = owner ?? Application.Current?.MainWindow
            };

            // Меняем кнопки на Yes/No/Cancel
            dlg.OkButton.Content = yesText;
            dlg.OkButton.Click -= dlg.Ok_Click;
            dlg.OkButton.Click += dlg.Yes_Click;

            // Добавляем кнопку No
            var noButton = new System.Windows.Controls.Button
            {
                Content = noText,
                Style = dlg.CancelButton.Style,
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 80
            };
            noButton.Click += dlg.No_Click;

            // Вставляем между OK и Cancel
            var panel = (System.Windows.Controls.StackPanel)dlg.OkButton.Parent;
            panel.Children.Insert(1, noButton);

            dlg.ShowDialog();
            return dlg.Result;
        }
    }
}
