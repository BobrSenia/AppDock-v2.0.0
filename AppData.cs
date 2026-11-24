using System.Collections.Generic;

namespace dionishuesos
{
    public class AppData
    {
        public string FolderName { get; set; } = "Мои приложения";
        public double Opacity { get; set; } = 0.87;
        public string Color { get; set; } = "#202020";

        // Положение окна на экране
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }

        // Фоновое изображение AppDock
        public string BackgroundImagePath { get; set; } = "";

        // Кастомные изображения кнопок AppDock
        public string SettingsImagePath { get; set; } = "";
        public string CloseImagePath { get; set; } = "";

        // Иконки приложений по пути. Не удаляем запись при удалении приложения из AppDock.
        public Dictionary<string, string> CustomIcons { get; set; } =
            new Dictionary<string, string>();

        public List<AppItem> Apps { get; set; } = new List<AppItem>();
    }

    public class AppItem
    {
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public int Row { get; set; } = 0;
        public int Column { get; set; } = 0;
        public string ShortcutPath { get; set; } = "";
        public string StoredShortcutPath { get; set; } = "";
        public bool HiddenByAppDock { get; set; } = false;
        public int OriginalAttributes { get; set; } = 0;
    }
}
