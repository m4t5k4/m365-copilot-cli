# Protocol v1

## Requests

Eén JSON-object met exact `version: 1` en een `operations`-array van 1–100 items. Onbekende en dubbele properties, onbekende types, nullwaarden, commentaren en trailing commas worden geweigerd. Propertynamen en operatietypes zijn hoofdlettergevoelig. Eén omhullend Markdown-codeblok (``` of ```json) is toegestaan; begeleidende tekst niet.

| Type | Velden | Betekenis |
| --- | --- | --- |
| `tree` | `type`, optioneel `path` | Alle bestandspaden onder een directory recursief opsommen, zonder inhoud |
| `read` | `type`, optioneel `path` | Bestand lezen of directory recursief lezen |
| `write` | `type`, `path`, `changes` | Gerichte replacements in een bestaand tekstbestand |
| `create` | `type`, `path`, `content` | Nieuw tekstbestand maken; nooit overschrijven |

Een batch bevat uitsluitend tree-operaties, uitsluitend reads of uitsluitend mutaties. Voor meerdere reads gebruik je meerdere operations. Mutatiedoelen moeten uniek zijn, ook qua Windows-casing. Een doel mag geen bovenliggende directory van een ander mutatiedoel zijn. Create en write op hetzelfde bestand in één batch zijn dus niet toegestaan.

`changes` is niet leeg. Elk item bevat exact `old` en `new` als strings. `old` mag niet leeg zijn; `new` en create-`content` wel. Replacements worden in volgorde toegepast op de in-memory tussenstand. `old` moet daarin letterlijk exact eenmaal voorkomen, inclusief controle op overlappende matches. Geen regex, case folding of newline-normalisatie. Een latere replacement kan tekst van een eerdere replacement gebruiken. `old == new` is een toegestane no-op. NUL en ongeldige Unicode zijn niet toegestaan.

## Repository en paden

De dichtstbijzijnde `.git` boven of in de huidige directory bepaalt de root; zowel een `.git`-bestand als directory wordt ondersteund. Git is een runtimevereiste. Ontbreekt een repository, dan faalt de batch.

Expliciete paden zijn altijd repository-relatief met `/`. `read` zonder `path` start bij de huidige directory. `read` met `path: "."` start bij de root. Geen absolute paden, backslashes, `..`, ingebedde `.`-segmenten, lege segmenten, trailing slash, globpatronen, controltekens, Windows-reserved names of alternate data streams. Namen met afsluitende spaties of punten zijn ongeldig.

`.git` is ontoegankelijk. Symlinks, junctions, hardlinks en linked repository-ancestors worden geweigerd. Geneste repositories en submodules zijn grenzen; submodules worden ook via gitlink-entries in de index herkend als hun `.git` ontbreekt. Start de CLI in de aparte repository wanneer je die wilt benaderen.

## Read-selectie en tekst

`tree` gebruikt dezelfde recursieve Git-selectie, padregels, deduplicatie en repository-/linkgrenzen als read. Er is geen dieptelimiet. Zonder path begint tree in de huidige directory; `path: "."` selecteert de hele repository. De selectie bevat alle bestaande reguliere bestanden, ook binaire bestanden en bestanden groter dan 1 MiB. Er wordt geen bestandsinhoud gelezen; de bestandslimiet geldt daarom niet voor tree. Een expliciet bestand als tree-doel faalt met `not_a_directory`. Lege directories worden niet apart weergegeven: de folderstructuur is afleidbaar uit de bestandspaden. Ontbrekende tracked bestanden en grenzen verschijnen in `skipped`. Meerdere tree-operaties mogen in één batch; mengen met read of mutaties niet. De response moet nog steeds binnen 10 MiB blijven, anders faalt de hele batch zonder afkapping.

Tree-succes:

```json
{"version":1,"ok":true,"context":{"repository":"my-app","cwd":"."},"paths":["src/app/a.ts","src/assets/logo.png"],"skipped":[]}
```

Recursieve selectie gebruikt `git ls-files --cached --others --exclude-standard -z`. Dit combineert tracked bestanden en niet-genegeerde untracked bestanden. Git verwerkt geneste `.gitignore`, `.git/info/exclude`, globale excludes en negatieregels. Tracked ignored bestanden blijven geselecteerd. Een expliciete bestandsread omzeilt ignore-regels. Er zijn geen eigen extensie- of builddirectoryfilters.

De inhoud komt altijd uit de working tree. Bestanden worden eenmaal teruggegeven en op pad gesorteerd. UTF-8 met of zonder BOM wordt ondersteund. De BOM is metadata en wordt niet in read-`content` opgenomen. Write bewaart hem; create gebruikt UTF-8 zonder BOM. Alle regeleinden en een eventuele ontbrekende afsluitende newline blijven letterlijk behouden, behalve waar een replacement ze wijzigt.

Recursieve reads rapporteren niet-ondersteunde tekst, ontbrekende tracked bestanden en repository-/linkgrenzen onder `skipped`. Redenen zijn `unsupported_text`, `missing`, `unsupported_link`, `nested_repository`, `forbidden_path`. Ignored bestanden worden niet apart opgesomd. Een expliciete read van een ontbrekend bestand of niet-ondersteunde tekst faalt. Toegangsfouten en limietoverschrijdingen laten de hele readbatch falen; geen gedeeltelijke `files` in foutresponses. Een lege selectie slaagt.

## Responses

Alle responses bevatten `version: 1` en `ok`. Volgorde van JSON-properties is niet significant. Niet-toepasselijke velden ontbreken.

Read-succes:

```json
{"version":1,"ok":true,"context":{"repository":"my-app","cwd":"src"},"files":[{"path":"src/a.ts","content":"hello\r\n"}],"skipped":[]}
```

`repository` is een weergavenaam, geen unieke repository-ID. `cwd` en bestandspaden zijn repository-relatief. De response bevat geen absolute lokale root.

Mutatiesucces:

```json
{"version":1,"ok":true,"mutationState":"committed","results":[{"operationIndex":0,"type":"write","path":"src/a.ts","replacements":1},{"operationIndex":1,"type":"create","path":"src/b.ts"}]}
```

Fout:

```json
{"version":1,"ok":false,"mutationState":"unchanged","error":{"code":"old_not_unique","message":"old matches more than once; expected exactly 1.","operationIndex":0,"changeIndex":0,"path":"src/a.ts"}}
```

Indices zijn nulgebaseerd. Het bericht is menselijk leesbaar; clients moeten op `code` en `mutationState` vertrouwen. Parsing en simulatie rapporteren de eerste gevonden fout; het volledige request wordt structureel gevalideerd voordat inhoudelijke simulatie begint. Index en pad verschijnen waar beschikbaar. Read-fouten gebruiken eveneens `unchanged`.

| mutationState | Betekenis |
| --- | --- |
| `unchanged` | Geen blijvende mutaties van de batch; commit niet gestart of faalde vóór de eerste wijziging |
| `committed` | Alle mutaties geschreven |
| `rolled_back` | Commit faalde, oorspronkelijke bytes en door de batch aangemaakte paden hersteld |
| `indeterminate` | Minstens één herstelactie mislukte; inspectie vereist |

Foutcodes: `invalid_json`, `invalid_request`, `unsupported_version`, `mixed_batch`, `repository_not_found`, `git_unavailable`, `git_failed`, `invalid_path`, `outside_repository`, `forbidden_path`, `unsupported_link`, `nested_repository`, `not_found`, `not_a_file`, `not_a_directory`, `already_exists`, `unsupported_text`, `access_denied`, `io_error`, `duplicate_target`, `old_not_found`, `old_not_unique`, `limit_exceeded`, `concurrent_change`, `commit_failed`, `rollback_failed`, `clipboard_read_failed`, `clipboard_write_failed`.

`clipboard_read_failed` verschijnt uitsluitend in de terminal. Bij `clipboard_write_failed` verschijnt een transportmelding gevolgd door de oorspronkelijke volledige uitvoeringsresponse. Zo blijft zichtbaar of de mutatie is gecommit. Clipboardfouten veranderen de filesystemtoestand niet en starten geen rollback.

## Staging, commit en limieten

1. Parse en valideer het gehele request.
2. Lees oorspronkelijke bytes, controleer paden en simuleer alle wijzigingen in memory.
3. Controleer bestands-/batchlimieten en de omvang van de succesresponse.
4. Controleer opnieuw of oorspronkelijke bestanden dezelfde bytes hebben en create-doelen nog vrij zijn.
5. Maak ontbrekende directories aan en schrijf bestanden in operatievolgorde. Bestaande bestanden worden vóór schrijven opnieuw gecontroleerd; nieuwe bestanden gebruiken create-new-semantiek.
6. Bij een commitfout: herstel aangeraakte bestanden in omgekeerde volgorde; verwijder alleen nieuwe bestanden en nieuwe lege directories van deze batch. Ook het bestand waarvan schrijven gedeeltelijk faalde hoort bij rollback.

Limieten: 1 MiB per bestand inclusief BOM, 10 MiB per UTF-8-request en geserialiseerde UTF-8-response, 10 MiB voor oorspronkelijke plus nieuwe mutatiebytes samen. Tussenstanden van replacements moeten ook binnen de bestandslimiet blijven. Geen truncatie of tijdelijke stagingbestanden. Git-enumeratie heeft een timeout van 30 seconden per interne aanroep.

Rollback garandeert geen herstel bij processcrashes, stroomuitval of gelijktijdige filesystemwijzigingen. Er is geen journal, recovery-command, Git-reset of metadatarestore. Houd betrokken bestanden vrij van gelijktijdige edits. `write` bevat geen hash van een eerdere read: het unieke `old`-blok is de inhoudelijke preconditie; een inmiddels elders gewijzigd bestand kan dus nog steeds geldig matchen.

V1 biedt geen delete, search, build, diff, status, arbitrary shell, dry-runmodus of volledig overschrijvende write. De interne Git-processen voeren uitsluitend vaste listing-commando's uit en ontvangen geen uitvoerbare instructies uit het request.

Bronnen voor gebruikte platformsemantiek: [Git ls-files](https://git-scm.com/docs/git-ls-files), [Windows Forms clipboard](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.clipboard.settext?view=windowsdesktop-10.0).
