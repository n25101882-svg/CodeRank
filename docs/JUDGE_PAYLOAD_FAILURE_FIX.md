# SCRUM-10 · Завершення спроб із непридатним Judge payload

Гілка `fix/judge-payload-failure-handling` від `develop` після PR №18. До виправлення Worker вважав `TestsUnavailable` та `InvalidPayload` прочитаним payload і продовжував heartbeat. Спроба залишалася `Running`, а Worker не міг узяти наступну. [SCRUM-10](https://coderank.atlassian.net/browse/SCRUM-10) · [Stage 2A](JUDGE_STAGE2A_REPORT.md)

## Протокол і стан черги

`POST /api/v1/judge/jobs/{id}/fail` приймає JSON `{ "leaseToken": "<uuid>", "reason": "judge_tests_unavailable" }` через HTTPS. Потрібні `Authorization: CodeRankJudge <key>` і `X-CodeRank-Worker: <worker-id>`. Друга допустима причина — `judge_payload_invalid`. Токен передається в тілі. Ліміт тіла — 1 КіБ.

| Результат | Умова |
| --- | --- |
| `204` | API підтвердив причину та завершив спробу. Той самий Worker із тим самим токеном і підтвердженою причиною також отримує `204` на повтор. |
| `400` | Некоректний токен, причина, query string або зайві поля в JSON. |
| `401` | Відсутня чинна машинна автентифікація. Student і Admin JWT також отримують `401`. |
| `409 lease_lost` | Невідома спроба, чужий Worker, інший токен або сплив незавершеного lease. |
| `409 judge_payload_failure_not_confirmed` | Payload справний або його фактичний стан не збігається із заявленою причиною. |
| `503` | Тимчасово недоступне блокування черги. Діє чинний `Retry-After: 5`. |

API використовує ту саму перевірку збереженого коду та знімка, що й `GET /payload`. Відсутність знімка дає `judge_tests_unavailable`. Некоректний знімок або код за наявності знімка дає `judge_payload_invalid`. SQL application lock `CodeRank.JudgeQueue.v1` і транзакція `Serializable` охоплюють перевірку та оновлення. Перед записом API повторно звіряє час lease. Єдиний запис встановлює `SystemError` і `JudgeFinishedAtUtc` та очищає `JudgeLeaseExpiresAtUtc`. Worker ID і токен залишаються для розпізнавання повтору. Повтор не змінює час завершення та лічильник спроб. Після завершення heartbeat і GET із цим токеном повертають `409 lease_lost`.

Worker із `JudgeWorker__FetchPayload=true` надсилає `fail` тільки після визначених відповідей API на GET. Після `204` або `lease_lost` він переходить до наступного claim. Якщо відповідь втрачено або причину відхилено, Worker повторно читає payload без heartbeat для цієї спроби. За `Ready` він відновлює штатний heartbeat. Якщо помилка повторюється, чинний строк lease дає черзі відновити спробу за правилами Stage 1. Мережевий збій, `503` або пошкоджена відповідь самі по собі не є підставою для `fail`. Після перезапуску claim повертає чинний lease або наступну спробу, якщо попередня завершена. Журнал Worker містить ID, стани й технічні типи помилок. Заголовки з ключами й токенами та тіла Judge-запитів мають бути виключені з HTTP-журналів.

Використано чинну схему і дозволи Stage 1/2A. Запис використовує наявні `UPDATE` на `Status`, `JudgeFinishedAtUtc` і `JudgeLeaseExpiresAtUtc`. Виконання C++17/Python 3 та запис студентських вердиктів через `complete` вимкнені.

## Адміністративне відновлення

`SystemError` через відсутність тестів є кінцевим станом. Додавання тестів до задачі не повертає цю спробу в чергу. Адміністратор має виправити набір і узгодити повтор. Поки вікно олімпіади відкрите, учасник може подати новий Submission. Для повторного оцінювання тієї самої спроби потрібна окрема адміністративна процедура. Цей PR не додає операції requeue.

## Автоматизовані перевірки

| Набір | Результат |
| --- | --- |
| API, SQLite in-memory і loopback Kestrel | 345/345 |
| Worker, підставний HTTP API | 40/40 |
| Database Seeding, ізольовані тести | 26/26 |
| Разом | 411/411 |
| Release `CodeRank.Api.slnx` | 0 попереджень, 0 помилок |
| EF Core `has-pending-model-changes` | Неврахованих змін немає |

Нові сценарії охоплюють обидві причини, перехід до наступної спроби, паралельні запити з перевіркою одного запису, replay після перезапуску API, перезапуск Worker до коміту, перевірку lease й облікових даних, відхилення справного payload, втрату відповіді та тимчасові помилки. Два наявні Kestrel-тести пройшли після повторного запуску з дозволеним loopback.

## Ручний QA на CodeRankJudgeQA

1. Розгорни оновлений API перед Worker. Використай Stage 2A схему, окремі машинні облікові дані й лише синтетичні спроби в `CodeRankJudgeQA`. Увімкни `Judge__Enabled=true` для QA API. Під час ручних claim залиш Worker зупиненим. Для останнього кроку запусти його з `JudgeWorker__FetchPayload=true`.
2. Підготуй першу спробу для задачі без активних тестів, другу — з валідним набором. У ручній сесії зроби claim першої спроби та отримай `409 judge_tests_unavailable` від GET. Приклад PowerShell 7 нижче перевіряє `fail` та його повтор і виводить лише статуси.
3. У SSMS звір `SystemError`, заповнений `JudgeFinishedAtUtc`, порожній `JudgeLeaseExpiresAtUtc` і незмінний `JudgeAttemptCount`. Паралельний повтор має дати `204` з тим самим часом завершення. Наступний claim має видати другу спробу. Для її справного payload обидві причини мають дати `409 judge_payload_failure_not_confirmed`.
4. На іншій синтетичній спробі після claim зміни тільки її знімок `JudgeTestSnapshots.TestCasesJson` на `[]` під DBA в QA. Очікуй `422 judge_payload_invalid` від GET та `204` від `fail`. Для окремого незавершеного lease перевір неправильний токен, інший Worker і сплив строку. Очікуй `409 lease_lost`. Для Student/Admin JWT або неправильного ключа очікуй `401`.
5. Повтори чергу з двома новими синтетичними спробами через Worker. Перевір, що після `SystemError` він бере наступну й підтримує її lease за `Ready`. Перезапусти Worker під тим самим ID і звір стан. Stage 1 і Stage 2A раніше пройшли ручне QA. Цей fix на MSSQL та Ubuntu ще потребує ручної перевірки.

```powershell
$jobs = $env:JudgeWorker__ApiBaseUrl.TrimEnd('/') + '/api/v1/judge/jobs'
$headers = @{
    Authorization = 'CodeRankJudge ' + $env:JudgeWorker__Key
    'X-CodeRank-Worker' = $env:JudgeWorker__WorkerId
}
$lease = Invoke-RestMethod -Method Post -Uri "$jobs/claim" -Headers $headers
if ($null -eq $lease.id) { throw 'QA queue returned no submission.' }
$payloadHeaders = $headers.Clone()
$payloadHeaders['X-CodeRank-Lease'] = $lease.leaseToken
$payload = Invoke-WebRequest -Uri "$jobs/$($lease.id)/payload" -Headers $payloadHeaders -SkipHttpErrorCheck
$reason = 'judge_tests_unavailable'
if ($payload.StatusCode -ne 409 -or ($payload.Content | ConvertFrom-Json).code -ne $reason) {
    throw 'Expected a QA submission without a test snapshot.'
}
$body = @{ leaseToken = $lease.leaseToken; reason = $reason } | ConvertTo-Json -Compress
1..2 | ForEach-Object {
    (Invoke-WebRequest -Method Post -Uri "$jobs/$($lease.id)/fail" -Headers $headers `
        -ContentType 'application/json' -Body $body -SkipHttpErrorCheck).StatusCode
}
```
