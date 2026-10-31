# Database Seeding — CodeRank

Seeder є окремою явною командою ASP.NET Core Web API. Він використовує наявний `CodeRankDbContext`, не змінює ER-модель, не створює/не застосовує міграції та не викликає EnsureCreated. Звичайний запуск API не запускає seeder. Конфігурація appsettings/змінні середовища не можуть самостійно увімкнути виконання: потрібен аргумент CLI `--seed`.

## Демонстраційний набір

На порожній схемі створюються:

| Таблиця | Записів |
|---|---:|
| Users | 2 |
| UserProfiles | 2 |
| Olympiads | 2 |
| Problems | 5 |
| OlympiadProblems | 6 |
| ProblemExamples | 15 |

- `admin@coderank.test`: роль Admin, профіль Demo Administrator.
- `student@coderank.test`: роль User, профіль Demo Student.
- CodeRank Beginner Contest: 07.11.2026, 09:00–12:00 UTC; сума, максимум і паліндром, по 100 балів.
- CodeRank Algorithms Challenge: 14.11.2026, 09:00–13:00 UTC; паліндром (100), Фібоначчі (150), сортування (150).
- Обидві олімпіади опубліковані й мають адміністратора-створювача. Дати є сталими демонстраційними значеннями; повторний запуск не пересуває їх відносно поточного часу.
- Кожна з 5 задач має умову, формати вводу/виводу, складність та 3 відкриті приклади. Це приклади з умови, а не приховані тести judge.

## Підготовка конфігурації та паролів

Потрібна наявна схема шести таблиць і налаштований `ConnectionStrings:DefaultConnection`. Для локального запуску використовуйте User Secrets проєкту `backend/CodeRank.Api/` (UserSecretsId `CodeRank.Api-LocalDevelopment`).

У Visual Studio: **CodeRank.Api → Manage User Secrets**. Збережіть існуючу секцію ConnectionStrings і додайте секцію нижче. Порожні значення замініть власними паролями локально:

```json
"Seeding": {
  "AdminPassword": "",
  "StudentPassword": ""
}
```

Для створення нових облікових записів потрібні паролі щонайменше з 12 символів. Вони читаються спеціальним провайдером **лише** з User Secrets (у Development) або змінних середовища; значення з appsettings та аргументів командного рядка seeder не використовує. Паролі хешуються стандартним `PasswordHasher<User>` (ASP.NET Core Identity); фіксованих тестових паролів у коді немає.

Альтернатива — змінні процесу `Seeding__AdminPassword` і `Seeding__StudentPassword`. Приклад PowerShell із захищеним введенням, без пароля в тексті команди:

```powershell
$seedAdminSecret = Read-Host 'Seed admin password' -AsSecureString
$env:Seeding__AdminPassword = [PSCredential]::new('seed', $seedAdminSecret).GetNetworkCredential().Password
$seedStudentSecret = Read-Host 'Seed student password' -AsSecureString
$env:Seeding__StudentPassword = [PSCredential]::new('seed', $seedStudentSecret).GetNetworkCredential().Password
```

Не додавайте реальні паролі в .env.example, appsettings або Git. Змінні процесу мають вищий пріоритет за User Secrets. `.env` сам собою не завантажується. Після сеансу змінні можна прибрати через `Remove-Item Env:Seeding__AdminPassword,Env:Seeding__StudentPassword`.

Для **наявного** користувача пароль не змінюється й відповідний секрет не потрібен. Dry-run не звертається до джерела seed-паролів і не виконує хешування.

## Попередній перегляд без запису

З кореня репозиторію:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet run --project backend/CodeRank.Api --no-launch-profile -- --seed --dry-run
```

Короткий `--seed` також означає dry-run. Додатково можна перевірити очікувану назву БД:

```powershell
dotnet run --project backend/CodeRank.Api --no-launch-profile -- --seed --dry-run --seed-target=CodeRankDB
```

Dry-run виконує лише SELECT, виявляє конфлікти та показує кількість нових і повторно використаних записів за таблицями. Він не додає сутності до ChangeTracker, не викликає SaveChanges і не бере блокування sp_getapplock. Це попередня оцінка: за конкурентних змін даних результат наступного запуску може відрізнятися.

Виконання завершується з кодом 0 при успіху та 1 при відмові/помилці. У Production заборонені як запис, так і dry-run; перевірка є в CLI та всередині DatabaseSeeder.

## Наповнення окремої тестової БД

Спочатку DBA створює окрему БД, наприклад CodeRankSeedTest, застосовує до неї наявний SQL зі схемою та надає потрібні права. Механізм seeding не створює БД самостійно. Під час підтвердженого інтеграційного тестування в `CodeRankSeedTest` успішно створено шість таблиць і застосовано `InitialBaseline` як окремі кроки підготовки; seeder не виконував створення схеми або застосування міграції.

1. Налаштуйте окреме локальне підключення до тестової БД.
2. Запустіть dry-run з `--seed-target=CodeRankSeedTest`.
3. Після перевірки виконайте:

   ```powershell
   dotnet run --project backend/CodeRank.Api --no-launch-profile -- --seed --apply --seed-target=CodeRankSeedTest
   ```

Прапорець `--apply` і точна назва підключеної БД обов’язкові для запису. Підтвердження перевіряє назву БД, а не адресу сервера: перед запуском перевірте обидва значення локальної конфігурації. Прапорці з іншим написанням або значеннями на кшталт `--dry-run=true` відхиляються, щоб не змішувати режими. Системні БД master/model/msdb/tempdb заборонені. HTTP-сервер у режимі seeding не запускається.

**Реальна CodeRankDB:** агент не виконував цей режим. Команда нижче є інструкцією для окремо погодженого майбутнього запуску, а не вже виконаною дією:

```powershell
dotnet run --project backend/CodeRank.Api --no-launch-profile -- --seed --apply --seed-target=CodeRankDB
```

Ніколи не підмінюйте середовище Production лише заради запуску демонстраційного seeder.

## Ідемпотентність, конфлікти й транзакції

- Ключі пошуку: нормалізований email; назви демоолімпіад і задач; пара OlympiadId/ProblemId; позиція прикладу в задачі.
- Сумісні наявні записи повторно використовуються. Наявні паролі, імена профілів, дати та дані не перезаписуються.
- Відсутній профіль існуючого сумісного користувача можна додати; пароль такого користувача не змінюється.
- Несумісні роль/активність/NormalizedEmail, інший автор або змінений вміст задачі/олімпіади, інший приклад чи зайнята позиція викликають явну відмову. Не виконується автоматичне підвищення ролі, розблокування або відновлення пароля.
- Дубльовані назви неоднозначні: seeder зупиняється й залишає дані для ручного розгляду.
- План повністю перевіряється до додавання записів. Використовуються лише нові сутності; UPDATE/DELETE не викликаються.
- У режимі запису план читається й застосовується в одній транзакції Serializable. SQL Server додатково отримує транзакційне Exclusive application lock `CodeRank.DemoSeed.v1` з очікуванням до 10 секунд. Це серіалізує паралельні запуски цього seeder. Блокування звільняється при commit/rollback.
- Помилка збереження скасовує транзакцію; автоматичних повторів із перезаписуванням немає. Після збою перевірте стан і повторіть команду.

Повторний запуск тієї самої команди на незміненому наборі показує 0 нових і 32 повторно використані записи. Паролі можна прибрати з оточення після створення акаунтів; повторний запуск не вимагає їх повторного введення.

## Перевірка результату

SQL нижче — лише читання; виконуйте його під обліковим записом із SELECT у потрібній БД:

```sql
SELECT u.Email, u.Role, u.IsActive, p.FirstName, p.LastName,
       CASE WHEN LEN(u.PasswordHash) > 0 THEN 1 ELSE 0 END AS HasPasswordHash
FROM dbo.Users AS u
LEFT JOIN dbo.UserProfiles AS p ON p.UserId = u.Id
WHERE u.NormalizedEmail IN (N'ADMIN@CODERANK.TEST', N'STUDENT@CODERANK.TEST');

SELECT o.Title, o.StartsAtUtc, o.EndsAtUtc, o.IsPublished,
       p.Title AS ProblemTitle, op.Position, op.MaxPoints, op.IsVisible
FROM dbo.Olympiads AS o
JOIN dbo.OlympiadProblems AS op ON op.OlympiadId = o.Id
JOIN dbo.Problems AS p ON p.Id = op.ProblemId
WHERE o.Title IN (N'CodeRank Beginner Contest', N'CodeRank Algorithms Challenge')
ORDER BY o.Title, op.Position;

SELECT p.Title, COUNT(*) AS ExampleCount
FROM dbo.Problems AS p
JOIN dbo.ProblemExamples AS e ON e.ProblemId = p.Id
JOIN dbo.Users AS u ON u.Id = p.CreatedByUserId
WHERE u.NormalizedEmail = N'ADMIN@CODERANK.TEST'
  AND p.Title IN (N'Сума двох чисел', N'Максимум із трьох чисел',
                  N'Перевірка паліндрому', N'Числа Фібоначчі', N'Сортування масиву')
GROUP BY p.Id, p.Title;
```

Очікується 2 користувачі з профілями, 6 рядків зв’язків та 5 задач по 3 приклади. Ці числа стосуються демонабору, а не всіх даних у БД. Повторіть dry-run/погоджений apply і переконайтеся у відсутності нових записів. Не виводьте справжні паролі чи хеші до звітів.

## Права MSSQL

Для dry-run потрібні CONNECT та SELECT на всіх шести таблицях. CONNECT + VIEW DEFINITION без SELECT недостатньо; SQL 229 означає відсутність потрібного дозволу на читання.

Для режиму запису потрібні CONNECT, SELECT та INSERT на Users, UserProfiles, Olympiads, Problems, OlympiadProblems і ProblemExamples. UPDATE, DELETE, ALTER, CREATE TABLE, db_owner або широке db_datawriter цьому seeder не потрібні. Для sp_getapplock зазвичай достатньо членства в public; при спеціальному обмеженні системних процедур DBA має перевірити дозвіл виконання.

Права видає DBA окремому довіреному виконавцю. Агент не змінював права codex_dev. Для реальної CodeRankDB спочатку потрібне окреме погодження запису.

## Тести та підтверджені результати

### Автоматизовані тести

```powershell
dotnet build backend/CodeRank.Api/CodeRank.Api.csproj
dotnet test tests/CodeRank.Api.Seeding.Tests/CodeRank.Api.Seeding.Tests.csproj
```

Автоматизовані тести: **26/26 успішні**. Вони використовують ізольовані SQLite in-memory БД з FK/транзакціями. Модель адаптується до діалекту SQLite тільки у тестовому класі, а EnsureCreated викликається лише для цих приватних тестових БД. Перевіряються наповнення, сумісність хешів з PasswordHasher, повторний запуск, dry-run, відсутні секрети, конфлікти, відкат при помилці посеред SaveChanges, Production та підтвердження цільової БД.

### Інтеграційне тестування Microsoft SQL Server

Окремо від автоматизованих SQLite-тестів успішно виконано інтеграційний сценарій на тестовій БД **`CodeRankSeedTest`**:

| Етап | Підтверджений результат |
|---|---|
| Підготовка тестової БД | Створено 6 таблиць і застосовано `InitialBaseline` |
| Dry-run | Заплановано 32 нові записи; змін у БД немає |
| Перший apply | Створено 32 записи |
| Повторний apply | 0 нових записів; 32 наявні записи повторно використано |
| Перевірка в SSMS | Підтверджено кількість записів у всіх шести таблицях |

Підтверджені в SSMS підсумки:

| Таблиця | Записів |
|---|---:|
| Users | 2 |
| UserProfiles | 2 |
| Olympiads | 2 |
| Problems | 5 |
| OlympiadProblems | 6 |
| ProblemExamples | 15 |
| **Разом** | **32** |

Отже, dry-run без запису, початкове наповнення та ідемпотентність повторного послідовного запуску підтверджені на Microsoft SQL Server. Реальну **`CodeRankDB`** для цих перевірок не використовували.

### Ще не проведені перевірки на MSSQL

- **Паралельні запуски:** не виконано окремого тесту двох одночасних apply, очікування/тайм-ауту `sp_getapplock` та поведінки за конкурентних змін. Успішні послідовні запуски не підтверджують ці сценарії.
- **Примусовий відкат:** не виконано інтеграційного тесту з навмисною помилкою посеред запису на MSSQL і перевіркою повного відкату. Такий відкат перевірено автоматизованими тестами на SQLite; результат не переноситься автоматично на MSSQL.

Ці два сценарії залишаються окремими наступними перевірками на тестовій БД; вони не включені до наведених успішних MSSQL-результатів.

## Потенційні ризики

Назви задач/олімпіад не мають UNIQUE у поточній схемі. Seeder обробляє неоднозначність і координує свої запуски, але не може заборонити сторонньому коду створити дублі після завершення транзакції. Ручне перейменування seed-запису змінює його природний ключ; наступний запуск може сприйняти старе ім’я як відсутній запис. Вміст каталогу v1 і сталі ключі слід змінювати лише з окремим планом узгодження даних.

Демонстраційні опубліковані олімпіади мають сталі дати; з часом вони стають завершеними. Конфлікти й помилки доступу потребують ручного розбору, а не видалення наявних даних або розширення прав до db_owner.

## Джерела

- [PasswordHasher](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.identity.passwordhasher-1)
- [EF Core Transactions](https://learn.microsoft.com/en-us/ef/core/saving/transactions)
- [SQL Server sp_getapplock](https://learn.microsoft.com/en-us/sql/relational-databases/system-stored-procedures/sp-getapplock-transact-sql)
