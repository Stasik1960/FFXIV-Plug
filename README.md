# Плагины Ringoto для FFXIV

Два отдельных плагина для Dalamud API 15. Установите нужный через один общий каталог; отдельный сервер и настройка сети не требуются.

| Плагин | Что делает | Открыть окно |
| --- | --- | --- |
| **[Emote Together](docs/EmoteTogether.md)** · 0.4.0 | Эмоциональная группа до восьми игроков, приглашения, синхронный запуск эмоций и передача лидерства. | `/emotegroup` |
| **[Slap Your Friend](docs/SlapYourFriend.md)** · 0.1.0 | Автоматическая пощёчина выбранным игрокам при сближении; индивидуальный интервал повторения. | `/slapfriend` |

## Установка

1. Откройте `/xlsettings` → **Experimental** → **Custom Plugin Repositories**.
2. Добавьте ссылку, нажмите **+**, включите **Enabled** и сохраните настройки:

```text
https://raw.githubusercontent.com/Stasik1960/FFXIV-Plug/main/repo.json
```

3. В `/xlplugins` найдите **Emote Together** или **Slap Your Friend** и нажмите **Install**.

Если каталог уже добавлен для Emote Together, повторно добавлять ссылку не нужно: Slap Your Friend появится в том же списке. Плагины устанавливаются и обновляются независимо. Автор обоих — **Ringoto**.

Для Emote Together плагин нужен каждому участнику. Slap Your Friend достаточно установить тому, кто будет выполнять эмоцию.

## Сборка

Нужны .NET SDK 10 и локальные библиотеки Dalamud API 15. Для нестандартного расположения Dalamud задайте `DALAMUD_HOME`.

```powershell
./scripts/build.ps1
```

Скрипт собирает решение, запускает обе автономные проверки и обновляет `dist/plugin/` и `dist/SlapYourFriend/`.

Для новой версии увеличьте версию соответствующего csproj, соберите решение и выполните нужный скрипт:

```powershell
./scripts/package-repository.ps1 -ReleaseTag v0.4.0
./scripts/package-slap.ps1 -ReleaseTag v0.1.0
```

Каждый скрипт формирует собственный ZIP и SHA256 в `plugins/<InternalName>/<version>/`, проверяет архив и обновляет только запись своего плагина в `repo.json`. Записи остальных плагинов сохраняются. Исходники, новый ZIP, контрольную сумму и индекс следует публиковать одним коммитом. Для уже опубликованной версии используйте её существующий архив; изменения выпускайте под новым номером.

Подробности и ограничения: [Emote Together](docs/EmoteTogether.md), [Slap Your Friend](docs/SlapYourFriend.md).
