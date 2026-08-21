# Plik konfiguracji lokalnej RDM (`rdm.config.json`)

Przewodnik po lokalnym pliku konfiguracyjnym RDM — gdzie się znajduje, jak jest wczytywany i co robi każda sekcja i każdy klucz.

> **Szukasz czegoś innego?** Większość ustawień używanych na co dzień (profile enkodera/streamingu, konta użytkowników, mapowania wyzwalaczy, większość ustawień audio jak np. czas fade-outu przy Stop) znajduje się w **bazie danych** aplikacji, nie w tym pliku — patrz [§6](#6-czego-nie-ma-w-tym-pliku). Jeśli szukasz API HTTP, zobacz osobną [Dokumentację API RDM](RDM_Dokumentacja_API.md).

---

## Spis treści

1. [Wprowadzenie](#1-wprowadzenie)
2. [Lokalizacja](#2-lokalizacja)
3. [Jak plik jest wczytywany przy starcie](#3-jak-plik-jest-wczytywany-przy-starcie)
4. [Edycja pliku](#4-edycja-pliku)
5. [Pełna dokumentacja schematu](#5-pełna-dokumentacja-schematu)
   - [5.1 `general`](#51-general)
   - [5.2 `audio`](#52-audio)
   - [5.3 `recording`](#53-recording)
   - [5.4 `streaming`](#54-streaming)
   - [5.5 `stream_titles`](#55-stream_titles)
   - [5.6 `api`](#56-api)
   - [5.7 `voicetrack`](#57-voicetrack)
   - [5.8 `scheduler`](#58-scheduler)
   - [5.9 `database`](#59-database)
   - [5.10 `hardware`](#510-hardware)
   - [5.11 `ui_state`](#511-ui_state)
   - [5.12 `mic_dsp`](#512-mic_dsp)
6. [Czego nie ma w tym pliku](#6-czego-nie-ma-w-tym-pliku)
7. [Znane nieużywane/martwe klucze](#7-znane-nieużywanemartwe-klucze)
8. [Pełny przykład z opisami](#8-pełny-przykład-z-opisami)

---

## 1. Wprowadzenie

`rdm.config.json` to plik konfiguracyjny RDM przypisany do konkretnej maszyny. Zawiera lokalne ustawienia, które muszą istnieć, zanim aplikacja w ogóle połączy się ze wspólną bazą danych (przede wszystkim same dane połączenia z bazą), plus garść innych preferencji lokalnych — zapamiętany układ okien UI, rozmiary buforów, lokalny port API itd.

To **nie** jest miejsce, w którym mieszka większość codziennej konfiguracji RDM. Profile enkodera/streamingu, zaplanowane zdarzenia, konta użytkowników, mapowania wyzwalaczy i większość zachowań audio (ustawienia z Ustawienia → Auto DJ, na przykład) są przechowywane we wspólnej bazie danych **MySQL**, ponieważ muszą być takie same na każdej maszynie w studiu. Zobacz [§6](#6-czego-nie-ma-w-tym-pliku), gdzie przebiega ta granica.

## 2. Lokalizacja

**Zapisywalny plik zawsze znajduje się pod:**

```
%ProgramData%\RDM\rdm.config.json
```

— zwykle `C:\ProgramData\RDM\rdm.config.json`.

Powód, dla którego plik znajduje się właśnie tam, a nie obok samej aplikacji, jest architektoniczny: Program Files (gdzie zainstalowany jest RDM) nie jest zapisywalny dla zwykłego użytkownika, więc każde ustawienie, które aplikacja musi trwale zapisać — łącznie z danymi połączenia z bazą potrzebnymi przy każdym starcie — musi od razu znajdować się gdzieś, gdzie zapis jest możliwy.

### Inicjalizacja przy pierwszym uruchomieniu

Przy pierwszym uruchomieniu, jeśli `%ProgramData%\RDM\rdm.config.json` jeszcze nie istnieje, RDM kopiuje na jego miejsce **szablon** z pliku, który instalator umieścił w folderze instalacyjnym w Program Files. Po tym pierwszym skopiowaniu oba pliki są od siebie niezależne — edycja szablonu nic już nie da, gdy plik docelowy istnieje.

## 3. Jak plik jest wczytywany przy starcie

Warto znać jeden szczegół startowy, szczególnie przy diagnozowaniu problemów z portem API: wbudowane API HTTP RDM (patrz [Dokumentacja API RDM](RDM_Dokumentacja_API.md)) jest hostowane *wewnątrz* procesu aplikacji desktopowej, a serwer WWW ASP.NET Core (Kestrel) musi wiedzieć, na jakim porcie nasłuchiwać, **zanim** reszta systemu konfiguracji aplikacji w ogóle wystartuje.

Żeby to zadziałało, RDM odczytuje plik konfiguracyjny **dwukrotnie** przy starcie:

1. Jednorazowy, minimalny odczyt — tylko po to, żeby pobrać `api.base_url` i ustawić zmienną środowiskową (`ASPNETCORE_URLS`) zanim zostanie utworzony builder serwera WWW. To właśnie ona faktycznie decyduje, na jakim porcie nasłuchuje Kestrel.
2. Normalne, pełne wczytanie konfiguracji, z którego korzysta reszta aplikacji (łącznie z kontrolerami RDM.API) do wszystkiego innego, z włączonym automatycznym przeładowaniem przy zmianie.

**Praktyczna konsekwencja:** zmiana samego numeru portu w `api.base_url` z poziomu Ustawienia → API działa zgodnie z oczekiwaniami (nadpisuje plik, a restart ponownie uruchamia ten dwuetapowy start). Ręczna zmiana części „host" w `api.base_url` (np. żeby API było dostępne z innych maszyn w sieci) również wymaga restartu, żeby zadziałać, i nie ma do tego kontrolki w UI — patrz [§5.6](#56-api).

## 4. Edycja pliku

Większość sekcji tego pliku ma odpowiadającą kontrolkę w **Ustawieniach** wewnątrz aplikacji desktopowej RDM — to bezpieczniejszy sposób edycji, ponieważ aplikacja waliduje wartości i sama zapisuje plik. Garść sekcji **nie ma żadnego UI** i są edytowalne wyłącznie ręcznie; są one osobno oznaczone poniżej.

Jeśli mimo to edytujesz plik ręcznie:

- **Zamknij najpierw RDM**, albo licz się z koniecznością restartu po edycji — większość sekcji jest odczytywana tylko przy starcie, bez przeładowania na żywo w trakcie działania.
- Plik musi pozostać **poprawnym JSON-em** — błąd składni zwykle spowoduje, że RDM wróci do wartości domyślnych dla uszkodzonej sekcji (a w przypadku `database` całkowicie uniemożliwi start).
- Zrób kopię zapasową pliku przed ręczną edycją; nie ma automatycznego cofania błędnej zmiany.

## 5. Pełna dokumentacja schematu

### 5.1 `general`

| Klucz                | Typ    | Domyślnie                        | Edytowalne w Ustawieniach?                 | Przeznaczenie                                                              |
| -------------------- | ------ | -------------------------------- | ------------------------------------------ | -------------------------------------------------------------------------- |
| `language`           | string | `"pl"`                           | Tak                                        | Język interfejsu.                                                          |
| `date_format`        | string | `"dddd, d MMMM yyyy"`            | Tak                                        | Format daty (.NET) używany do wyświetlania dat na ekranie.                 |
| `process_priority`   | string | `"Normal"`                       | Tak                                        | Priorytet procesu RDM w systemie.                                          |
| `results_to_show`    | int    | `100`                            | Tak                                        | Maks. liczba wierszy w różnych listach wyszukiwania/wyników.               |
| `library_page_size`  | int    | `50`                             | Tak                                        | Rozmiar strony w widokach biblioteki/kreatora playlisty/menedżera utworów. |
| `allow_one_instance` | bool   | `true`                           | Tak                                        | Blokuje uruchomienie drugiej kopii RDM na tej samej maszynie.              |
| `minimize_to_tray`   | bool   | `false`                          | Tak                                        | Minimalizuje do zasobnika systemowego zamiast paska zadań.                 |
| `always_on_top`      | bool   | `false`                          | Tak                                        | Trzyma główne okno zawsze na wierzchu.                                     |
| `enable_error_log`   | bool   | `true`                           | Tak                                        | Przełącza dodatkowy log błędów (osobny od głównego `rdm.log`).             |
| `error_log_path`     | string | `"%AppData%\RDM\logs\error.log"` | Tak (zapisuje, ale nic tego nie odczytuje) | ⚠️ **Obecnie nieużywane** — patrz [§7](#7-znane-nieużywanemartwe-klucze).  |
| `logo_path`          | string | `""`                             | Tak                                        | Ścieżka do własnego loga wyświetlanego w UI.                               |
| `show_clock`         | bool   | `true`                           | Tak                                        | Pokazuje zegar w UI.                                                       |
| `show_date`          | bool   | `true`                           | Tak                                        | Pokazuje datę w UI.                                                        |

### 5.2 `audio`

Ustawienia buforów silnika audio. **Zmiana wymaga restartu aplikacji** — są odczytywane tylko przy starcie.

| Klucz                 | Typ | Domyślnie | Edytowalne w Ustawieniach?                 | Przeznaczenie                                                                                                                                                               |
| --------------------- | --- | --------- | ------------------------------------------ | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `playback_buffer_ms`  | int | `450`     | Tak                                        | Rozmiar głównego bufora odtwarzania (ms).                                                                                                                                   |
| `mixer_buffer`        | int | `3`       | Tak                                        | Liczba buforów miksera.                                                                                                                                                     |
| `input_buffer_ms`     | int | `2500`    | Tak                                        | Rozmiar bufora wejściowego (nagrywanie/monitoring) (ms).                                                                                                                    |
| `preload_before_s`    | int | `10`      | Tak                                        | Ile sekund wcześniej wczytywany jest z wyprzedzeniem kolejny utwór.                                                                                                         |
| `fadein_manual_ms`    | int | `900`     | Tak                                        | Czas fade-in przy ręcznie uruchomionym odtwarzaniu (ms).                                                                                                                    |
| `stop_fadeout_ms`     | int | `1250`    | Tak (zapisuje, ale nic tego nie odczytuje) | ⚠️ **Obecnie nieużywane** — rzeczywista wartość fade-outu przy Stop jest przechowywana w bazie danych (Ustawienia → Auto DJ). Patrz [§7](#7-znane-nieużywanemartwe-klucze). |
| `bass_update_period`  | int | `100`     | Nie                                        | ⚠️ **Obecnie nieużywane.**                                                                                                                                                  |
| `bass_update_threads` | int | `1`       | Nie                                        | ⚠️ **Obecnie nieużywane.**                                                                                                                                                  |

### 5.3 `recording`

Domyślne ustawienia lokalnego rejestratora (Ustawienia → zakładka Streaming/Recording).

| Klucz              | Typ    | Domyślnie                      | Edytowalne w Ustawieniach? | Przeznaczenie                                                                                            |
| ------------------ | ------ | ------------------------------ | -------------------------- | -------------------------------------------------------------------------------------------------------- |
| `enabled`          | bool   | *(brak = wyłączone)*           | Tak                        | Włącza/wyłącza moduł Recording (widoczność ikony w UI). Nieobecne, dopóki nie dotkniesz tego ustawienia. |
| `output_directory` | string | `""` (domyślnie `~\Music\RDM`) | Tak                        | Domyślny folder dla nowych nagrań.                                                                       |
| `format`           | string | `"MP3"`                        | Tak                        | Domyślny format nagrywania.                                                                              |
| `bitrate_kbps`     | int    | `192`                          | Tak                        | Domyślny bitrate nagrywania.                                                                             |
| `name_prefix`      | string | `"rec"`                        | Tak                        | Prefiks nazwy pliku dla nowych nagrań.                                                                   |

### 5.4 `streaming`

Nieobecne w żadnym dostarczanym szablonie — dodawane przy pierwszym dotknięciu przełącznika Streaming w Ustawieniach.

| Klucz     | Typ  | Domyślnie            | Edytowalne w Ustawieniach? | Przeznaczenie                                                                                                                                                                   |
| --------- | ---- | -------------------- | -------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `enabled` | bool | *(brak = wyłączone)* | Tak                        | Włącza/wyłącza moduł Streaming (tylko widoczność ikony). **Same profile enkodera nie są tu przechowywane** — mieszkają w bazie danych; patrz [§6](#6-czego-nie-ma-w-tym-pliku). |

### 5.5 `stream_titles`

Eksport tekstu „teraz gra" do pliku, dla zewnętrznych playerów/stron www (Ustawienia → zakładka Stream Titles).

| Klucz                | Typ      | Domyślnie                  | Edytowalne w Ustawieniach?                 | Przeznaczenie                                                                                                                                         |
| -------------------- | -------- | -------------------------- | ------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------- |
| `enabled`            | bool     | `false`                    | Tak                                        | Włącza/wyłącza funkcję.                                                                                                                               |
| `output_file_path`   | string   | `""`                       | Tak                                        | Gdzie zapisywany jest plik tekstowy.                                                                                                                  |
| `format`             | string   | `"$artist$ - $title$"`     | Tak                                        | Szablon tekstu. Tokeny: `$artist$ $title$ $duration$ $format$ $year$ $bpm$`.                                                                          |
| `encoding`           | string   | `"UTF-8"`                  | Tak                                        | Kodowanie tekstu pliku wyjściowego (akceptuje też `"ANSI"`).                                                                                          |
| `update_on`          | string   | `"TRACK_STARTED"`          | Tak (zapisuje, ale nic tego nie odczytuje) | ⚠️ **Obecnie nieużywane** — usługa zawsze aktualizuje przy starcie utworu, niezależnie od tej wartości. Patrz [§7](#7-znane-nieużywanemartwe-klucze). |
| `fallback_artist`    | string   | `"Radio"`                  | Tak                                        | Używane, gdy utwór nie ma tagu artysty.                                                                                                               |
| `fallback_title`     | string   | `"Testowe"`                | Tak                                        | Używane, gdy utwór nie ma tagu tytułu.                                                                                                                |
| `allowed_format_ids` | string[] | `[]` (= wszystkie formaty) | Tak                                        | Ogranicza funkcję do konkretnych formatów/kategorii assetów.                                                                                          |

### 5.6 `api`

Steruje wbudowanym API HTTP RDM. Zobacz [Dokumentację API RDM](RDM_Dokumentacja_API.md), żeby dowiedzieć się, co robi samo API.

| Klucz      | Typ          | Domyślnie                 | Edytowalne w Ustawieniach?              | Przeznaczenie                                |
| ---------- | ------------ | ------------------------- | --------------------------------------- | -------------------------------------------- |
| `base_url` | string (URL) | `"http://localhost:9300"` | **Tylko port**, przez Ustawienia → API. | Base URL, pod którym nasłuchuje API/Kestrel. |

> ⚠️ **Ustawienia autoryzacji nie znajdują się tutaj.** `api.base_url` znajduje się w tej samej zakładce Ustawień co przełącznik logowania API i dane logowania, ale te (`ApiAuthEnabled`, `ApiAnonymousLocal`, `ApiUsername`, `ApiPasswordHash`) są przechowywane w **bazie danych**, nie w tym pliku. Patrz [§6](#6-czego-nie-ma-w-tym-pliku).
> 
> Zmiana samego **portu** jest wspierana z poziomu UI. Jeśli potrzebujesz, aby API było dostępne z innych maszyn w sieci, musisz ręcznie zmienić część **host** w `base_url` i zrestartować aplikację — nie ma do tego kontrolki UI, a taka zmiana ma konsekwencje dla bezpieczeństwa (wbudowane obejście logowania Basic Auth dla „zaufanego localhost" dotyczy tylko połączeń z loopback; patrz sekcja o autoryzacji w dokumentacji API).

### 5.7 `voicetrack`

Brak UI w Ustawieniach — tylko edycja ręczna.

| Klucz  | Typ    | Domyślnie       | Przeznaczenie                                   |
| ------ | ------ | --------------- | ----------------------------------------------- |
| `path` | string | `"VoiceTracks"` | Folder używany przez edytor segue/voicetracków. |

### 5.8 `scheduler`

Brak UI w Ustawieniach — tylko edycja ręczna, i **istotne dla bezpieczeństwa**.

| Klucz                      | Typ  | Domyślnie (instalator) | Przeznaczenie                                                                                                                                                                                                                      |
| -------------------------- | ---- | ---------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `allow_external_execution` | bool | `true`                 | Steruje tym, czy zaplanowane zdarzenia mogą uruchamiać akcje „uruchom program zewnętrzny". **Brakująca lub niepoprawna wartość jest traktowana jako `false`** — funkcja domyślnie jest zablokowana (bezpiecznie), nie odblokowana. |

### 5.9 `database`

Dane połączenia ze wspólną bazą danych MySQL RDM. Ustawiane przez dedykowane okno **Konfiguracji bazy danych** przy pierwszym uruchomieniu (dostępne też później), nie przez główne okno Ustawień. **Zmiana wymaga restartu.**

| Klucz            | Typ    | Domyślnie (instalator)                | Przeznaczenie                                                                 |
| ---------------- | ------ | ------------------------------------- | ----------------------------------------------------------------------------- |
| `host`           | string | `"localhost"`                         | Host MySQL.                                                                   |
| `port`           | int    | `3306`                                | Port MySQL.                                                                   |
| `name`           | string | `"rdm"`                               | Nazwa bazy danych.                                                            |
| `username`       | string | `"root"`                              | Login MySQL.                                                                  |
| `password`       | string | `""`                                  | Hasło MySQL — **przechowywane jawnym tekstem** w tym pliku.                   |
| `dump_tool_path` | string | `"mysqldump"` (rozwiązywane z `PATH`) | Tylko edycja ręczna — ścieżka do binarki `mysqldump` używanej przy backupach. |

### 5.10 `hardware`

Konfiguracja urządzeń szeregowych (RS-232) — np. sterowanie zewnętrzną matrycą audio. Nieobecne w żadnym dostarczanym szablonie; tylko edycja ręczna, brak UI w Ustawieniach.

```json
"hardware": {
  "serial_drivers": [
    { "device_id": "matrix_main", "port": "COM4", "baud_rate": 9600, "terminator": "\r\n" }
  ]
}
```

| Klucz        | Typ    | Domyślnie    | Przeznaczenie                                                                                                                                                                                                 |
| ------------ | ------ | ------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `device_id`  | string | *(wymagane)* | Identyfikator używany gdzie indziej (np. `rdm.sendSerial(deviceId, cmd)` w skryptach JavaScript — patrz [Poradnik: Instrukcja JS](Poradnik_Instrukcja_JS.md)). Wpis bez `device_id` jest po cichu ignorowany. |
| `port`       | string | *(wymagane)* | Nazwa portu COM.                                                                                                                                                                                              |
| `baud_rate`  | int    | `9600`       | Prędkość transmisji szeregowej.                                                                                                                                                                               |
| `terminator` | string | `"\r\n"`     | Terminator linii dołączany do wysyłanych komend (akceptuje escapowanie `\r`/`\n`).                                                                                                                            |

### 5.11 `ui_state`

Sekcja w całości zarządzana w runtime — RDM zapisuje ją automatycznie, żeby pamiętać pozycje/rozmiary okien i ostatnio wybraną zakładkę w różnych panelach. Nieobecna przy świeżej instalacji; pojawia się po pierwszym przesunięciu okna lub zmianie zakładki. **Nie jest przeznaczona do ręcznej edycji** — jeśli chcesz zresetować układ UI, usuń ją, nie próbuj tworzyć jej ręcznie.

```json
"ui_state": {
  "<kluczOkna>": { "x": 100, "y": 100, "width": 1280, "height": 800 },
  "<kluczPanelu>_tab": 2
}
```

### 5.12 `mic_dsp`

Łańcuch efektów/VST mikrofonu — budowany przez edytor FX mikrofonu w UI. Celowo przechowywany tutaj (lokalnie na maszynie), a nie we wspólnej bazie danych, ponieważ sloty VST odwołują się do bezwzględnych ścieżek plików DLL, które są poprawne tylko na maszynie, na której zostały wczytane. Nieobecny, dopóki nie dodasz efektu lub VST-a na ścieżce mikrofonu.

```json
"mic_dsp": {
  "fx":  [ { "type": "<MicFxType>", "params": { "<kluczParametru>": 0.0 } } ],
  "vst": [ { "dll_path": "C:\\VST\\JakiśPlugin.dll", "name": "Jakiś Plugin", "state_chunk": "<base64, opcjonalne>" } ]
}
```

## 6. Czego nie ma w tym pliku

Żeby uniknąć najczęstszego nieporozumienia: wszystko w tabeli poniżej konfiguruje się przez UI RDM, ale jest **przechowywane we wspólnej bazie danych MySQL**, nie w `rdm.config.json`. To celowe — te ustawienia muszą być takie same na każdej maszynie w studiu, albo zmieniają się zbyt często, żeby plik per-maszyna miał sens.

| Znajduje się w bazie danych                                                     | Gdzie to edytować        |
| ------------------------------------------------------------------------------- | ------------------------ |
| Profile serwerów enkodera/streamingu (host, port, dane logowania itd.)          | Ustawienia → Streaming   |
| Autoryzacja API (`ApiAuthEnabled`, `ApiAnonymousLocal`, login/hasło)            | Ustawienia → API         |
| Większość zachowań Auto DJ (crossfade, fade-out przy Stop, parametry duckingu)  | Ustawienia → Auto DJ     |
| Konta użytkowników (Admin/Operator)                                             | Ustawienia → Użytkownicy |
| Mapowania wyzwalaczy (MIDI/sprzęt)                                              | Hardware Manager         |
| Zaplanowane zdarzenia, zawartość cartwalla, zapisane playlisty, sama biblioteka | Ich własne ekrany        |

## 7. Znane nieużywane/martwe klucze

Garść kluczy jest zapisywana przez UI Ustawień (albo dostarczana w plikach szablonów), ale w chwili pisania tego dokumentu **żaden kod ich z powrotem nie odczytuje**. Ustawienie ich niczego nie zepsuje, ale nie oczekuj żadnego efektu:

- `general.error_log_path`
- `audio.stop_fadeout_ms` *(prawdziwa wartość jest w bazie danych, pod Ustawienia → Auto DJ)*
- `audio.bass_update_period`, `audio.bass_update_threads`
- `stream_titles.update_on` *(usługa zawsze aktualizuje przy starcie utworu, niezależnie od wartości)*

Jeśli diagnozujesz ustawienie, które „nie chce działać", sprawdź najpierw tę listę — zanim założysz, że plik konfiguracyjny jest uszkodzony.

## 8. Pełny przykład z opisami

Poniżej znajdują się wyłącznie realne, działające klucze z ich wartościami domyślnymi (komentarze dodane tylko na potrzeby tego dokumentu — prawdziwy JSON nie wspiera komentarzy `//`, więc usuń je, jeśli wklejasz to gdzieś realnie). Sekcje zarządzane wyłącznie w runtime (`ui_state`, `mic_dsp`) zostały pominięte, bo nie powinny być tworzone ręcznie.

```jsonc
{
  "rdm_config_version": "1.0",

  "general": {
    "language":           "pl",
    "date_format":        "dddd, d MMMM yyyy",
    "process_priority":   "Normal",
    "results_to_show":    100,
    "library_page_size":  50,
    "allow_one_instance": true,
    "minimize_to_tray":   false,
    "always_on_top":      false,
    "enable_error_log":   true,
    "logo_path":          "",
    "show_clock":         true,
    "show_date":          true
  },

  "audio": {
    "playback_buffer_ms": 450,
    "mixer_buffer":       3,
    "input_buffer_ms":    2500,
    "preload_before_s":   10,
    "fadein_manual_ms":   900
  },

  "recording": {
    "enabled":          false,
    "output_directory": "",
    "format":           "MP3",
    "bitrate_kbps":      192,
    "name_prefix":       "rec"
  },

  "streaming": {
    "enabled": false
  },

  "stream_titles": {
    "enabled":            false,
    "output_file_path":   "",
    "format":             "$artist$ - $title$",
    "encoding":           "UTF-8",
    "fallback_artist":    "Radio",
    "fallback_title":     "Testowe",
    "allowed_format_ids": []
  },

  "api": {
    "base_url": "http://localhost:9300"
  },

  "voicetrack": {
    "path": "VoiceTracks"
  },

  "scheduler": {
    "allow_external_execution": true
  },

  "database": {
    "host":     "localhost",
    "port":     3306,
    "name":     "rdm",
    "username": "root",
    "password": ""
  },

  "hardware": {
    "serial_drivers": [
      { "device_id": "matrix_main", "port": "COM4", "baud_rate": 9600, "terminator": "\r\n" }
    ]
  }
}
```

---

**Ostatnia aktualizacja**: 2026-07-18
