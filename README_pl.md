# RDM — Radio Digital Manager

<p align="center">
  <a href="README.md">🇬🇧 English</a> | 🇵🇱 <strong>Polski</strong>
</p>

<p align="center">
  <img src="image/rdm.png" alt="RDM — Radio Digital Manager" width="128">
</p>

<p align="center">
  <strong>Radio Digital Manager</strong><br>
  Zintegrowane środowisko do obsługi i automatyzacji pracy rozgłośni radiowej.
</p>

## O programie

**RDM (Radio Digital Manager)** to aplikacja radiowa, która łączy w jednym programie funkcje znane z kilku różnych narzędzi wykorzystywanych przy produkcji i emisji radiowej.

Celem projektu jest stworzenie jednego, spójnego środowiska, w którym można zarządzać biblioteką audio, przygotowywać i odtwarzać playlisty, korzystać z cartwalla, obsługiwać emisję, streaming oraz mikrofon z duckingiem, a także realizować inne zadania związane z codzienną pracą radia.

RDM jest rozwijany jako projekt modułowy. Poszczególne funkcje są stopniowo integrowane w jednym interfejsie, zamiast wymagać korzystania z kilku niezależnych programów.

Mimo że program jest stworzony w technologi obsługiwanej przez Linux oraz Macosx to działa tylko na Windows 64bit. 

### Aktualny stan projektu

Projekt jest **aktywnie rozwijany**. Nie wszystkie planowane funkcje są jeszcze dostępne.

W szczególności **na obecnym etapie RDM nie posiada jeszcze schedulera odpowiedzialnego za automatyczne losowanie i tworzenie playlist**. Funkcja ta jest planowana na późniejszy etap rozwoju projektu.

Folder [`screenshots`](screenshots/) zawiera zrzuty ekranu prezentujące aktualny wygląd programu oraz poszczególne dostępne funkcje.

## Główne funkcje

W zależności od aktualnego etapu rozwoju RDM obejmuje lub rozwija m.in.:

- odtwarzanie audio i obsługę playoutu,
- zarządzanie biblioteką utworów,
- playlisty,
- cartwall,
- podgląd / Preview / AUX,
- obsługę mikrofonu, wraz z łańcuchem efektów VST,
- ducking podczas pracy mikrofonu,
- streaming,
- obsługę punktów Cue i parametrów utworów,
- edycję i przygotowanie utworów do emisji,
- obsługę wielu wyjść audio,
- konta wieloosobowe z rolami (Admin/Operator),
- integrację z bazą danych,
- interfejs graficzny dla operatora,
- API umożliwiające komunikację pomiędzy komponentami aplikacji.

> **Uwaga:** lista funkcji będzie się zmieniać wraz z rozwojem projektu. Niektóre elementy są obecnie częściowo zaimplementowane lub znajdują się w fazie rozwoju.

## Screenshots

W katalogu [`screenshots`](screenshots/) znajdują się screeny przedstawiające program oraz jego poszczególne moduły i funkcje.

Przykładowe obrazy można wykorzystać bezpośrednio w dokumentacji projektu:

```markdown
![Opis](screenshots/1.png)
```

## Stack technologiczny

- **.NET 10**
- **Avalonia UI** — interfejs użytkownika (`RDM.UI`)
- **ASP.NET Core Web API** — API (`RDM.API`)
- **MariaDB**
- **Dapper** — dostęp do bazy danych, bez Entity Framework Core
- **BASS / Bass.Net** — silnik audio

Pełny opis architektury i projektów solucji znajduje się w [`ARCHITECTURE.md`](ARCHITECTURE.md).

## Struktura projektu

Najważniejsze komponenty rozwiązania:

- `RDM.UI` — aplikacja desktopowa i interfejs użytkownika,
- `RDM.API` — Web API,
- `RDM.Core` — logika domenowa,
- `RDM.Audio` — obsługa silnika audio,
- `RDM.Infrastructure` — infrastruktura, baza danych i repozytoria,
- `RDM.Shared` — współdzielone modele i elementy.

## Uruchomienie lokalne

### 1. Konfiguracja

Skopiuj przykładowe pliki konfiguracyjne i utwórz ich lokalne odpowiedniki:

```text
src/RDM.UI/rdm.config.example.json  →  src/RDM.UI/rdm.config.json
src/RDM.API/rdm.config.example.json →  src/RDM.API/rdm.config.json
```

Następnie uzupełnij sekcję `database` danymi dostępu do MariaDB:

- host,
- port,
- użytkownik,
- hasło,
- nazwa bazy danych.

Pliki zawierające rzeczywiste dane konfiguracyjne nie powinny być dodawane do repozytorium.

### 2. Biblioteki BASS

Katalog `libs/` z natywnymi bibliotekami BASS oraz `Bass.Net.dll` **nie jest częścią repozytorium**.

Biblioteki należy pobrać bezpośrednio ze strony [un4seen.com](https://www.un4seen.com) i umieścić w katalogu `libs/`.
Wrapper Bass.Net.dll należy umieścić w folderze głównym programu.

### 3. Testy integracyjne

Testy integracyjne w `tests/RDM.Infrastructure.Tests` korzystają z rzeczywistej bazy MariaDB.

Connection string należy przekazać za pomocą zmiennej środowiskowej:

```text
RDM_TEST_DB_CONNECTION_STRING
```

Jeżeli zmienna nie zostanie ustawiona, używany jest lokalny placeholder, który nie zadziała bez skonfigurowanej bazy danych.

## Licencja i biblioteki audio

Kod źródłowy RDM jest udostępniany na licencji **MIT** — szczegóły znajdują się w pliku [`LICENSE`](LICENSE).

RDM korzysta z silnika audio **BASS** oraz wrappera **Bass.Net**, autorstwa **un4seen developments**. Biblioteki te **nie są objęte licencją MIT** i podlegają własnym warunkom licencyjnym.

BASS jest bezpłatny wyłącznie do użytku **niekomercyjnego**. Użycie komercyjne wymaga uzyskania odpowiedniej licencji od un4seen.

Biblioteki BASS nie są redystrybuowane w tym repozytorium ani w wydaniach (Releases). Należy pobrać je samodzielnie ze strony producenta.

## Status projektu

RDM jest projektem rozwijanym etapami.

Obecna wersja koncentruje się na budowie podstawowego środowiska radiowego i integracji funkcji, które w tradycyjnym workflow mogą wymagać kilku oddzielnych programów.

**Automatyczny scheduler losujący i tworzący playlisty nie jest jeszcze zaimplementowany.**

Planowane funkcje będą dodawane stopniowo wraz z rozwojem kolejnych modułów.

## Pobranie programu

Aktualne wydania programu są dostępne w sekcji Releases:

**[Pobierz RDM — Releases](https://github.com/remikaaa-uk/RDM-Radio-Automation/releases)**

---

## Dokumentacja

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — architektura projektu
- [`LICENSE`](LICENSE) — licencja projektu
- [`screenshots/`](screenshots/) — zrzuty ekranu programu i jego funkcji
