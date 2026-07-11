# EU Call Tracker za MSP iz Srbije

Microsoft stack aplikacija za pracenje EU i EU-povezanih otvorenih poziva relevantnih za mala i srednja preduzeca iz Srbije.

Stack:

- C# / .NET 6
- ASP.NET Core za lokalni web pregled
- System.Text.Json za lokalnu bazu u `data/calls.json`
- PowerShell / Windows Task Scheduler za automatizaciju
- bez Python-a, Node-a i eksternih NuGet paketa

## Pokretanje

Iz ovog foldera:

```powershell
dotnet restore .\EuProjects.sln --configfile .\NuGet.Config
dotnet run --project .\src\EuCallTracker -- update
dotnet run --project .\src\EuCallTracker -- report --open-only --min-score 4
dotnet run --project .\src\EuCallTracker -- serve
```

Web pregled se otvara na:

```text
http://localhost:5055
```

Svaki poziv na web stranici ima `Apply` sekciju. Kada je otvoris, prikazuje se spisak dokumenata koje treba pripremiti: dokumenti firme, projektna dokumentacija, budzet/finansije i partneri/podnosenje.

Glavni izvestaj ima link `Alert izvori`, koji otvara stranicu sa newsletterima, hubovima i portalima koje treba pratiti za EU, kaskadna, cross-border i nacionalna SME finansiranja za Srbiju, Maltu i Sloveniju.

Dodali smo i `USA plan`, posebnu stranicu za pripremu US entiteta, SBIR/STTR, NSF, NIH, DOE, NASA, DIU, DARPA, ARPA-H, Challenge.gov i Grants.gov rute.

Brz tekstualni pregled:

```powershell
dotnet run --project .\src\EuCallTracker -- list --open-only --min-score 4
```

## Fajlovi

- `config/sources.json` - lista izvora i reci za prepoznavanje relevantnosti
- `data/calls.json` - lokalna baza nadjenih poziva
- `reports/open-calls.html` - HTML izvestaj
- `reports/alert-sources.html` - stranica sa newsletterima, hubovima i alert servisima
- `reports/usa-readiness.html` - USA readiness mapa za grantove, SBIR/STTR i federalne programe
- `reports/open-calls.csv` - CSV izvestaj za Excel
- `src/EuCallTracker` - C# / ASP.NET Core aplikacija

## Dodavanje izvora

U `config/sources.json` dodaj novi zapis u `sources`:

```json
{
  "name": "Naziv izvora",
  "level": "Local / Regional / Serbia national / EU central",
  "type": "web",
  "url": "https://primer.rs/konkursi",
  "detail_fetch_limit": 20,
  "enabled": true
}
```

Za RSS feed:

```json
{
  "name": "Naziv RSS izvora",
  "level": "EU central",
  "type": "rss",
  "url": "https://primer.eu/feed.xml",
  "enabled": true
}
```

## Automatizacija na Windowsu

U Task Scheduler-u napravi dnevni zadatak koji pokrece:

```powershell
.\scripts\Register-EuCallTrackerTask.ps1
```

Rucno dnevno osvezavanje mozes pokrenuti preko:

```powershell
.\scripts\Update-EuCalls.ps1
```

## Kako se racuna relevantnost

Svaki rezultat dobija score:

- osnovne reci kao `call`, `grant`, `javni poziv`, `konkurs`
- jace se boduju reci kao `SME`, `MSP`, `startup`, `mala i srednja preduzeca`
- dodatno se boduju `Serbia`, `Srbija`, `Western Balkans`, `IPA`
- arhivirani, zatvoreni ili rezultatski linkovi se spustaju nize

Reci se menjaju u delu `filters` u `config/sources.json`.

Napomena: alat moze da prati samo izvore koji su uneseni u konfiguraciju. Zato je lista izvora otvorena i lako prosiriva za EU, nacionalni, regionalni i lokalni nivo.
