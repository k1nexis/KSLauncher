import asyncio
import os
import time
import aiohttp
import discord
from discord.ext import commands
from dotenv import load_dotenv

load_dotenv()

DISCORD_TOKEN = os.getenv("DISCORD_TOKEN", "").strip()
SUPPORT_CHANNEL_ID = os.getenv("SUPPORT_CHANNEL_ID", "").strip()
OLLAMA_URL = os.getenv("OLLAMA_URL", "http://localhost:11434").rstrip("/")
OLLAMA_MODEL = os.getenv("OLLAMA_MODEL", "qwen2.5:7b").strip()
GROQ_API_KEY = os.getenv("GROQ_API_KEY", "").strip()
GEMINI_API_KEY = os.getenv("GEMINI_API_KEY", "").strip()
OPENROUTER_API_KEY = os.getenv("OPENROUTER_API_KEY", "").strip()

SYSTEM_PROMPT = """Ты — официальный AI-помощник поддержки KS Launcher (лаунчер Minecraft для Windows).
Отвечай кратко, дружелюбно и по делу на русском языке (до 1500 символов).

База знаний KS Launcher:
1. Загрузчики: Vanilla, Fabric, Forge, NeoForge, Quilt. Вкладка Vanilla всегда запускает чистый Minecraft без модов.
2. Краши и ошибки модов: посоветуй нажать кнопку «Починить сборку» на главной или в Библиотеке, а также проверить версию Java в Настройках (1.17–1.20.4 -> Java 17; 1.20.5+ -> Java 21; 1.16.5 и ниже -> Java 8).
3. Скины: в KS System скин (PNG 64x64 или 64x32) загружается во вкладке Аккаунты -> KS System и автоматически подтягивается через встроенный Yggdrasil-мост. Также поддерживаются Microsoft и Ely.by.
4. KS Friends (вкладка «Друзья»): ID формата KS-XXXXXXXX. Можно добавлять друзей независимо от их аккаунта (Microsoft, Ely.by, KS System, Offline). Приглашения из других стран работают через код KSINV:... (вставляется в поле добавления друзей).
5. Локальные серверы (вкладка «Серверы»): поддерживаются Vanilla, Paper, Fabric; настройка ОЗУ (2-8 ГБ), живая консоль, вход открыт для всех аккаунтов, авто-проброс порта UPnP. Если роутер блокирует UPnP, нужен ручной проброс порта 25565 (TCP) или Radmin VPN / Playit.gg.
6. FPS и лаги: во время игры фоновые эффекты лаунчера автоматически ставятся на паузу, процессу игры даётся High-приоритет, включена многопоточная прогрузка чанков. Рекомендуется выделять 4–6 ГБ ОЗУ (не всю память ПК).
7. Скачать/обновить: https://kslauncher.pages.dev/ и зеркало https://k1nexis.github.io/KSLauncher/ (лаунчер сам переключается между ними).

Если вопрос не относится к KS Launcher или требует вмешательства человека — вежливо попроси упомянуть @Модератор."""

USER_COOLDOWNS: dict[int, float] = {}
COOLDOWN_SECONDS = 5.0

intents = discord.Intents.default()
intents.message_content = True
bot = commands.Bot(command_prefix="!", intents=intents)


async def query_ollama(session: aiohttp.ClientSession, prompt: str) -> str | None:
    try:
        async with session.post(
            f"{OLLAMA_URL}/api/chat",
            json={
                "model": OLLAMA_MODEL,
                "stream": False,
                "messages": [
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": prompt},
                ],
            },
            timeout=aiohttp.ClientTimeout(total=25),
        ) as resp:
            if resp.status == 200:
                data = await resp.json()
                return data.get("message", {}).get("content")
    except Exception:
        pass
    return None


async def query_groq(session: aiohttp.ClientSession, prompt: str) -> str | None:
    if not GROQ_API_KEY:
        return None
    try:
        async with session.post(
            "https://api.groq.com/openai/v1/chat/completions",
            headers={"Authorization": f"Bearer {GROQ_API_KEY}"},
            json={
                "model": "llama-3.3-70b-versatile",
                "messages": [
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": prompt},
                ],
                "max_tokens": 600,
                "temperature": 0.4,
            },
            timeout=aiohttp.ClientTimeout(total=20),
        ) as resp:
            if resp.status == 200:
                data = await resp.json()
                return data["choices"][0]["message"]["content"]
    except Exception:
        pass
    return None


async def query_gemini(session: aiohttp.ClientSession, prompt: str) -> str | None:
    if not GEMINI_API_KEY:
        return None
    try:
        async with session.post(
            f"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={GEMINI_API_KEY}",
            json={
                "system_instruction": {"parts": [{"text": SYSTEM_PROMPT}]},
                "contents": [{"parts": [{"text": prompt}]}],
            },
            timeout=aiohttp.ClientTimeout(total=20),
        ) as resp:
            if resp.status == 200:
                data = await resp.json()
                return data["candidates"][0]["content"]["parts"][0]["text"]
    except Exception:
        pass
    return None


async def query_openrouter(session: aiohttp.ClientSession, prompt: str) -> str | None:
    if not OPENROUTER_API_KEY:
        return None
    try:
        async with session.post(
            "https://openrouter.ai/api/v1/chat/completions",
            headers={"Authorization": f"Bearer {OPENROUTER_API_KEY}"},
            json={
                "model": "meta-llama/llama-3.3-70b-instruct:free",
                "messages": [
                    {"role": "system", "content": SYSTEM_PROMPT},
                    {"role": "user", "content": prompt},
                ],
            },
            timeout=aiohttp.ClientTimeout(total=25),
        ) as resp:
            if resp.status == 200:
                data = await resp.json()
                return data["choices"][0]["message"]["content"]
    except Exception:
        pass
    return None


def local_faq_fallback(text: str) -> str:
    low = text.lower()
    if any(k in low for k in ("java", "джава", "версия java")):
        return "Проверьте **Java** в **Настройках лаунчера**: для 1.17–1.20.4 нужна **Java 17**, для 1.20.5+ — **Java 21**, для 1.16.5 и ниже — **Java 8**."
    if any(k in low for k in ("краш", "вылетает", "не запускается", "ошибка", "мод")):
        return "Нажмите кнопку **«Починить сборку»** на главной или в **Библиотеке** — лаунчер проверит моды и докачает нужные библиотеки. Если не поможет, прикрепите файл лога и позовите `@Модератор`."
    if any(k in low for k in ("скин", "плащ")):
        return "Войдите в **KS System** во вкладке **Аккаунты** и загрузите PNG-скин (`64×64` или `64×32`) — он подтянется в одиночную игру и на серверы автоматически."
    if any(k in low for k in ("друг", "друз", "ksinv", "сервер", "порт", "upnp")):
        return "В **KS Friends** добавляйтесь по ID `KS-XXXXXXXX` или коду `KSINV:...`. При запуске локального сервера порт открывается через UPnP; если роутер блокирует его, откройте `25565` (TCP) вручную или используйте Radmin VPN."
    if any(k in low for k in ("fps", "фпс", "лаг", "тормоз")):
        return "Выделите **4–6 ГБ ОЗУ** в Настройках (не ставьте всю память ПК) и закройте лишние программы. В KS Launcher уже включена многопоточная прогрузка чанков и высокий приоритет игры."
    return "Опишите проблему подробнее (версия Minecraft, загрузчик и скрин/лог ошибки) — постараюсь помочь, либо позовите `@Модератор`."


async def generate_answer(prompt: str) -> str:
    async with aiohttp.ClientSession() as session:
        for fn in (query_ollama, query_groq, query_gemini, query_openrouter):
            ans = await fn(session, prompt)
            if ans and ans.strip():
                return ans.strip()
    return local_faq_fallback(prompt)


def is_support_channel(channel: discord.abc.GuildChannel | discord.Thread) -> bool:
    if SUPPORT_CHANNEL_ID:
        if str(channel.id) == SUPPORT_CHANNEL_ID:
            return True
        if isinstance(channel, discord.Thread) and str(channel.parent_id) == SUPPORT_CHANNEL_ID:
            return True
    name = (getattr(channel, "name", "") or "").lower()
    parent_name = (getattr(getattr(channel, "parent", None), "name", "") or "").lower()
    keywords = ("помощ", "баг", "support", "help", "вопрос")
    return any(k in name or k in parent_name for k in keywords)


async def send_chunked(target, text: str):
    for i in range(0, len(text), 1900):
        await target.reply(text[i : i + 1900], mention_author=False)


@bot.event
async def on_ready():
    print(f"KS Support Bot запущен как {bot.user}")


@bot.event
async def on_thread_create(thread: discord.Thread):
    if not is_support_channel(thread):
        return
    await asyncio.sleep(1.5)
    try:
        starter = thread.starter_message or await thread.fetch_message(thread.id)
        text = f"Тема: {thread.name}\nСообщение: {starter.content if starter else ''}"
        ans = await generate_answer(text)
        await thread.send(ans[:1900])
    except Exception:
        pass


@bot.event
async def on_message(message: discord.Message):
    if message.author.bot:
        return

    mentioned = bot.user in message.mentions if bot.user else False
    in_support = is_support_channel(message.channel)

    if not mentioned and not in_support:
        return

    now = time.monotonic()
    last = USER_COOLDOWNS.get(message.author.id, 0.0)
    if now - last < COOLDOWN_SECONDS:
        return
    USER_COOLDOWNS[message.author.id] = now

    content = message.content
    if bot.user:
        content = content.replace(f"<@{bot.user.id}>", "").replace(f"<@!{bot.user.id}>", "").strip()

    # Читаем прикреплённый лог-файл (.txt/.log), если есть
    for att in message.attachments[:1]:
        if att.filename.endswith((".log", ".txt")) and att.size < 250_000:
            try:
                raw = (await att.read()).decode("utf-8", errors="ignore")
                content += f"\n\nФрагмент лога ({att.filename}):\n{raw[-2500:]}"
            except Exception:
                pass

    if not content:
        return

    async with message.channel.typing():
        reply = await generate_answer(content)
    await send_chunked(message, reply)


if __name__ == "__main__":
    if not DISCORD_TOKEN or DISCORD_TOKEN == "ВАШ_ТОКЕН_БОТА":
        raise SystemExit("Укажите DISCORD_TOKEN в файле .env")
    bot.run(DISCORD_TOKEN)
