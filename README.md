# Плагины Ringoto для FFXIV

Три отдельных плагина для Dalamud API 15. Установите нужный через один общий каталог; отдельный сервер и настройка сети не требуются.

| Плагин | Что делает | Открыть окно |
| --- | --- | --- |
| **[Emote Together](docs/EmoteTogether.md)** · 0.4.0 | Эмоциональная группа до восьми игроков, приглашения, синхронный запуск эмоций и передача лидерства. | `/emotegroup` |
| **[Slap Your Friend](docs/SlapYourFriend.md)** · 0.1.1 | Автоматическая пощёчина выбранным игрокам после остановки рядом; индивидуальный интервал повторения. | `/slapfriend` |
| **[Party Art](docs/PartyArt.md)** · 0.1.0 | Отправка рисунка из символов в чат группы по кнопке или команде. | `/partyart` |

## Установка

1. Откройте `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Добавьте ссылку, нажмите **+**, включите **Enabled** и сохраните настройки:

```text
https://raw.githubusercontent.com/Stasik1960/FFXIV-Plug/main/repo.json
```

3. В `/xlplugins` найдите **Emote Together**, **Slap Your Friend** или **Party Art** и нажмите **Install**.

Если каталог уже добавлен, повторно добавлять ссылку не нужно: все три плагина доступны в том же списке. Плагины устанавливаются и обновляются независимо. Автор — **Ringoto**.

Для Emote Together плагин нужен каждому участнику. Slap Your Friend достаточно установить тому, кто будет выполнять эмоцию. Party Art нужен только отправителю рисунка.

## Сборка

Нужны .NET SDK 10 и локальные библиотеки Dalamud API 15. Для нестандартного расположения Dalamud задайте `DALAMUD_HOME`.

```powershell
./scripts/build.ps1
```

Скрипт собирает решение, запускает три автономные проверки и обновляет `dist/plugin/`, `dist/SlapYourFriend/` и `dist/PartyArt/`.

Для новой версии увеличьте версию соответствующего csproj, соберите решение и выполните нужный скрипт:

```powershell
./scripts/package-repository.ps1 -ReleaseTag v0.4.0
./scripts/package-slap.ps1 -ReleaseTag v0.1.1
./scripts/package-party-art.ps1 -ReleaseTag v0.1.0
```

Каждый скрипт формирует собственный ZIP и SHA256 в `plugins/<InternalName>/<version>/`, проверяет архив и обновляет только запись своего плагина в `repo.json`. Записи остальных плагинов сохраняются. Исходники, новый ZIP, контрольную сумму и индекс следует публиковать одним коммитом. Для уже опубликованной версии используйте её существующий архив; изменения выпускайте под новым номером.

Подробности и ограничения: [Emote Together](docs/EmoteTogether.md), [Slap Your Friend](docs/SlapYourFriend.md), [Party Art](docs/PartyArt.md).
