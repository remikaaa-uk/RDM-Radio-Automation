# Instrukcja instalacji RDM

Jak uruchomić RDM — poprzez instalację wersji wydanej (release), albo przez zbudowanie ze źródeł. To przewodnik dla **użytkowników końcowych i osób hostujących RDM samodzielnie**.

> Szukasz czegoś innego? [Plik konfiguracji lokalnej RDM](RDM_plik_konfiguracji.md) opisuje szczegółowo `rdm.config.json`, a [Dokumentacja API RDM](RDM_Dokumentacja_API.md) opisuje API HTTP.

---

## Spis treści

1. [Dwa sposoby na zdobycie RDM](#1-dwa-sposoby-na-zdobycie-rdm)
2. [Wymagania systemowe](#2-wymagania-systemowe)
3. [Instalacja z instalatora wydania](#3-instalacja-z-instalatora-wydania)
4. [Po instalacji](#4-po-instalacji)
5. [Odinstalowywanie](#5-odinstalowywanie)
6. [Budowanie ze źródeł](#6-budowanie-ze-źródeł)
7. [Rozwiązywanie problemów](#7-rozwiązywanie-problemów)

---

## 1. Dwa sposoby na zdobycie RDM

| Ścieżka | Dla kogo |
|---|---|
| **[Instalator](#3-instalacja-z-instalatora-wydania)** (`RDM-Setup.exe`, z [GitHub Releases](https://github.com/remikaaa-uk/RDM-Radio-Automation/releases)) | Dla każdego, kto po prostu chce uruchomić RDM. Zawiera biblioteki silnika audio i sam proponuje instalację reszty wymagań. |
| **[Budowanie ze źródeł](#6-budowanie-ze-źródeł)** | Dla każdego, kto chce modyfikować kod, zweryfikować go samodzielnie, albo uruchomić RDM na maszynie bez korzystania z gotowego instalatora. Biblioteki silnika audio trzeba wtedy zdobyć samodzielnie — patrz [§6](#6-budowanie-ze-źródeł), dlaczego. |

## 2. Wymagania systemowe

Poniżej znajdują się wyłącznie fakty rzeczywiście wymuszane lub potwierdzone przez projekt — celowo nie zgadujemy minimalnego RAM/CPU, bo nikt tego nie testował na potrzeby tej dokumentacji.

- **System operacyjny:** Windows, **tylko 64-bit**. Instalator wprost blokuje systemy 32-bitowe i instaluje do 64-bitowego Program Files.
- **Uprawnienia administratora** są wymagane do uruchomienia instalatora (instaluje do Program Files i może instalować wymagania systemowe).
- **Miejsce na dysku:** sama zainstalowana aplikacja to około **35–50 MB**. Jeśli pozwolisz instalatorowi postawić **lokalny** serwer bazy danych (MariaDB), doliczysz do tego jeszcze **~250–400 MB**. Sam plik instalatora do pobrania waży około **12 MB**.
- **Baza danych:** serwer MariaDB lub MySQL — albo zainstalowany lokalnie przez instalator, albo istniejący serwer gdzieś w Twojej sieci, do którego masz już dane dostępowe.

## 3. Instalacja z instalatora wydania

### 3.1 Pobranie

Pobierz najnowszy `RDM-Setup.exe` ze strony **[GitHub Releases](https://github.com/remikaaa-uk/RDM-Radio-Automation/releases)** projektu. Nie buduj instalatora samodzielnie, chyba że robisz to, co opisuje [§6](#6-budowanie-ze-źródeł) — wersja z Releases to wspierana ścieżka.

### 3.2 Uruchomienie instalatora

Instalator to standardowy kreator Windows (Inno Setup) z dwiema dodatkowymi stronami poza zwykłymi krokami „wybierz folder":

**Strona bazy danych** — pyta o połączenie z MariaDB/MySQL, którego ma używać RDM: host, port, nazwa bazy, użytkownik, hasło. Sugerowane domyślne wartości (`localhost`, `3306`, `rdm`, `root`) działają, jeśli chcesz, żeby instalator postawił Ci lokalną bazę danych. **Nie musisz sam tworzyć bazy ani tabel** — RDM tworzy swój schemat i początkowe konto administratora automatycznie przy pierwszym uruchomieniu.

**Strona wymagań** — instalator sprawdza Twój system i domyślnie zaznacza tylko to, czego faktycznie brakuje:

| Komponent | Jak jest wykrywany | Co się dzieje, jeśli brakuje i jest zaznaczony |
|---|---|---|
| **.NET 10 Runtime** (Desktop + ASP.NET Core, oba wymagane) | Uruchamia `dotnet --list-runtimes` i sprawdza obecność zarówno `Microsoft.WindowsDesktop.App 10.x`, jak i `Microsoft.AspNetCore.App 10.x` | Pobierany od Microsoftu i instalowany po cichu |
| **MariaDB** (serwer bazy danych) | Sprawdza rejestr usług Windows pod kątem usługi MariaDB/MySQL | Pobierana i instalowana po cichu, z portem i hasłem roota podanymi na stronie bazy danych — **proponowane tylko, jeśli host bazy podany na poprzedniej stronie to `localhost`/`127.0.0.1`/pusty**; jeśli wskazałeś zdalny serwer, ten krok jest automatycznie pomijany |
| **Visual C++ 2015–2022 Redistributable (x64)** | Sprawdza rejestr pod kątem runtime'u VC++ x64 | Pobierany od Microsoftu i instalowany po cichu — wymagany przez natywne biblioteki audio (BASS) |

Możesz odznaczyć dowolne pole, jeśli wolisz zająć się danym komponentem samodzielnie.

> **Biblioteki silnika audio (BASS) są już dołączone do instalatora** — w odróżnieniu od trzech powyższych komponentów, nie musisz ich zdobywać ani instalować osobno przy instalacji z gotowej wersji. Patrz [§6.3](#63-biblioteki-silnika-audio-bass), jeśli budujesz ze źródeł — tam sytuacja jest inna.

### 3.3 Co się dzieje po przejściu przez kreator

1. Wybrane wymagania (jeśli jakieś zaznaczono) są pobierane i instalowane po cichu.
2. Pliki aplikacji są kopiowane do `Program Files\RDM`.
3. Szablon konfiguracji jest zapisywany do `%ProgramData%\RDM\rdm.config.json` (tylko jeśli ten plik jeszcze nie istnieje — istniejący config z poprzedniej instalacji pozostaje nietknięty), z sekcją `database` wypełnioną danymi podanymi na stronie bazy danych.
4. Tworzone są skróty na pulpicie/w Menu Start.
5. RDM się uruchamia (jeśli zostawisz zaznaczone „Uruchom RDM po instalacji"). Przy tym pierwszym uruchomieniu RDM łączy się ze skonfigurowaną bazą i tworzy swój schemat oraz początkowe konto administratora — nie ma osobnego, ręcznego kroku konfiguracji bazy.

## 4. Po instalacji

- **Plik konfiguracyjny:** `%ProgramData%\RDM\rdm.config.json` — pełny schemat opisuje [Plik konfiguracji lokalnej RDM](RDM_plik_konfiguracji.md).
- **Plik loga:** `%ProgramData%\RDM\rdm.log`.
- **Uwaga o licencji silnika audio:** dołączone biblioteki BASS (un4seen) są darmowe do użytku **niekomercyjnego**. Jeśli prowadzisz RDM dla rozgłośni **komercyjnej**, wymaga to płatnej licencji BASS bezpośrednio od un4seen — niezależnie od tego, jak pliki trafiły na Twój komputer. Ani instalator, ani sam RDM nie mogą tego rozstrzygnąć za Ciebie. Aktualne warunki sprawdź na stronie un4seen.

## 5. Odinstalowywanie

Użyj **Ustawienia → Aplikacje** (albo skrótu w Menu Start), żeby odinstalować RDM jak każdą aplikację Windows. Warto wiedzieć, czego deinstalator **nie robi**:

- **Nie usuwa** `%ProgramData%\RDM\` — Twoja konfiguracja, logi i (jeśli używane) lokalne dane `mic_dsp`/`ui_state` zostają na dysku. To celowe: ponowna instalacja RDM później podłącza się pod istniejący config zamiast zaczynać od zera.
- **Nie dotyka** serwera MariaDB/MySQL ani bazy `rdm` — bez względu na to, czy instalator postawił ją lokalnie, czy wskazałeś istniejący serwer — Twoja biblioteka i ustawienia w bazie danych zostają nietknięte. Usuń je samodzielnie (narzędziami MariaDB/MySQL), jeśli chcesz naprawdę czystego startu.

## 6. Budowanie ze źródeł

To sekcja dla deweloperów, kontrybutorów albo każdego, kto chce uruchomić RDM bez gotowego instalatora.

### 6.1 Wymagania

| Wymaganie | Uwagi |
|---|---|
| **Windows, 64-bit** | Projekt celuje w `net10.0-windows` (API specyficzne dla Windows) — obecnie nie da się go zbudować/uruchomić na Linuksie ani macOS, mimo że używa wieloplatformowego frameworka UI (Avalonia). |
| **.NET 10 SDK** | Zgodne z docelowym frameworkiem `net10.0` / `net10.0-windows` używanym w całym solution. |
| **Visual Studio 2022** (lub nowszy, z obciążeniem .NET desktop) — opcjonalnie | Wygodne do otwarcia `RDM.sln`, ale CLI `dotnet` też działa (patrz [§6.4](#64-budowanie-i-uruchamianie)). |
| **Serwer MariaDB lub MySQL** | Potrzebny, żeby faktycznie *uruchomić* aplikację (samo budowanie/kompilacja go nie wymaga, ale bez niego nie przejdziesz przez pierwsze uruchomienie). |
| **Biblioteki audio BASS** | **Nie są dołączone do repozytorium** — patrz [§6.3](#63-biblioteki-silnika-audio-bass); to jedyny krok wymagający ręcznej akcji. |
| **Inno Setup 6.1+** — opcjonalnie | Potrzebny tylko, jeśli chcesz też zbudować własny `RDM-Setup.exe`; niepotrzebny do zbudowania czy uruchomienia samej aplikacji. |

### 6.2 Pobranie źródeł

```bash
git clone https://github.com/remikaaa-uk/RDM-Radio-Automation.git
cd RDM-Radio-Automation
```

W katalogu głównym repozytorium znajduje się `RDM.sln` (plik solution — otwórz go w Visual Studio albo użyj z CLI `dotnet`) wraz z `version.props` i `Directory.Build.props` (wspólne numerowanie wersji dla każdego projektu).

### 6.3 Biblioteki silnika audio (BASS)

Silnik audio RDM jest zbudowany na **BASS** (biblioteki natywne) i **Bass.Net** (jego zarządzany wrapper .NET), oba od [un4seen](https://www.un4seen.com/). To produkty licencjonowane i są **celowo wykluczone z repozytorium źródłowego** — `libs/*.dll` jest w `.gitignore`. Budowanie ze źródeł oznacza samodzielne zdobycie tych plików.

1. Pobierz poniższe pliki ze strony un4seen.com (upewnij się, że bierzesz wersje **x64**):

   | Plik | Wymagany? | Co włącza |
   |---|---|---|
   | `Bass.Net.dll` | **Tak** — aplikacja bez niego w ogóle się nie uruchomi | Zarządzany wrapper BASS, na którym zbudowany jest cały silnik audio |
   | `bass.dll` | **Tak** | Rdzeń silnika audio |
   | `bassmix.dll` | **Tak** | Mikser/routing |
   | `basswasapi.dll` | **Tak** | Wyjście audio WASAPI |
   | `bassloud.dll` | **Tak** | Pomiar/normalizacja głośności |
   | `bass_fx.dll` | Opcjonalny | Efekty BFX na torze mikrofonu |
   | `bass_vst.dll` | Opcjonalny | Hosting wtyczek VST 2.x |
   | `bassasio.dll` | Opcjonalny | Wyjście ASIO |
   | `bassenc.dll` | Opcjonalny | Streaming (SHOUTcast/Icecast) — wymagany przez każdy z poniższych enkoderów per-format |
   | `bassenc_mp3.dll`, `bassenc_ogg.dll`, `bassenc_opus.dll` | Opcjonalne | Po jednym na format streamingu; każdy włącza tylko dany format |

2. Umieść je wszystkie bezpośrednio w folderze `libs\` w **katalogu głównym repozytorium** (czyli `RDM-Radio-Automation\libs\Bass.Net.dll`, `RDM-Radio-Automation\libs\bass.dll` itd.) — to dokładnie ta ścieżka, do której odwołuje się `RDM.Audio.csproj` przez `HintPath`/`<Content Include>`. Pliki opcjonalne są automatycznie wykrywane, jeśli są obecne (warunkowe wpisy `<Content>`), i po prostu pomijane, jeśli ich nie dodasz — stracisz wtedy tylko daną funkcję (np. brak `bassasio.dll` = brak opcji wyjścia ASIO), nie zepsujesz builda.

   > **To jest tylko miejsce, gdzie kładziesz pliki przed budowaniem — nie zostają tam.** Build kopiuje każdy z nich w konkretne miejsce obok skompilowanego pliku wykonywalnego, a **te dwa miejsca są różne**:
   > - `Bass.Net.dll` to zwykła referencja do assembly, więc trafia do **głównego folderu programu** (tego samego, w którym jest `RDM.exe`).
   > - Wszystkie natywne pliki `bass*.dll` trafiają do **podfolderu `BassLib\`** obok `RDM.exe` (to dokładnie ten sam układ, jaki tworzy instalator wersji wydanej).
   >
   > Pomyl to — np. ręcznie kopiując `bass*.dll` obok `RDM.exe` zamiast do `BassLib\`, albo `Bass.Net.dll` do `BassLib\` zamiast obok `RDM.exe` — a RDM **nie uruchomi się**. Jeśli zawsze tylko wkładasz pliki do `libs\` i pozwalasz buildowi je skopiować, nie musisz się tym przejmować — to ma znaczenie tylko przy diagnozowaniu zepsutego wyniku builda albo ręcznym kopiowaniu DLL-i.

3. **Przypomnienie o licencji** (to nie jest porada prawna — aktualne warunki sprawdź na un4seen.com): BASS jest darmowy do użytku **niekomercyjnego**; użytek **komercyjny** (np. realna komercyjna rozgłośnia nadająca na antenie) wymaga płatnej licencji BASS, niezależnie od tego, jak pliki trafiły na Twój komputer. Bass.Net ma dodatkowo własną, osobną rejestrację/licencję — patrz komentarz „Registration slot" przy `BassNet.Registration(...)` w `BassAudioEngine.cs`, jeśli przygotowujesz licencjonowany build.

### 6.4 Budowanie i uruchamianie

Z katalogu głównego repozytorium:

```bash
# Restore + build całego solution
dotnet build RDM.sln -c Debug

# Uruchomienie aplikacji desktopowej bezpośrednio
dotnet run --project src\RDM.UI\RDM.UI.csproj
```

Albo otwórz `RDM.sln` w Visual Studio i naciśnij F5 — ten sam efekt, z debugowaniem.

Przy pierwszym uruchomieniu builda deweloperskiego RDM kopiuje szablon konfiguracji leżący obok skompilowanego pliku wykonywalnego (`src\RDM.UI\bin\Debug\net10.0-windows\rdm.config.json`) do `%ProgramData%\RDM\rdm.config.json`, jeśli ten plik jeszcze nie istnieje — po jego utworzeniu edytuj kopię w `%ProgramData%`, nie szablon. Wypełnij sekcję `database` danymi do działającego serwera MariaDB/MySQL; RDM sam utworzy schemat przy pierwszym połączeniu, tak samo jak w wersji zainstalowanej. Pełny opis konfiguracji: [Plik konfiguracji lokalnej RDM](RDM_plik_konfiguracji.md).

### 6.5 Uruchamianie testów

```bash
dotnet test RDM.sln
```

### 6.6 Budowanie własnego instalatora (opcjonalnie)

Istotne tylko, jeśli chcesz wyprodukować własny `RDM-Setup.exe` (np. żeby dystrybuować zmodyfikowanego builda):

```powershell
powershell -ExecutionPolicy Bypass -File installer\publish.ps1
```

Publikuje build `win-x64` typu framework-dependent, podbija numer builda w `version.props` i — jeśli zainstalowany jest Inno Setup 6.1+ — automatycznie kompiluje `installer\Output\RDM-Setup.exe`. Jeśli Inno Setup nie zostanie znaleziony, skrypt i tak publikuje aplikację i wypisuje komendę `ISCC` do ręcznego skompilowania instalatora.

## 7. Rozwiązywanie problemów

- **Nic się nie dzieje / crash przy pierwszym uruchomieniu po zbudowaniu ze źródeł:** niemal zawsze brakujący `Bass.Net.dll` — proces odwołuje się do niego przy starcie i zawiedzie, zanim pokaże jakikolwiek interfejs czy zapisze cokolwiek do loga. Sprawdź, czy `libs\Bass.Net.dll` istnieje w katalogu głównym repozytorium.
- **Audio nie działa, ale reszta aplikacji chodzi normalnie:** sprawdź, czy wymagane natywne DLL-e BASS (`bass.dll`, `bassmix.dll`, `basswasapi.dll`, `bassloud.dll`) faktycznie trafiły do folderu `BassLib\` obok zbudowanego pliku wykonywalnego — jeśli nie było ich w `libs\` w momencie budowania, po prostu nie zostaną skopiowane, bez żadnego ostrzeżenia.
- **Nie można połączyć się z bazą danych:** sprawdź sekcję `database` w `rdm.config.json` — patrz [Plik konfiguracji lokalnej RDM §5.9](RDM_plik_konfiguracji.md#59-database).
- **Ogólne rozwiązywanie problemów:** najpierw sprawdź plik loga — `%ProgramData%\RDM\rdm.log` dla wersji zainstalowanej, albo `src\RDM.UI\bin\Debug\net10.0-windows\rdm.log` dla builda deweloperskiego uruchomionego z IDE/CLI.

---

**Ostatnia aktualizacja**: 2026-08-18
