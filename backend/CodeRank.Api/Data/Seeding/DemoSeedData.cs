namespace CodeRank.Api.Data.Seeding;

internal sealed record ExampleSpec(string Input, string Output, string Explanation);
internal sealed record ProblemSpec(string Title, string Statement, string InputFormat, string OutputFormat,
    string Difficulty, ExampleSpec[] Examples);
internal sealed record ContestSpec(string Title, string Description, DateTime Starts, DateTime Ends,
    (int ProblemIndex, int MaxPoints)[] Problems);

internal static class DemoSeedData
{
    // Stable natural keys/dates are intentional: rerunning never moves an existing contest.
    internal const string AdminEmail = "admin@coderank.test";
    internal const string StudentEmail = "student@coderank.test";
    internal static readonly ProblemSpec[] Problems =
    [
        new("Сума двох чисел",
            "Дано два цілих числа a і b, кожне від -1000000000 до 1000000000. Обчисліть їхню суму.",
            "Один рядок містить два цілих числа a і b, розділені пробілом.",
            "Виведіть одне ціле число — суму a і b.", "Easy",
            [new("2 3", "5", "2 + 3 = 5."), new("-7 4", "-3", "Сума від’ємного й додатного чисел."), new("0 0", "0", "Обидва доданки дорівнюють нулю.")]),
        new("Максимум із трьох чисел",
            "Дано три цілих числа a, b і c від -1000000000 до 1000000000. Знайдіть найбільше з них.",
            "Один рядок містить три цілі числа, розділені пробілами.",
            "Виведіть найбільше з трьох чисел.", "Easy",
            [new("3 9 5", "9", "Найбільше число — 9."), new("-8 -2 -5", "-2", "Усі числа від’ємні."), new("4 4 4", "4", "Усі числа однакові.")]),
        new("Перевірка паліндрому",
            "Дано непорожній рядок із малих латинських літер довжиною до 1000 символів. Визначте, чи однаково він читається зліва направо та справа наліво.",
            "Один рядок містить слово з малих латинських літер без пробілів.",
            "Виведіть YES, якщо рядок є паліндромом, або NO в іншому випадку.", "Easy",
            [new("level", "YES", "Слово читається однаково в обох напрямках."), new("hello", "NO", "Порядок літер при розвороті змінюється."), new("a", "YES", "Рядок з одного символу є паліндромом.")]),
        new("Числа Фібоначчі",
            "Послідовність задано так: F(0)=0, F(1)=1, F(n)=F(n-1)+F(n-2) для n>=2. Для заданого n від 0 до 90 знайдіть F(n). Результат уміщується у знаковому 64-бітному цілому числі.",
            "Один рядок містить ціле число n (0 <= n <= 90).",
            "Виведіть одне ціле число F(n).", "Medium",
            [new("0", "0", "Початкове значення F(0)."), new("1", "1", "Початкове значення F(1)."), new("10", "55", "Десяте число послідовності дорівнює 55.")]),
        new("Сортування масиву",
            "Дано масив із n цілих чисел, де 1 <= n <= 1000, а кожен елемент від -1000000000 до 1000000000. Відсортуйте елементи за неспаданням, зберігаючи повторення.",
            "Перший рядок містить n. Другий рядок містить n цілих чисел, розділених пробілами.",
            "Виведіть відсортовані елементи в одному рядку через пробіл.", "Medium",
            [new("5\n3 1 4 1 5", "1 1 3 4 5", "Повторення числа 1 збережено."), new("1\n-7", "-7", "Масив з одного елемента."), new("4\n0 -1 2 -1", "-1 -1 0 2", "Підтримуються нулі, від’ємні числа та повторення.")])
    ];

    internal static readonly ContestSpec[] Contests =
    [
        new("CodeRank Beginner Contest", "Демонстраційна олімпіада для знайомства з базовими задачами програмування.",
            new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc), new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc),
            [(0,100), (1,100), (2,100)]),
        new("CodeRank Algorithms Challenge", "Демонстраційна олімпіада з рядків, послідовностей і сортування.",
            new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc), new(2026, 11, 14, 13, 0, 0, DateTimeKind.Utc),
            [(2,100), (3,150), (4,150)])
    ];
}
