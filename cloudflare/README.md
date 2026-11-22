# KS System — Cloudflare

Подпроект развёртывания сайта и API учётных записей лаунчера. Статический сайт и portable `.exe` лежат в `site/` (Pages), API — в Worker (`src/index.ts`), учётные записи и сессии — D1. Базовая публикация использует бесплатные Pages, Workers и D1.

## Что делает сайт

- Регистрация и вход в KS System через API Worker.
- Прямая загрузка Windows `.exe` как Pages static asset.
- Адрес Worker указывается в меню «Настроить адрес API»; он хранится в localStorage только как URL. Пароли и access token сайт не сохраняет.

## Первый запуск Cloudflare

1. Создайте D1 базу (аккаунт KS System уже создал `kslauncher`; ID указан в `wrangler.toml`):

   ```powershell
   npx wrangler d1 list
   ```

2. Оставьте `ALLOWED_ORIGIN` на точном Pages origin, например `https://kslauncher.pages.dev`.
3. Задайте секрет подписи JWT и примените схему:

   ```powershell
   npx wrangler secret put TOKEN_SECRET
   npx wrangler d1 migrations apply kslauncher --remote
   npx wrangler deploy
   ```

4. Worker hostname уже задан в `site/config.js`. Разверните сайт Pages из папки `site`:

   ```powershell
   npx wrangler pages project create kslauncher
   npx wrangler pages deploy site --project-name kslauncher
   ```

5. Откройте Pages сайт; URL Worker уже будет подставлен в форму регистрации/ссылку скачивания. При необходимости его можно поменять в настройках на самой странице.

## Подключение Ely.by OAuth

Зарегистрируйте OAuth-приложение Ely.by и укажите redirect URI, совпадающий с `ELY_REDIRECT_URI` в `wrangler.toml`, например `https://kslauncher-api.<ваш-домен>.workers.dev/v1/ely/callback`. Обновите hostname после создания Worker. Секрет Ely.by не помещайте в лаунчер, сайт или git:

```powershell
npx wrangler secret put ELY_CLIENT_ID
npx wrangler secret put ELY_CLIENT_SECRET
npx wrangler secret put TOKEN_SECRET
npx wrangler d1 migrations apply kslauncher --remote
npx wrangler deploy
```

Для Ely.by OAuth-приложения запросите scopes `account_info` и `minecraft_server_session`. Лаунчер открывает официальный Ely.by OAuth, а Worker меняет код на стороне сервера и возвращает через короткоживущий одноразовый зашифрованный билет игровой сессии. Ely-сессия Minecraft использует authlib-injector из официального проекта, автоматически загружаемый с GitHub при первом запуске Ely-профиля.

## Сборка и публикация portable launcher

Из корня репозитория соберите однофайловый Windows x64 executable в `site/`. Он компактен и требует установленный .NET 8 Desktop Runtime:

```powershell
dotnet publish .\KSLauncher.App\KSLauncher.App.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\cloudflare\site
```

Страница скачивания указывает прямо на `KSLauncher.exe` в Pages. Для самодостаточного приложения нужно перейти на self-contained publish; размер будет больше, и ограничения бесплатного тарифа следует перепроверить.

## API

| Method | Path | Назначение |
|---|---|---|
| GET | `/v1/health` | Проверка Worker |
| GET | `/v1/ely/start` | Начать Ely.by OAuth с локальным callback URI |
| GET | `/v1/ely/callback` | Обработать ответ Ely.by на Worker |
| POST | `/v1/ely/exchange` | Обменять одноразовый билет на игровую сессию |
| POST | `/v1/auth/register` | Создать KS System аккаунт и выдать сессию |
| POST | `/v1/auth/login` | Вход по email/паролю |
| POST | `/v1/auth/logout` | Отозвать текущую сессию в D1 |
| GET | `/v1/me` | Аккаунт и метаданные косметики |
| PUT | `/v1/cosmetics/skin` | Загрузить PNG скин (до 4 MiB) |
| PUT | `/v1/cosmetics/cape` | Загрузить PNG плащ (до 4 MiB) |
| GET | `/v1/cosmetics/{skin|cape}/{userId}` | Получить публичный PNG |

Загрузка скинов и плащей через R2 намеренно отключена в бесплатной конфигурации: Cloudflare Dashboard предлагает включить R2 с оплатой по использованию и привязкой подписки. Не включайте его, если сайт должен остаться строго бесплатным.

## Перед публичным запуском

Добавьте rate limiting (WAF/Turnstile), подтверждение email и восстановление пароля, проверку размеров/геометрии PNG для игровых скинов/плащей, квоты R2 и приватную доставку косметики. Установочный файл подписывайте Authenticode и публикуйте контрольную сумму. Не храните `TOKEN_SECRET` или пользовательские токены в клиенте и репозитории. KS System — отдельный аккаунт лаунчера, он не является Minecraft-лицензией и не авторизует пользователя на серверах Minecraft.
