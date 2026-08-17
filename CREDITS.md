# CREDITS — сторонние ассеты

Запись по ADR-002 §4 Critical Rule 9: docs/adr/ASSETS-001-Модели-и-анимации.md
(+ amendment A8 в ADR-002 §10 после мерджа Э1). CC0 атрибуции не требует —
фиксируем всё равно. Исходные архивы и полные манифесты — вне git
(`assets-src/` у владельца: SOURCES.md, INSPECTION.md, MANIFEST.sha256).
Дата скачивания всех паков: 2026-08-02.

| Пак | Автор | Лицензия | URL | sha256 архива |
|---|---|---|---|---|
| Universal Animation Library [Standard] | Quaternius | CC0 1.0 Universal («Asset license: Creative Commons Zero v1.0 Universal» на странице; License.txt в архиве) | https://quaternius.itch.io/universal-animation-library | `cc73fc4e495b82958207316596317a3f40b9fa38065bde1027937452da537724` |
| Universal Animation Library 2 [Standard] | Quaternius | CC0 1.0 Universal (там же) | https://quaternius.itch.io/universal-animation-library-2 | `4008ea208a604773a2b2177d965f0f5d3195498b5bf838c3f5785d68e95f2a68` |
| Animated Mech Pack | Quaternius | CC0 1.0 Universal (ссылка на creativecommons.org/publicdomain/zero/1.0/ на странице пака; License.txt в поставке) | https://quaternius.com/packs/animatedmech.html | поставка Drive-папкой — sha256 поштучно в MANIFEST.sha256 (вне git) |
| Sci-Fi Essentials Kit [Standard] | Quaternius | CC0 («Free to use in personal, educational and commercial projects. (CC0 License)» на странице) | https://quaternius.itch.io/sci-fi-essentials-kit | `a08346d538aa39fbea9fa492e03620d1860fc6214eedd62a4f5db373ac6fca01` |

В репозиторий импортирован ТОЛЬКО отбор (FBX + текстуры; список —
`assets-src/INSPECTION.md`): OBJ/glTF/Blend-дубли, `_RM`-варианты, мебель и
превью-материалы паков в репо не входят.

## Сторонние библиотеки кода

Запись по тому же Critical Rule 9 (ADR-002 §4) + амендмент T11 (голос —
MetaVoiceChat вместо Dissonance). Дата вендоринга: 2026-08-16.

| Библиотека | Автор | Лицензия | URL | Версия / контрольная сумма |
|---|---|---|---|---|
| MetaVoiceChat | Connor Myers (Metater) | MIT (`LICENSE` в поставке) | https://github.com/Metater/MetaVoiceChat | тег `v4.2`, коммит `de1bfd404871be9f2327c3df1ab10b4ff08f8b25`; sha256 релизного `MetaVoiceChat.v4.2.unitypackage` — `32c19e1fac59a755036487f50dcfed618cfd0a26e00ea1e38815e1d65f339a33` |
| Concentus (в поставке MetaVoiceChat) | Logan Stromberg и правообладатели Opus (Skype, Xiph.Org, CSIRO, Microsoft и др.) | BSD 3-clause (`Concentus.2.2.2/LICENSE`) | https://github.com/lostromb/concentus | `2.2.2`, `lib/netstandard2.0/Concentus.dll` — управляемая сборка; нативных бинарников не несёт, но умеет оппортунистически подхватить системную libopus (слой `Concentus.Native`), при её отсутствии работает чистым managed-путём |
| UniVoice / UniMic / univoice-unimic-input / univoice-audiosource-output (код-предок частей MetaVoiceChat) | Vatsal Ambastha (adrenak) | MIT (все четыре) | https://github.com/adrenak/univoice · https://github.com/adrenak/unimic · https://github.com/adrenak/univoice-unimic-input · https://github.com/adrenak/univoice-audiosource-output | без версии: заимствование на уровне исходников, отмечено самим автором MetaVoiceChat в шапках `MetaVc.cs`, `Output/AudioSource/VcAudioClip.cs` и `Output/AudioSource/VcAudioSourceOutput.cs` |

Иконки `Examples/Speaking{None,Min,Mid,Max}Icon (CCO).png` в поставке помечены
CC0 (в имени файла — опечатка апстрима, «CCO» вместо «CC0»); атрибуции CC0 не
требует, фиксируем по тому же правилу, что и паки выше.

⚠ **Открытый долг: раздаваемые сборки уведомлений НЕ несут.** MIT требует
включать уведомление «in all copies», BSD (2- и 3-clause) пункт 2 —
воспроизводить его при распространении **в бинарной форме**, а
`Ring.Editor.BuildCommands` сегодня не кладёт в артефакт ни `CREDITS.md`, ни
лицензий. Заведено задачей `app-29t` (найдено ревью Т54); касается всех целей
сборки, docker-образа и уже собранного `ring-windows-client-dev.zip`.

**Периметр долга шире голосовой цепочки — он измерен по содержимому артефакта, а
не по списку зависимостей.** Кроме трёх записей выше, в каждую сборку едут два
компонента, лежащие ВНУТРИ FishNet (его `LICENSE.txt` п. 4.a прямо отсылает к
`THIRD PARTY NOTICE.md` пакета):

| Компонент | Автор | Лицензия | Где в пакете FishNet 4.7.2 |
|---|---|---|---|
| LiteNetLib | Ruslan Pyrch (2020), Benjamin Berwick (2021) | MIT | `Runtime/Transporting/Transports/Tugboat/LiteNetLib` — 139 совпадений в отгружаемой `FishNet.Runtime.dll` |
| GameKit | Benjamin Berwick | BSD 2-clause | `Runtime/Plugins/GameKit` — 3 совпадения в отгружаемой `FishNet.Runtime.dll`. ⚠ В `THIRD PARTY NOTICE.md` путь записан как `FishNet\GameKit`, по нему компонента НЕТ |

`MonoCecil` и `CodeGen Helpers` из того же уведомления в артефакт **не едут**
(`FishNet/CodeGenerating/**` — editor-side; в `Ring_Data/Managed/` их нет).
CC0-паки моделей атрибуции не требуют.

MetaVoiceChat лежит в `client/Assets/Plugins/MetaVoiceChat/` (**не** в
`ThirdParty/`: `ThirdPartyImportBootstrap.CheckJunk` отвергает любые `.cs`/`.dll`
в `ThirdParty/**` вне `_Ring/`). Дерево тега перенесено **дословно, без единой
правки исходников**; единственный написанный нами файл — `MetaVoiceChat.asmdef`
(сборки asmdef не видят предопределённых, поэтому без него `Ring.Networking` не
смог бы сослаться на пакет вовсе), плюс `.meta` к нему и `LICENSE.meta` —
их сгенерировал Unity. Опциональное шумоподавление rnnoise
(`RnnoiseVcInputFilter`) выключено собственным `#define` поставки — пакет
Adrenak RNNoise4Unity в репозиторий не вносился.
