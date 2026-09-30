using System.Collections.Generic;

// All game text. The game is Russian-only: Loc.T("key") returns the Russian string
// (unknown keys return the key itself, so a typo is visible in-game rather than blank).
public static class Loc
{
    static readonly Dictionary<string, string> Table = new Dictionary<string, string>
    {
        { "Level", "Уровень {0}" },
        { "Moves", "ХОДЫ" },
        { "Score", "ОЧКИ" },
        { "Tap", "Нажми, чтобы начать" },
        { "Goals", "Задания" },
        { "Pause", "Пауза" },
        { "Settings", "Настройки" },
        { "Music", "Музыка" },
        { "Sound", "Звуки" },
        { "Win", "Победа!" },
        { "Lose", "Ходы закончились" },
        { "Continue", "+5 ходов" },
        { "MapSoon", "Карта уровней скоро появится!\nТы на уровне {0}." },
        { "NoMoves", "Нет ходов - перемешиваем!" },
        { "NotEnough", "Не хватает рыбок" },
        { "Bought", "Куплено!" },
        { "PickCat", "Выбери кота" },
        { "Goal.Score", "Очки" },
        { "Goal.Color", "Собери" },
        { "Goal.Box", "Коробки" },
        { "Goal.Yarn", "Клубки" },
        { "Goal.Mint", "Мята" },
        { "Goal.Vase", "Вазы" },
        { "Goal.Fish", "Рыбки" },
        { "PU.Paw", "Лапка" },
        { "PU.MegaBomb", "Бомба" },
        { "PU.ColorBlast", "Цвет" },
        { "PU.ExtraMoves", "+Ходы" },
        { "Combo.1", "Комбо!" },
    };

    public static string T(string key) => Table.TryGetValue(key, out var s) ? s : key;

    public static string T(string key, object arg) => string.Format(T(key), arg);
}
