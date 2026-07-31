# Agent Instructions — репо `app` («Кольцо»)

**Канонические правила этого репозитория — в [CLAUDE.md](CLAUDE.md)** (структура, зоны
ответственности, CRITICAL RULES ADR-002 §4, пять правил, процесс и гейты). Этот файл их
не дублирует — прочитай CLAUDE.md полностью перед любой работой.

Каноника уровня проекта — в родительском каталоге: `../AGENT.md`, ADR — `../ADR/`.

## Non-Interactive Shell Commands

**ALWAYS use non-interactive flags** with file operations to avoid hanging on confirmation prompts (`cp -f`, `mv -f`, `rm -f`, `apt-get -y`, `ssh -o BatchMode=yes`) — `cp`/`mv`/`rm` may be aliased to `-i` mode.

<!-- BEGIN BEADS INTEGRATION v:1 profile:minimal hash:ca08a54f -->
## Beads Issue Tracker

This project uses **bd (beads)** for issue tracking. Run `bd prime` to see full workflow context and commands.

### Quick Reference

```bash
bd ready              # Find available work
bd show <id>          # View issue details
bd update <id> --claim  # Claim work
bd close <id>         # Complete work
```

### Rules

- Use `bd` for ALL task tracking — do NOT use TodoWrite, TaskCreate, or markdown TODO lists
- Run `bd prime` for detailed command reference and session close protocol
- Use `bd remember` for persistent knowledge — do NOT use MEMORY.md files

## Session Completion

**When ending a work session**, you MUST complete ALL steps below. Work is NOT complete until `git push` succeeds.

**MANDATORY WORKFLOW:**

1. **File issues for remaining work** - Create issues for anything that needs follow-up
2. **Run quality gates** (if code changed) - Tests, linters, builds
3. **Update issue status** - Close finished work, update in-progress items
4. **PUSH TO REMOTE** - This is MANDATORY:
   ```bash
   git pull --rebase
   bd dolt push
   git push
   git status  # MUST show "up to date with origin"
   ```
5. **Clean up** - Clear stashes, prune remote branches
6. **Verify** - All changes committed AND pushed
7. **Hand off** - Provide context for next session

**CRITICAL RULES:**
- Work is NOT complete until `git push` succeeds
- NEVER stop before pushing - that leaves work stranded locally
- NEVER say "ready to push when you are" - YOU must push
- If push fails, resolve and retry until it succeeds
<!-- END BEADS INTEGRATION -->

⚠ К блоку выше: `bd dolt push` не настроен — beads синхронизируется через
`.beads/issues.jsonl` в git; `git push` обязателен. Remote: `origin` =
`https://github.com/AlexanderBrolin/ring_app.git` (только HTTPS + gh credential helper,
SSH к github.com с этой машины виснет).
