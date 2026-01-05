using SimpleDroneGCS.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SimpleDroneGCS.Controls
{
    public partial class LanguageToggle : UserControl
    {
        private bool _isKazakh = false;

        public LanguageToggle()
        {
            InitializeComponent();

            // Подписываемся на изменение языка
            LocalizationService.Instance.LanguageChanged += OnLanguageChanged;

            // Устанавливаем начальное состояние
            UpdateVisual(LocalizationService.Instance.CurrentLanguage == "kk");
        }

        private void Toggle_Click(object sender, MouseButtonEventArgs e)
        {
            _isKazakh = !_isKazakh;
            AnimateToggle(_isKazakh);

            // Меняем язык в сервисе
            LocalizationService.Instance.CurrentLanguage = _isKazakh ? "kk" : "ru";
        }

        private void OnLanguageChanged(object sender, EventArgs e)
        {
            bool isKazakh = LocalizationService.Instance.CurrentLanguage == "kk";
            if (_isKazakh != isKazakh)
            {
                _isKazakh = isKazakh;
                UpdateVisual(_isKazakh);
            }
        }

        private void AnimateToggle(bool toKazakh)
        {
            // Анимация слайда
            var animation = new DoubleAnimation
            {
                To = toKazakh ? 96 : 0,
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            SliderTransform.BeginAnimation(TranslateTransform.XProperty, animation);

            // Анимация цветов текста
            AnimateTextColor(RussianText, toKazakh ? "#98F019" : "White");
            AnimateTextColor(KazakhText, toKazakh ? "White" : "#98F019");
        }

        private void UpdateVisual(bool isKazakh)
        {
            SliderTransform.X = isKazakh ? 96 : 0;
            RussianText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(isKazakh ? "#98F019" : "White"));
            KazakhText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString(isKazakh ? "White" : "#98F019"));
        }

        private void AnimateTextColor(TextBlock textBlock, string toColor)
        {
            var colorAnimation = new ColorAnimation
            {
                To = (Color)ColorConverter.ConvertFromString(toColor),
                Duration = TimeSpan.FromMilliseconds(250)
            };

            var brush = new SolidColorBrush(((SolidColorBrush)textBlock.Foreground).Color);
            textBlock.Foreground = brush;
            brush.BeginAnimation(SolidColorBrush.ColorProperty, colorAnimation);
        }
    }
}