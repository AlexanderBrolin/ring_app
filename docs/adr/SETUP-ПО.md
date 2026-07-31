# Установка ПО для разработки

## Рабочая станция (Windows)

### Обязательное

| ПО | Что ставить | Примечание |
|---|---|---|
| Unity Hub | последний | Через него всё остальное |
| Unity 6 LTS (6000.0.x) | модули: **Windows Build Support (Mono)**, **Linux Build Support (Server)** — это headless-сервер, **Linux Build Support (Player)** — опционально под Linux-клиент | IL2CPP для MVP не нужен, Mono быстрее итерации |
| .NET SDK 8 | winget/сайт MS | Нужен C#-тулингу VS Code |
| VS Code | + расширения: **C# Dev Kit**, **Unity** (официальное от Microsoft, тянет отладчик PlayMode) | В Unity: Preferences → External Tools → VS Code |
| Git + **Git LFS** | `git lfs install` сразу после установки | Без LFS Unity-репо распухнет мгновенно; .gitattributes — на Этапе 0 |
| Claude Code | уже есть | — |
| Docker Desktop **или** WSL2 + docker | для локальной проверки серверного контейнера и меты | Можно обойтись и удалённым Debian, но локально быстрее |
| Python 3.12 + uv | для arena-meta | Твой стандартный тулинг |

### Пакеты в Unity-проект (не в систему)

| Пакет | Откуда | Зачем |
|---|---|---|
| FishNet | Asset Store (free) или GitHub FirstGearGames/FishNet | Сетевой стек |
| Input System | Package Manager (Unity) | Вместо legacy Input |
| Unity.Mathematics | Package Manager | Детерминированная математика в Simulation |
| DOTween | Asset Store (free) | Твины UI/game feel |
| Feel (More Mountains) | Asset Store, платный (~$45) | Game-feel-компоненты: hitstop, тряска, вспышки. Опционален, но окупает себя на Этапе 1 |

### MCP для Claude-агентов

Unity MCP-сервер, чтобы агент видел консоль Unity, запускал тесты и управлял Editor:
кандидаты — `CoderGamester/mcp-unity` или `justinpbarnett/unity-mcp`. Выбрать и закрепить
в CLAUDE.md на Этапе 0 (оба живут как пакет в проекте + MCP-конфиг в Claude Code).
Без MCP работать тоже можно: агент правит код, человек жмёт Play — но цикл длиннее.

### Ассеты (по мере надобности, не сразу)

- Kenney.nl — бесплатные паки (прототипирование).
- Synty POLYGON-паки — дешёвый лоуполи под финальный стиль.
- Blender — только если захочется править модели; для MVP не нужен.

## Сервер (Debian 12)

Всё по твоему стандартному профилю, нового почти нет:

```
docker + docker compose plugin
postgresql — в контейнере (compose в arena-meta)
redis     — в контейнере (там же)
```

- Game-server: образ на базе `ubuntu:24.04`/`debian:12-slim` + Linux headless билд Unity
  (Dockerfile в arena-game/server/). Никакого Unity на сервере не ставится —
  туда едет готовый билд.
- Открыть: UDP-диапазон для матчей (например 7770–7799, порт на контейнер),
  TCP 443 для меты за существующим nginx.
- GitLab Runner на этом или отдельном хосте — для CI-сборок (NUnit + Linux headless).
  Windows-билды в CI потребуют либо Windows-раннер, либо GameCI-образы с Mono —
  решение фиксируется на Этапе 0; до тех пор Windows-билд собирается локально.

## Порядок первого запуска

1. Unity Hub → Unity 6 LTS с модулями → пустой URP-проект → закоммитить с LFS.
2. VS Code + расширения, проверить отладку PlayMode.
3. FishNet + Input System в проект.
4. Выбрать и подключить Unity MCP, прописать в CLAUDE.md.
5. Этап 0 из ADR-002 §8.
