# copilot-fs

Kleine .NET 10 console-CLI die JSON-operaties via het Windows-clipboard uitvoert in een lokale Git-repository. Bedoeld als filesystem-bridge voor een Microsoft 365 Copilot custom agent. V1 ondersteunt `tree`, `read`, gerichte `write`-replacements en `create`.

## Bouwen en gebruiken

Vereist Windows, Git op `PATH` en de .NET 10 SDK. De framework-dependent executable vereist de .NET 10 **Desktop Runtime**; deze is ook onderdeel van een passende Windows SDK-installatie. Er zijn geen externe NuGet-packages.

Voer uit vanuit deze solutiondirectory:

```powershell
dotnet build copilot-fs.slnx -c Release
dotnet run --project tests/CopilotFs.Tests -c Release
dotnet publish src/CopilotFs -c Release --no-self-contained -o artifacts/copilot-fs
```

Voeg de volledige map `artifacts/copilot-fs` aan je gebruikers-PATH toe of start de exe via zijn volledige pad. Bewaar alle gepubliceerde bestanden samen. Er wordt geen PATH-wijziging of globale installatie door het project uitgevoerd.

Ga vervolgens naar de repository waarmee je wilt werken:

```powershell
cd C:\code\my-app
copilot-fs
```

Het commando leest één clipboardrequest, voert het uit en vervangt het clipboard door een JSON-response. Plak die response terug in Copilot. Inspecteer mutaties daarna zelf in IDE/Git. Er zijn geen bevestigingsvragen en geen tijdelijke request-, response- of backupbestanden in de normale workflow.

Gebruik `copilot-fs --help` of `copilot-fs --version` voor informatie. Andere argumenten worden geweigerd. Gebruik bij dagelijks werken de gepubliceerde executable; `dotnet run` vereist het projectpad en kan de werkdirectory minder duidelijk maken.

## Custom agent

Kopieer de inhoud van [docs/m365-agent-instructions.md](docs/m365-agent-instructions.md) naar het instructieveld van je custom M365 Copilot-agent. Hiervoor hoeft de agent geen lokale connector of action te hebben: jij verplaatst requests en responses via het clipboard.

Begin bijvoorbeeld met: “Bekijk de Angular-app en help me de gebruikersservice aan te passen.” Geef zo mogelijk het relevante directorypad mee. De agent vraagt eerst reads, wacht op de response en bespreekt daarna de wijziging voordat hij een mutatiebatch maakt.

## Voorbeelden

De volledige repositorystructuur opvragen, zonder bestandsinhoud:

```json
{"version":1,"operations":[{"type":"tree","path":"."}]}
```

`tree` doorloopt alle niveaus onder de gekozen map en retourneert gesorteerde bestandspaden in `paths`. Zonder `path` begint hij bij de huidige directory. Git bepaalt de uitsluitingen, net als bij read: ignored untracked bestanden/folders vallen weg; tracked bestanden blijven zichtbaar. Ook binaire en grote bestanden worden genoemd. Lege directories worden niet apart opgenomen. Repository- en linkgrenzen blijven gelden. Gebruik een aparte tree-batch, gevolgd door gerichte reads.

Een read vanaf je huidige directory:

```json
{"version":1,"operations":[{"type":"read"}]}
```

Twee gerichte reads, met paden vanaf de repository-root:

```json
{"version":1,"operations":[{"type":"read","path":"src/app"},{"type":"read","path":"src/backend/Program.cs"}]}
```

Een replacement en nieuw bestand in één batch:

```json
{
  "version": 1,
  "operations": [
    {"type":"write","path":"src/app/user.service.ts","changes":[{"old":"const enabled = false;","new":"const enabled = true;"}]},
    {"type":"create","path":"src/app/models/user.ts","content":"export interface User {\n  id: string;\n}\n"}
  ]
}
```

Een bestand is maximaal 1 MiB. Requests en geserialiseerde responses zijn maximaal 10 MiB. De som van oorspronkelijke en nieuwe bytes bij mutaties is eveneens maximaal 10 MiB. Vraag kleinere reads wanneer een selectie te groot is; inhoud wordt nooit afgekapt.

## Rollback en grenzen

Alle mutaties worden eerst gevalideerd en in memory gesimuleerd. Daarna controleert de CLI dat de doelbestanden nog overeenkomen met de gelezen bytes. Bij een schrijffout probeert hij eerdere wijzigingen en nieuwe directories terug te draaien. De oorspronkelijke inhoud, BOM en ongewijzigde regeleinden blijven behouden.

Dit is **best-effort rollback**, geen crashbestendige transactie over meerdere bestanden. Een crash, stroomuitval, gelijktijdige wijzigingen of een mislukte rollback kan een gedeeltelijke toestand achterlaten. Andere programma's kunnen tussenstanden zien. Bewerk de betrokken bestanden niet tijdens een batch. Tijdstempels en overige filesystemmetadata worden niet teruggedraaid.

Paden blijven binnen de gevonden repository. `.git`, geneste repositories, submodules, symlinks, junctions en hardlinks zijn uitgesloten. Die controles zijn bedoeld voor een normale lokale developmentworkflow; de CLI is geen OS-securitysandbox tegen een ander proces dat gelijktijdig directories of links vervangt. Recursieve reads volgen Git-excludes; expliciet opgevraagde ignored bestanden mogen wel gelezen en gewijzigd worden.

| Exitcode | Betekenis |
| --- | --- |
| 0 | Uitvoering en clipboard-output geslaagd |
| 1 | Ongeldig request, validatiefout of commit zonder blijvende batchwijzigingen |
| 2 | Rollback onvolledig; inspecteer alle batchdoelen |
| 3 | Clipboard lezen of schrijven mislukt |

Bij falende clipboard-output staat de volledige uitvoeringsresponse in de terminal. Een `committed` batch is dan **wel uitgevoerd**: voer hem niet opnieuw uit. Een onvolledige rollback heeft prioriteit en behoudt exitcode 2, ook als het clipboard faalt.

Zie [docs/protocol-v1.md](docs/protocol-v1.md) voor het volledige contract.

## Projecten en tests

- `src/CopilotFs.Core`: protocolvalidatie, padbeleid, Git-selectie, staging, rollback en testbare clipboardworkflow.
- `src/CopilotFs`: Windows-console-entrypoint met STA-clipboardadapter.
- `tests/CopilotFs.Tests`: executable testsuite zonder testframework- of packageafhankelijkheden. Start met **`dotnet run --project tests/CopilotFs.Tests`**, niet `dotnet test`. Elke mislukte test geeft een niet-nul-exitcode.

De tests gebruiken eigen tijdelijke Git-repositories en ruimen die op. Ze testen Git-ignoregedrag, worktrees, submodules, Windows-links, exact matching, bytebehoud, validatie zonder writes, concurrente wijzigingen, echte read-only-fouten, geïnjecteerde commit-/rollbackfouten en clipboardfouten. Clipboardtransport wordt met functies nagebootst; de tests veranderen je echte clipboard niet.

Een handmatige clipboard-smoketest: kopieer het eerste readvoorbeeld, start de executable in een kleine Git-repository en plak de response in een editor. Controleer vervolgens een create en een replacement op een oefenbestand.
