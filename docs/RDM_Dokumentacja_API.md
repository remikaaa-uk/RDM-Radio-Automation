# Dokumentacja API HTTP RDM

Przewodnik dla każdego, kto chce zbudować coś na bazie API HTTP i WebSocket RDM — zewnętrzne dashboardy, skrypty automatyzujące, alternatywne panele sterowania, integracje z systemami playout/traffic itd.

> **Szukasz czegoś innego?** Jeśli chcesz uruchamiać logikę *wewnątrz* samego RDM (wyzwalaną padami, makrami albo harmonogramem), zobacz osobny [Poradnik: Instrukcja JS](Poradnik_Instrukcja_JS.md) — to osadzony sandbox Jint, niezwiązany z tym API HTTP.

---

## Spis treści

1. [Wprowadzenie](#1-wprowadzenie)
2. [Pierwsze kroki](#2-pierwsze-kroki)
3. [Autoryzacja](#3-autoryzacja)
4. [Konwencje](#4-konwencje)
5. [API WebSocket (zdarzenia w czasie rzeczywistym)](#5-api-websocket-zdarzenia-w-czasie-rzeczywistym)
6. [Dokumentacja REST API](#6-dokumentacja-rest-api)
   - [6.1 Assets (biblioteka)](#61-assets-biblioteka)
   - [6.2 Import i skanowanie](#62-import-i-skanowanie)
   - [6.3 Waveform](#63-waveform)
   - [6.4 Playback (główny odtwarzacz)](#64-playback-główny-odtwarzacz)
   - [6.5 Elementy playlisty (kolejka na żywo)](#65-elementy-playlisty-kolejka-na-żywo)
   - [6.6 Zapisane playlisty](#66-zapisane-playlisty)
   - [6.7 Aux Players](#67-aux-players)
   - [6.8 Cartwall](#68-cartwall)
   - [6.9 Mikrofon](#69-mikrofon)
   - [6.10 Nagrywanie](#610-nagrywanie)
   - [6.11 Encoder / Streaming](#611-encoder--streaming)
   - [6.12 Streams (streamy internetowe jako assety)](#612-streams-streamy-internetowe-jako-assety)
   - [6.13 Kategorie, podkategorie i gatunki](#613-kategorie-podkategorie-i-gatunki)
   - [6.14 Formaty](#614-formaty)
   - [6.15 Now Playing](#615-now-playing)
   - [6.16 Dziennik zdarzeń](#616-dziennik-zdarzeń)
   - [6.17 Zdarzenia zaplanowane](#617-zdarzenia-zaplanowane)
7. [Znane nieregularności i niespójności](#7-znane-nieregularności-i-niespójności)
8. [Ściągawka: przykłady curl](#8-ściągawka-przykłady-curl)

---

## 1. Wprowadzenie

RDM (Radio Digital Manager) udostępnia API REST + WebSocket obejmujące niemal wszystko, co potrafi sama aplikacja desktopowa: zarządzanie biblioteką, główny odtwarzacz, kolejkę na żywo, zapisane playlisty, aux playery, cartwall, ścieżkę mikrofonu/duckingu, nagrywanie, enkodery streamingowe, harmonogram oraz strumień zdarzeń w czasie rzeczywistym.

Ważna kwestia architektoniczna: **API nie jest osobnym procesem serwera**. To aplikacja ASP.NET Core (projekt `RDM.API`) hostowana *w tym samym procesie* co aplikacja desktopowa RDM (`RDM.UI`, oparta na Avalonii). Działa wyłącznie wtedy, gdy działa aplikacja desktopowa, na tej samej maszynie, i domyślnie nasłuchuje tylko na `localhost`. Nie ma publicznego endpointu w chmurze, wbudowanego hostingu wielo-najemcowego ani interfejsu Swagger/OpenAPI — ten dokument jest podstawowym źródłem referencji.

## 2. Pierwsze kroki

- **Base URL:** domyślnie `http://localhost:9300` (konfigurowalny — patrz niżej).
- **Ścieżka bazowa API:** każdy endpoint REST znajduje się pod `/api/v1/...`.
- **Aplikacja musi być uruchomiona.** Ponieważ API jest hostowane wewnątrz RDM.UI, po zamknięciu aplikacji desktopowej API jest niedostępne.

### Skąd bierze się base URL

Port (a w zasadzie i host) jest odczytywany z `rdm.config.json → api.base_url` przy starcie:

```json
{
  "api": { "base_url": "http://localhost:9300" }
}
```

Port można zmienić w Ustawieniach RDM (ograniczenie 1024–65535); to nadpisuje `api.base_url` w pliku konfiguracyjnym. Nie ma kontrolki UI dla części „host" — jeśli potrzebujesz, aby API było dostępne z innych maszyn w sieci, musisz ręcznie zmienić `base_url` (i wcześniej zapoznać się z konsekwencjami dla autoryzacji opisanymi w kolejnej sekcji).

### Pierwsze zapytanie

```bash
curl -u operator:twojehaslo http://localhost:9300/api/v1/nowplaying
```

Jeśli wszystko skonfigurowane poprawnie, dostaniesz w odpowiedzi JSON `NowPlayingDto`. Jeśli dostaniesz `401`, zobacz sekcję [Autoryzacja](#3-autoryzacja) poniżej.

## 3. Autoryzacja

RDM używa **HTTP Basic Authentication** — bez OAuth, bez kluczy API, bez tokenów bearer.

```
Authorization: Basic base64(username:password)
```

Przykład:

```bash
curl -u operator:twojehaslo http://localhost:9300/api/v1/nowplaying
# curl sam zbuduje nagłówek Authorization: Basic ... z parametru -u
```

### Jak faktycznie działa weryfikacja

Pojedynczy globalny middleware (`AuthMiddleware`) blokuje **każde** żądanie, łącznie z handshake'iem WebSocket — w kodzie nigdzie nie ma atrybutów `[Authorize]`; to jedyna bramka. Sprawdza, w kolejności:

1. **Jeśli ustawienie „wymagaj logowania" jest wyłączone** (`ApiAuthEnabled = false` w Ustawieniach) — każde żądanie jest traktowane jako uwierzytelnione z rolą **Admin**. Dane logowania w ogóle nie są potrzebne.
2. **W przeciwnym razie, jeśli włączone jest „zezwól lokalnie bez logowania"** (`ApiAnonymousLocal = true`) **i** żądanie przychodzi z adresu loopback (`127.0.0.1`/`::1`) — jest traktowane jako uwierzytelnione z rolą **Operator**, bez danych logowania. To celowo **nie** obejmuje innych maszyn w Twojej sieci LAN, tylko procesy na tej samej maszynie co RDM.
3. **W pozostałych przypadkach wymagany jest poprawny nagłówek `Authorization: Basic`.** Sprawdzany jest wobec dwóch źródeł, w kolejności:
   - pojedynczego zapasowego użytkownika API skonfigurowanego w Ustawieniach (login + hasło zahaszowane BCrypt) → rola **Operator**;
   - tabeli kont użytkowników studia (te same konta Admin/Operator, które służą do logowania wieloosobowego w aplikacji desktopowej) → rola pobierana z rekordu tego użytkownika.

### Role

Istnieją dokładnie dwie role: **`Admin`** i **`Operator`**. Zdecydowana większość endpointów akceptuje obie role równorzędnie — API RDM jest celowo permisywne do codziennej zdalnej obsługi. Niewielka liczba endpointów *zarządzających* (w odróżnieniu od *operacyjnych*) jest zarezerwowana wyłącznie dla Admina; są one oznaczone indywidualnie w dokumentacji poniżej. Podsumowując, działania tylko-dla-Admina to:

- Tworzenie, edycja i usuwanie **profili encodera/streamingu** (uruchamianie/zatrzymywanie istniejącego profilu jest dostępne dla Operatorów).
- Tworzenie, edycja i usuwanie **zdarzeń zaplanowanych** (Operator *może* nadal przełączyć flagę `skip_next` na istniejącym zdarzeniu przez `PATCH`, ale nie może zmienić jego stanu `enabled` ani niczego innego).

### Odpowiedź w razie błędu

Brak lub nieprawidłowe dane logowania dają:

```
HTTP/1.1 401 Unauthorized
WWW-Authenticate: Basic realm="RDM API"
Content-Type: application/json

{ "errorCode": "UNAUTHORIZED", "message": "Valid credentials required.", "traceId": "..." }
```

Uwierzytelnione, ale niewystarczająco uprawnione żądanie do endpointu tylko-dla-Admina dostaje `403 Forbidden` w tym samym formacie, z komunikatem w stylu *„Only Administrators are allowed to manage streaming profiles."*

## 4. Konwencje

### Ścieżka bazowa i wersjonowanie

Każdy kontroler jest zamontowany pod **`/api/v1/...`**. Nie ma na razie innej wersji w użyciu; segment wersji to stały literał, bez negocjacji treści.

Dwa endpointy celowo łamią ten wzorzec (znajdują się pod `/api/v1/track/{id}` — liczba pojedyncza — zamiast `/api/v1/assets/{id}`): pełny odczyt **szczegółów** i **aktualizacja** assetu. Wszystko inne związane z assetami używa `/api/v1/assets/...`.

### Wielkość liter w JSON

Odpowiedzi są serializowane z nazwami pól w **camelCase** (domyślne zachowanie `System.Text.Json` w ASP.NET Core — nic niestandardowego nie jest skonfigurowane). Ciała żądań są wiązane bez rozróżniania wielkości liter, więc wysyłanie camelCase jest bezpieczną konwencją także na wejściu.

**Dwa udokumentowane wyjątki**, warte zapamiętania:
- Ciało odpowiedzi `POST /api/v1/assets/{id}/waveform/request` używa **snake_case** (`asset_id`, nie `assetId`) — zostało napisane jako surowy obiekt anonimowy, a nie przez rekord DTO.
- **Wszystkie payloady zdarzeń WebSocket używają snake_case** (`asset_id`, `duration_ms` itp.) — patrz [§5](#5-api-websocket-zdarzenia-w-czasie-rzeczywistym). Zewnętrzna koperta (`eventId`, `eventType`, `timestamp`) jest camelCase; tylko wewnętrzny obiekt `payload` jest snake_case.

### Standardowy format błędu

Ilekroć endpoint zwraca błąd — czy to jawny błąd walidacji, czy nieobsłużony wyjątek wydostający się z serwera — ciało odpowiedzi ma ten kształt:

```
ErrorResponseDto {
  errorCode: string
  message:   string
  traceId:   string
}
```

Mapowanie nieobsłużonych wyjątków na status/kod (z globalnego middleware'u obsługi błędów):

| Typ wyjątku | Status | `errorCode` |
|---|---|---|
| `DuplicateAssetException` | 409 Conflict | `ASSET_ALREADY_EXISTS` |
| `KeyNotFoundException` | 404 Not Found | `RESOURCE_NOT_FOUND` |
| `ArgumentException` | 400 Bad Request | `BAD_REQUEST` |
| `UnauthorizedAccessException` | 401 Unauthorized | `UNAUTHORIZED` |
| `InvalidOperationException` | 409 Conflict | `BAD_REQUEST` *(status to 409, ale kod to nadal `BAD_REQUEST` — tak jest w kodzie, to nie literówka z naszej strony)* |
| cokolwiek innego | 500 Internal Server Error | `INTERNAL_ERROR` *(ogólny komunikat; żadne szczegóły wewnętrzne nie wyciekają)* |

Poszczególne endpointy zwracają też własne, jawne wartości `errorCode` dla spodziewanych przypadków błędów (`ASSET_NOT_FOUND`, `NO_PLAYLIST`, `SLOT_EMPTY` itd.) — są wymienione przy każdym endpoincie poniżej.

> Jeden kontroler nie trzyma się tej konwencji — patrz [§7](#7-znane-nieregularności-i-niespójności).

### Paginacja

Kilka endpointów listujących dzieli wspólną konwencję parametrów zapytania `limit`/`offset` (zamiast numerów stron):

| Endpoint | zakres `limit` | domyślny `limit` | domyślny `offset` |
|---|---|---|---|
| `GET /api/v1/assets` | 1–1000 | 50 | 0 |
| `GET /api/v1/nowplaying/history` | 1–500 | 50 | 0 |
| `GET /api/v1/events/log` | 1–500 | 50 | *(brak offsetu)* |

Wartości spoza zakresu zwracają `400 Bad Request`.

## 5. API WebSocket (zdarzenia w czasie rzeczywistym)

RDM wysyła zdarzenia na żywo przez zwykły (nie-SignalR) WebSocket ASP.NET Core.

- **Połącz się z:** `ws://localhost:9300/api/v1/ws` (lub `wss://`, jeśli sam postawisz przed tym TLS).
- **Autoryzacja:** ta sama bramka Basic-Auth (`AuthMiddleware`) dotyczy żądania upgrade tak samo jak każdego zapytania REST — wyślij nagłówek `Authorization` (albo połącz się z loopback z włączonym `ApiAnonymousLocal`).
- **Kierunek:** w praktyce **tylko serwer → klient**. Serwer odczytuje przychodzące ramki wyłącznie po to, by wykryć ramkę Close; nie przetwarza żadnej treści wiadomości wysłanej przez klienta. Nie ma protokołu subskrypcji/filtrowania — otrzymujesz każde zdarzenie.
- **Dostarczanie:** każde połączenie ma ograniczoną wewnętrzną kolejkę (pojemność 50). Wolny lub zablokowany klient po zapełnieniu kolejki po cichu **traci najstarsze zdarzenia** — nie ma powtórki ani uzupełnienia po ponownym połączeniu, więc traktuj ten strumień jako best-effort i po ponownym połączeniu zsynchronizuj stan przez REST (np. `GET /nowplaying`).

### Koperta wiadomości

```
WebSocketFrameDto {
  eventId:   string   // GUID
  eventType: string   // patrz tabela poniżej
  timestamp: string   // ISO-8601 UTC
  payload:   object   // zależne od zdarzenia — klucze snake_case, patrz uwaga w §4
}
```

### Katalog zdarzeń

| `eventType` | pola `payload` |
|---|---|
| `TRACK_STARTED` | `asset_id, title, artist, duration_ms, scheduled_at (nullable), vu_offset_db` |
| `TRACK_ENDED` | `asset_id, reason, ended_at` |
| `ASSET_IMPORTED` | `asset_id, title, artist` |
| `LOUDNESS_ANALYZED` | `asset_id, lufs, true_peak` |
| `SCHEDULE_CHANGED` | `event_id, name, change_type ("FIRED"\|"SKIPPED"), result` *(pole `result` obecne tylko przy `change_type` = `FIRED`)* |
| `PLAYLIST_MODE_CHANGED` | `previous_mode, mode` |
| `PLAYLIST_UPDATED` | `playlist_id` |
| `PLAYLIST_STOPPED` | `playlist_id, reason` |
| `DEAD_AIR_WARNING` | `silence_ms, mode` |
| `WAVEFORM_READY` | `asset_id` |
| `PFL_ENDED` | `asset_id` |
| `CART_TRIGGERED` | `slot_id, duration_ms, label` *(label jest obecnie zawsze `null`)* |
| `CART_STOPPED` | `slot_id` |
| `STREAM_META_CHANGED` | `asset_id, stream_title` |

### Przykładowy klient (JavaScript)

```javascript
const auth = btoa("operator:twojehaslo");
const ws = new WebSocket("ws://localhost:9300/api/v1/ws", [], { headers: { Authorization: `Basic ${auth}` } });
// Uwaga: przeglądarkowy WebSocket nie pozwala ustawiać dowolnych nagłówków — użyj biblioteki WS
// wspierającej Basic Auth w URL-u (ws://user:pass@host:port/...) albo klienta po stronie serwera.

ws.onmessage = (msg) => {
  const frame = JSON.parse(msg.data);
  console.log(frame.eventType, frame.payload);
};
```

## 6. Dokumentacja REST API

O ile nie zaznaczono inaczej, każdy poniższy endpoint wymaga autoryzacji jak opisano w [§3](#3-autoryzacja) i akceptuje obie role.

### 6.1 Assets (biblioteka)

Ścieżka bazowa: `/api/v1/assets` (plus dwa nadpisania absolutne pod `/api/v1/track/{id}`).

#### `GET /api/v1/assets`
Wyszukiwanie/listowanie assetów biblioteki.

- **Query:** `q?` (wyszukiwanie tekstowe), `asset_type?`, `format_id?`, `status?` (`ACTIVE`\|`DISABLED`\|`PENDING_REVIEW`\|`ALL`), `genre?`, `subcategory_id?`, `limit` (1–1000, domyślnie 50), `offset` (domyślnie 0), `sort?`, `sort_dir?` (`desc` lub rosnąco).
- **Odpowiedź `200`:**
  ```
  AssetSearchEnvelopeDto {
    total: int, limit: int, offset: int
    items: AssetDto[]
  }
  AssetDto {
    assetId, assetType, formatName?, subcategoryName?
    title, artist?, album?, durationMs: uint
    bpm?: decimal, year?: int, rating?: byte, status
    loudnessLufs?: decimal, mood?, gender?, language?, genre?
    playCount: uint, createdAt: DateTime
    cueMarkers?: CueMarkersDto
  }
  ```
- **Błędy:** `400` przy `limit`/`offset` spoza zakresu lub nierozpoznanym `asset_type`/`status`.

#### `GET /api/v1/assets/{id}`
Pobiera pojedynczy asset (skrócony kształt, ten sam `AssetDto` co wyżej). `404 ASSET_NOT_FOUND`, jeśli brak.

#### `GET /api/v1/assets/by-path?path=...`
Wyszukanie assetu po ścieżce pliku na dysku. `400`, jeśli `path` puste; goły `404`, jeśli nie znaleziono.

#### `POST /api/v1/assets/analyze-loudness`
Kolejkuje analizę głośności (LUFS) dla jednego lub wielu assetów.
- **Ciało:** `{ assetIds: string[] }` — `400`, jeśli puste.
- **Odpowiedź `202`:** `{ queued: int, message }`.

#### `POST /api/v1/assets/analyze-cue`
Kolejkuje automatyczne wykrywanie punktów cue.
- **Ciało:** `{ assetIds: string[], startDb: double, nextStartDb: double, endDb: double }` — `400`, jeśli `assetIds` puste.
- **Odpowiedź `202`:** `{ queued: int, message }`.

#### `GET /api/v1/track/{id}`
Pełny rekord szczegółowy assetu (wszystko edytowalne w edytorze utworu). `404 ASSET_NOT_FOUND`.
```
AssetDetailDto {
  assetId, assetType, formatId?, subcategoryId?
  title, artist?, album?, durationMs: uint
  rdmFilePath?, streamUrl?, imagePath?
  bpm?: decimal, year?: int, rating?: byte
  mood?, gender?, language?, genre?, comments?
  isDamaged: bool, isVariableDuration: bool, status
  startDate?, endDate?: DateTime, playLimit?: uint, playCount: uint
  lastPlayedAt?: DateTime
  loudnessLufs?: decimal, loudnessPeak?: decimal
  cueMarkers?: CueMarkersDto
}
CueMarkersDto {  // wszystkie double?, w sekundach
  start, intro, ramp2, ramp3, outro, startNext, fadeOut, fadeEnd,
  end, hookIn, hookFade, hookOut, loopIn, loopOut, anchor
}
```

#### `PUT /api/v1/track/{id}`
Pełna aktualizacja metadanych assetu (zapisuje też best-effort plik towarzyszący `.rdm` obok pliku audio).
- **Ciało:** `UpdateAssetRequestDto` — `title` wymagane; `status` wymagany (`ACTIVE`\|`DISABLED`\|`PENDING_REVIEW`); `assetType?`, jeśli podany, musi być `Track`\|`Sweeper`\|`InternetStream`; plus `artist?, album?, formatId?, subcategoryId?, bpm?, year?, rating?, mood?, gender?, language?, genre?, comments?, startDate?, endDate?, playLimit?, cueMarkers?, imagePath?, streamUrl?, isVariableDuration?`.
- **Odpowiedź `200`:** `{ assetId, updatedAt }`. **Błędy:** `400` przy pustym tytule/złym enumie, `404 ASSET_NOT_FOUND`.

#### `PATCH /api/v1/assets/{id}/status`
Zmienia tylko pole statusu.
- **Ciało:** `{ status }` (`ACTIVE`\|`DISABLED`\|`PENDING_REVIEW`).
- **Odpowiedź `200`:** `{ assetId, status, updatedAt }`. **Błędy:** `400`, `404`.

#### `DELETE /api/v1/assets/{id}`
- **Query:** `deleteFile` (bool, domyślnie `false`) — usuwa też plik audio.
- **Odpowiedź `200`:** `{ assetId, fileDeleted: bool }`. **Błędy:** `404`.

#### `POST /api/v1/assets/batch-delete`
- **Ciało:** `{ assetIds: string[], deleteFiles: bool }`.
- **Odpowiedź `200`:** `{ deleted: int, filesDeleted: int }`.

#### `POST /api/v1/assets/purge-orphans`
Usuwa rekordy z bazy, których pliki nie istnieją już na dysku. Bez ciała.
- **Odpowiedź `200`:** `{ deleted: int, deletedTitles: string[] }`.

#### `POST /api/v1/assets/optimize`
Uruchamia konserwację/optymalizację bazy danych. Bez ciała.
- **Odpowiedź `200`:** `{ success: bool, durationMs: long }`.

### 6.2 Import i skanowanie

Oba zamontowane pod `/api/v1/assets`.

#### `POST /api/v1/assets/import`
Import pojedynczego pliku do biblioteki (zadanie w tle).
- **Ciało:** `{ filePath, assetType, formatId?, subcategoryId?, readRdm: bool, readMmd: bool, readWfrm: bool, readId3: bool }` — `filePath` wymagane.
- **Odpowiedź `202`:** `{ importId, status: "QUEUED", filePath }`.

#### `GET /api/v1/assets/import/{importId}`
Sprawdza status zadania importu.
- **Odpowiedź `200`:** `{ importId, status, assetId?, title?, artist?, completedAt?, isDuplicate: bool }`. **Błędy:** `404`.

#### `POST /api/v1/assets/id3/peek`
Odczyt tagów ID3/pliku bez importu.
- **Ciało:** `{ filePath }`. **Błędy:** `400` pusta ścieżka, `404` plik nieznaleziony, `422` tagi nieczytelne.
- **Odpowiedź `200`:** `{ title?, artist?, album?, year?, bpm?, genre?, durationMs?, pictureBase64?, pictureMimeType? }`.

#### `POST /api/v1/assets/scan`
Skanuje wiele plików w poszukiwaniu kandydatów na nowe utwory (zadanie w tle).
- **Ciało:** `{ filePaths: string[] }` — `400`, jeśli puste.
- **Odpowiedź `202`:** `{ scanId, status: "QUEUED" }`.

#### `GET /api/v1/assets/scan/{scanId}`
Sprawdza status zadania skanowania.
- **Odpowiedź `200`:** `{ scanId, status, done: int, total: int, completedAt? }`. **Błędy:** `404`.

#### `GET /api/v1/assets/scan/{scanId}/results`
Pobiera wyniki po zakończeniu skanowania.
- **Odpowiedź `200`:** tablica `{ filePath, filename, artist?, title?, durationMs?, folder? }`.
- **Błędy:** `404` nieznane zadanie, `409 SCAN_NOT_COMPLETED`, jeśli wciąż trwa.

### 6.3 Waveform

Ścieżka bazowa: `/api/v1/assets/{id}/waveform`.

#### `GET /api/v1/assets/{id}/waveform`
Pobiera wcześniej wygenerowaną falę dźwiękową.
- **Odpowiedź `200`:** `application/octet-stream` — spakowana gzipem, skwantowana bajtowo fala (**nie JSON**).
- **Błędy:** `404 WAVEFORM_NOT_FOUND`, jeśli jeszcze nie wygenerowano — najpierw wywołaj endpoint poniżej.

#### `POST /api/v1/assets/{id}/waveform/request`
Kolejkuje generowanie fali dla assetu.
- **Odpowiedź `202`:** `{ asset_id, message }` — **uwaga na snake_case**, patrz [§4](#4-konwencje).
- **Błędy:** `404 ASSET_NOT_FOUND`, `422 FILE_NOT_ACCESSIBLE`, jeśli plik audio jest niedostępny z tej maszyny.

### 6.4 Playback (główny odtwarzacz)

Ścieżka bazowa: `/api/v1/playlist`.

#### `POST /api/v1/playlist/play` · `POST /pause` · `POST /stop`
Bez ciała. **Odpowiedź `200`:** `PlaybackStatusResponseDto { status: "OK", state }`.

#### `POST /api/v1/playlist/loop`
Przełącza powtarzanie bieżącego elementu kolejki (powtarzanie na poziomie playlisty, nie punkt pętli silnika audio).
- **Ciało:** `{ enabled: bool }`. **Odpowiedź:** `PlaybackStatusResponseDto` z `state` `LOOP_ON`/`LOOP_OFF`.

#### `POST /api/v1/playlist/next`
Przechodzi do następnego elementu kolejki.
- **Odpowiedź `200`:** `{ status, assetId, title, artist? }`. **Odpowiedź `204`:** jeśli nic teraz nie gra (kolejka wyczerpana).

#### `POST /api/v1/playlist/reset`
- **Odpowiedź `200`:** `{ status, positionMs: uint }`.

#### `POST /api/v1/playlist/play/{assetId}`
Przechodzi bezpośrednio do konkretnego assetu z biblioteki.
- **Odpowiedź `200`:** `{ status, assetId, title, artist? }`. **Błędy:** `404 ASSET_NOT_FOUND`, `422 ASSET_DAMAGED`.

#### `POST /api/v1/playlist/pfl/{assetId}`
Odsłuch (pre-fade listen) assetu z biblioteki (na słuchawki/monitor, nie na główne wyjście).
- **Odpowiedź `200`:** `PlaybackStatusResponseDto { state: "PFL_PLAYING" }`. **Błędy:** goły `404`, jeśli asset lub jego plik są niedostępne.

#### `POST /api/v1/playlist/pfl/file`
Odsłuch dowolnego pliku bezpośrednio z dysku.
- **Ciało:** `{ filePath }`. **Błędy:** `404 FILE_NOT_FOUND`.

#### `POST /api/v1/playlist/pfl/stop`
**Odpowiedź:** `state: "PFL_STOPPED"`.

#### `POST /api/v1/playlist/pfl/seek`
- **Query:** `offset_ms: int`. **Odpowiedź:** `state: "PFL_SEEK"`.

### 6.5 Elementy playlisty (kolejka na żywo)

Ścieżka bazowa: `/api/v1/playlist` (wspólna z §6.4 — inne podścieżki).

#### `GET /api/v1/playlist/items`
```
PlaylistItemsEnvelopeDto {
  items: PlaylistItemDto[]
  mode: "AUTO" | "LIVE_ASSIST" | "MANUAL"
  state: "IDLE" | "PLAYING" | "PAUSED"
  currentItemId?
}
PlaylistItemDto {
  itemId, assetId?, position: uint, itemType: "ASSET" | "DUMMY"
  externalFilePath?, dummyLabel?, dummyNote?, dummyDurationMs?: uint
  crossfadeMs?: uint, leadInMs?: int, trimStartMs?: uint, trimEndMs?: uint
  segueType: "AUTO" | "MANUAL" | "TIMED", scheduledAt?: DateTime
  autoLinkNext: bool
  title?, artist?, durationMs?: uint, assetType?, formatName?, status?
  isDamaged?: bool, comments?, cueMarkers?: CueMarkersDto, volumeEnvelope?
}
```

#### `POST /api/v1/playlist/items`
Dodaje asset z biblioteki do kolejki.
- **Ciało:** `{ assetId?, externalFilePath?, position: int, itemType: string = "ASSET" }` — dokładnie jedno z `assetId`/`externalFilePath` musi być ustawione. Jeśli podano `externalFilePath`, plik jest najpierw importowany.
- **Odpowiedź `200`:** `{ itemId, position: uint }`.
- **Błędy:** `400`, jeśli ani jedno, ani oba z `assetId`/`externalFilePath` nie są ustawione, `404 ASSET_NOT_FOUND`, `409 NO_PLAYLIST`, `422 IMPORT_FAILED`.

#### `POST /api/v1/playlist/items/external`
Dodaje do kolejki plik grany bezpośrednio z dysku, bez importu do biblioteki (tytuł/artysta/czas trwania są odczytywane z tagów ID3 po stronie serwera, jeśli pominięte).
- **Ciało:** `{ filePath, position: int, title?, artist?, durationMs? }`.
- **Błędy:** `400` pusta ścieżka, `404 FILE_NOT_FOUND`, `409 NO_PLAYLIST`.

#### `DELETE /api/v1/playlist/items`
Czyści całą kolejkę. **Odpowiedź:** `204`.

#### `DELETE /api/v1/playlist/items/{itemId}`
Usuwa pojedynczy element kolejki. **Odpowiedź:** `204`. **Błędy:** `409 NO_PLAYLIST`.

#### `DELETE /api/v1/playlist/current`
Usuwa aktualnie odtwarzany element. **Odpowiedź:** `204`.

#### `PATCH /api/v1/playlist/items/reorder`
- **Ciało:** `{ itemId, newPosition: int }`. **Odpowiedź:** `204`.

#### `PATCH /api/v1/playlist/items/{itemId}`
Częściowa aktualizacja parametrów odtwarzania elementu kolejki.
- **Ciało (wszystko opcjonalne):** `{ crossfadeMs?, leadInMs?, trimStartMs?, trimEndMs?, segueType?, autoLinkNext?, volumeEnvelope? }`. **Odpowiedź:** `204`.

#### `POST /api/v1/playlist/mode`
Przełącza tryb playlisty.
- **Ciało:** `{ mode }` (`AUTO`\|`LIVE_ASSIST`\|`MANUAL`). **Odpowiedź:** `PlaybackStatusResponseDto`.

### 6.6 Zapisane playlisty

Ścieżka bazowa: `/api/v1/playlists`.

#### `GET /api/v1/playlists`
```
SavedPlaylistsEnvelopeDto { items: SavedPlaylistSummaryDto[] }
SavedPlaylistSummaryDto { playlistId, name, createdAt, itemCount: int }
```

#### `GET /api/v1/playlists/{playlistId}`
```
SavedPlaylistDetailDto { playlistId, name, createdAt, items: PlaylistItemDto[] }
```
(`cueMarkers` w elementach zwracanych tutaj jest zawsze `null`.) **Błędy:** `404 PLAYLIST_NOT_FOUND`.

#### `POST /api/v1/playlists`
Zapisuje bieżącą (lub zbudowaną ręcznie) listę elementów jako nazwaną playlistę.
- **Ciało:** `{ name, items: PlaylistItemSaveDto[] }` — `name` wymagane.
  ```
  PlaylistItemSaveDto {
    assetId?, itemType, dummyLabel?, dummyNote?, dummyDurationMs?: uint
    crossfadeMs?: uint, trimStartMs?: uint, trimEndMs?: uint
    segueType, autoLinkNext: bool, volumeEnvelope?
  }
  ```
- **Odpowiedź `200`:** `{ playlistId, name, createdAt }`.

#### `PUT /api/v1/playlists/{playlistId}`
Pełne zastąpienie (to samo ciało co `POST`; istniejące elementy są czyszczone i dodawane od nowa). **Błędy:** `404`.

#### `DELETE /api/v1/playlists/{playlistId}`
**Odpowiedź:** `204`. **Błędy:** `404`.

### 6.7 Aux Players

Ścieżka bazowa: `/api/v1/aux/{index}` — `index` to `0`–`7` (w przeciwnym razie 400 `Aux index must be between 0 and 7.`).

Wszystkie odpowiedzi to `PlaybackStatusResponseDto { status: "OK", state }`, chyba że zaznaczono inaczej.

| Endpoint | Ciało | Wynikowy `state` |
|---|---|---|
| `POST /{index}/load` | `{ filePath }` | → `AuxLoadedDto { durationMs, waveformPath, startMs, endMs }` |
| `POST /{index}/play` | — | `ACTIVE` |
| `POST /{index}/pause` | — | `PAUSED` |
| `POST /{index}/stop` | — | `IDLE` (używa skonfigurowanego fade-outu aux) |
| `POST /{index}/eject` | — | `IDLE` |
| `POST /{index}/loop` | `{ enabled: bool }` | `LOOP_ON` / `LOOP_OFF` |
| `POST /{index}/volume` | `{ gain: float }` (0.0–1.0 liniowo) | `ACTIVE` |
| `POST /{index}/route` | `{ on: bool, pfl: bool }` | `ACTIVE` |

### 6.8 Cartwall

Ścieżka bazowa: `/api/v1` — same route'y niosą nazwę zasobu (`cartwalls`, `cartwall`), **nie** są dodatkowo zagnieżdżone pod `/api/v1/cartwalls`.

#### `GET /api/v1/cartwalls`
Listuje każdą „stronę" cartwalla. Samo-naprawiające się: jeśli studio nie ma żadnej, domyślna strona jest tworzona automatycznie; sloty są automatycznie dostawiane do skonfigurowanej liczby na stronę.
```
CartwallsEnvelopeDto { items: CartwallDto[] }
CartwallDto { cartwallId, name, pageOrder: int, hotkey?, slots: CartSlotDto[] }
CartSlotDto {
  slotId, cartwallId, assetId?, slotNumber: int
  label?, color? ("#RRGGBB"), hotkey?, loop: bool
  fadeoutMs?: uint, outputGainDb: decimal
  title?, artist?, durationMs?: uint   // zdenormalizowane z przypisanego assetu
}
```

#### `POST /api/v1/cartwalls`
- **Ciało:** `{ name }` — `400`, jeśli puste. **Odpowiedź:** nowy `CartwallDto` z pustym `slots`.

#### `DELETE /api/v1/cartwalls/{cartwallId}`
Usuwa stronę i przenumerowuje kolejność pozostałych stron.
- **Błędy:** `422 LAST_CARTWALL` (nie można usunąć jedynej strony), `404 CARTWALL_NOT_FOUND`. **Odpowiedź:** `204`.

#### `PATCH /api/v1/cartwalls/{cartwallId}`
- **Ciało:** `{ name?, pageOrder? }`. **Odpowiedź:** `204`. **Błędy:** `400` (puste ciało), `404`.

#### `POST /api/v1/cartwall/{slotId}/trigger`
Odtwarza slot cartwalla (liczba pojedyncza „cartwall" w ścieżce — celowo).
- **Odpowiedź `200`:** `{ slotId, status: "TRIGGERED", durationMs? }`.
- **Błędy:** `404 SLOT_NOT_FOUND` / `ASSET_NOT_FOUND`, `422 SLOT_EMPTY` / `ASSET_NO_FILE` / `ASSET_DAMAGED`.

#### `POST /api/v1/cartwall/{slotId}/stop`
- **Odpowiedź `200`:** `{ slotId, status: "STOPPED", durationMs: null }`. **Błędy:** `404 SLOT_NOT_FOUND`.

#### `PATCH /api/v1/cartwall/{slotId}`
Przypisanie/edycja slotu.
- **Ciało:** `{ assetId?, label?, color?, hotkey?, loop?, clearAsset: bool = false }`. **Odpowiedź:** `204`. **Błędy:** `400`, `404 SLOT_NOT_FOUND`.

#### `POST /api/v1/cartwalls/{cartwallId}/mode`
- **Ciało:** `{ mode }` (`ON`\|`PFL`\|`OFF`, bez rozróżniania wielkości liter). **Odpowiedź:** `PlaybackStatusResponseDto`. **Błędy:** `400`, `404 CARTWALL_NOT_FOUND`.

### 6.9 Mikrofon

Ścieżka bazowa: `/api/v1/mic`.

> To jest wyzwalacz **duckingu mikrofonu**, odpowiadający przyciskowi mikrofonu w aplikacji desktopowej — nie surowa kontrola gainu/poziomu. Jeśli integrujesz sprzęt, zobacz reguły duckingu projektu: ducking jest wyzwalany wyłącznie przyciskiem, nie ma detekcji aktywności głosu (VOX).

#### `POST /api/v1/mic/start` · `POST /api/v1/mic/stop`
**Odpowiedź:** `PlaybackStatusResponseDto { state: "ACTIVE" | "IDLE" }`.

#### `GET /api/v1/mic/level`
Odczyt miernika VU na żywo. **Odpowiedź:** `{ levelDb: double }`.

#### `GET /api/v1/mic/status`
**Odpowiedź:** `{ isActive: bool }`.

#### `GET /api/v1/mic/fx`
Listuje łańcuch efektów. **Odpowiedź:** tablica `{ slotId, fxType, parameters: Record<string, float> }`.

#### `GET /api/v1/mic/fx/params/{fxType}`
Pobiera prawidłowe zakresy parametrów dla typu efektu (przydatne do zbudowania edytora parametrów w UI).
- **Odpowiedź:** tablica `{ key, min: float, max: float, default: float, unit }`. **Błędy:** `400` (nieznany `fxType`).

#### `PUT /api/v1/mic/fx/{slotId}`
- **Ciało:** `{ parameters: Record<string, float> }`. **Odpowiedź:** `204`. **Błędy:** `404` (slot nieznaleziony).

#### `POST /api/v1/mic/fx`
- **Ciało:** `{ fxType }`. **Odpowiedź:** `{ slotId: int }`. **Błędy:** `400` (nieznany `fxType`).

#### `DELETE /api/v1/mic/fx/{slotId}`
**Odpowiedź:** `204`.

#### `GET /api/v1/mic/vst`
Listuje wtyczki VST załadowane na ścieżce mikrofonu. **Odpowiedź:** tablica `{ slotId, pluginName, dllPath }`.

#### `POST /api/v1/mic/vst`
- **Ciało:** `{ dllPath }`. **Odpowiedź:** `{ slotId: int }`.

#### `DELETE /api/v1/mic/vst/{slotId}`
**Odpowiedź:** `204`.

> ⚠️ Odpowiedzi błędów tego kontrolera mają inny format/wielkość liter niż reszta API — patrz [§7](#7-znane-nieregularności-i-niespójności).

### 6.10 Nagrywanie

Ścieżka bazowa: `/api/v1/recording`. Nagrywa magistralę programową; jedno nagranie naraz; celowo dostępne dla każdego uwierzytelnionego użytkownika (bez ograniczenia do Admina).

#### `POST /api/v1/recording/start`
- **Ciało:** `{ directory, format: string = "MP3", bitrateKbps: int = 192, sampleRateHz?, channels: byte = 2, namePrefix? }`.
- **Walidacja:** `directory` wymagane; `format` musi być znanym formatem enkodera; bitrate 32–320; channels 1 lub 2.
- **Błędy:** `400` jak wyżej, `422 FORMAT_UNAVAILABLE` (kodek niezainstalowany), `422 RECORDING_FAILED` (np. folder bez praw zapisu).
- **Odpowiedź:** `RecordingStatusDto`.

#### `POST /api/v1/recording/stop`
**Odpowiedź:** `RecordingStatusDto`.

#### `GET /api/v1/recording/status`
```
RecordingStatusDto { state, filePath?, startedAt?: DateTime, error?, bytesWritten: long }
```

### 6.11 Encoder / Streaming

Ścieżka bazowa: `/api/v1/encoder`. **Zarządzanie profilami (tworzenie/edycja/usuwanie) tylko dla Admina**; uruchamianie, zatrzymywanie i odczyt statusu jest dostępne dla każdego uwierzytelnionego użytkownika.

#### `GET /api/v1/encoder`
**Odpowiedź:** `{ profiles: EncoderProfileDto[] }`.
```
EncoderProfileDto {
  profileId, name, format, bitrateKbps: int, sampleRateHz?, channels: byte
  serverType, host, port: int, mount?, username?, streamId?
  hasPassword: bool           // samo hasło nigdy nie jest zwracane
  useSsl: bool, usePut: bool, streamName?, genre?, streamUrl?, description?
  isPublic: bool, enabled: bool, armed: bool, autoStart: bool
  reconnectDelaySeconds: int, titleMode, titleText?
}
```

#### `GET /api/v1/encoder/{id}`
**Błędy:** `404 PROFILE_NOT_FOUND`.

#### `POST /api/v1/encoder` — **tylko Admin** (w przeciwnym razie `403`)
- **Ciało `EncoderProfileCreateDto`:** `{ name, format, bitrateKbps, serverType, host, port, mount?, username?, streamId?, password?, sampleRateHz?, channels = 2, useSsl = false, usePut = false, streamName?, genre?, streamUrl?, description?, isPublic = false, enabled = true, armed = false, autoStart = false, reconnectDelaySeconds = 10, titleMode = "NOW_PLAYING", titleText? }`.
- **Walidacja (`400`, chyba że zaznaczono inaczej):** `name` wymagane; `format` musi być znanym formatem enkodera (`422 FORMAT_UNAVAILABLE`, jeśli dodatek kodeka nie jest zainstalowany); bitrate 32–320; channels 1 lub 2; `serverType` jeden z `SHOUTCAST`\|`SHOUTCAST2`\|`ICECAST`; `host` wymagany; `port` 1–65535; `reconnectDelaySeconds` 2–1800; Icecast wymaga `mount`; `titleMode` jeden z `NOW_PLAYING`\|`STATIC`\|`NONE`, a `STATIC` wymaga niepustego `titleText`.
- Hasło jest wysyłane **jawnym tekstem w tym wywołaniu** (chroń połączenie — nieprzypadkowo domyślnie jest to `localhost`) i szyfrowane po stronie serwera przed zapisem; nigdy nie jest zwracane w żadnej odpowiedzi.
- **Odpowiedź `200`:** `{ profileId }`.

#### `PUT /api/v1/encoder/{id}` — **tylko Admin**
To samo ciało/walidacja co `POST`. Przekazanie `password: null` zachowuje zapisane hasło bez zmian; przekazanie `""` jawnie je czyści. **Błędy:** `404`.

#### `DELETE /api/v1/encoder/{id}` — **tylko Admin**
Najpierw zatrzymuje ewentualną działającą sesję, potem usuwa. **Odpowiedź:** `204`. **Błędy:** `404`.

#### `POST /api/v1/encoder/{id}/start`
- **Błędy:** `404 PROFILE_NOT_FOUND`, `422 PROFILE_DISABLED`, `422 FORMAT_UNAVAILABLE`, `422 PASSWORD_UNREADABLE` (zapisane hasło jest związane z maszyną; profil skopiowany z innego komputera nie da się odszyfrować — trzeba wpisać hasło ponownie).
- **Odpowiedź:** `EncoderStatusDto`.

#### `POST /api/v1/encoder/{id}/stop`
Zawsze się udaje (syntetyzuje status `STOPPED`, nawet jeśli nic nie działało). **Odpowiedź:** `EncoderStatusDto`.

#### `POST /api/v1/encoder/start-armed`
Uruchamia każdy profil oznaczony jako uzbrojony/auto-start; jeden nieudany profil nie blokuje pozostałych. **Odpowiedź:** `{ statuses: EncoderStatusDto[] }`.

#### `POST /api/v1/encoder/stop-all`
Zatrzymuje każdą działającą sesję. **Odpowiedź:** `{ statuses: EncoderStatusDto[] }`.

#### `GET /api/v1/encoder/status`
Wszystkie aktualne sesje. **Odpowiedź:** `{ statuses: EncoderStatusDto[] }`.

#### `GET /api/v1/encoder/{id}/status`
```
EncoderStatusDto {
  profileId, profileName, state, error?
  connectedAt?: DateTime, retryAttempt: int, nextRetryAt?: DateTime
  listenerCount?: int
}
```
**Błędy:** `404 SESSION_NOT_FOUND` (profil nigdy nie był uruchamiany).

### 6.12 Streams (streamy internetowe jako assety)

Ścieżka bazowa: `/api/v1/streams`.

#### `POST /api/v1/streams`
Rejestruje internetowy stream radiowy jako asset biblioteki.
- **Ciało:** `{ name, streamUrl }` — `name` wymagane; `streamUrl` musi zaczynać się od `http://` lub `https://`.
- Deduplikacja po sumie kontrolnej URL-a: duplikat zwykle zwraca istniejący rekord z `200`, albo `409 DUPLICATE` przy wyścigu.
- **Odpowiedź `201`** (`Location: /api/v1/assets/{id}`): `{ assetId, name, streamUrl }`.

### 6.13 Kategorie, podkategorie i gatunki

Ścieżka bazowa: `/api/v1/categories` (plus `/genres` i `subcategories/{id}` pod tym samym prefiksem). „Kategorie" korzystają z tej samej tabeli co [Formaty](#614-formaty) — patrz [§7](#7-znane-nieregularności-i-niespójności).

#### `GET /api/v1/categories`
**Odpowiedź `200`:** **goła tablica** `CategoryDto[]` — `{ formatId, name, description? }` (bez opakowania w kopertę, w odróżnieniu od większości endpointów listujących).

#### `POST /api/v1/categories`
- **Ciało:** `{ name }` — `400`, jeśli puste. **Odpowiedź `201`:** `{ formatId, name }`.

#### `PUT /api/v1/categories/{id}`
- **Ciało:** `{ name }`. **Błędy:** `400`, `404`.

#### `DELETE /api/v1/categories/{id}`
Jeśli assety wciąż odwołują się do kategorii, **nie** zwraca to błędu — zwraca `200` z `{ formatId, deleted: false, reason: "Category has N assigned track(s)..." }`. W przeciwnym razie `{ formatId, deleted: true, reason: null }`. **Błędy:** `404`, jeśli sama kategoria nie istnieje.

#### `GET /api/v1/categories/{id}/subcategories`
**Odpowiedź:** `{ formatId, items: SubcategoryDto[] }`; `SubcategoryDto { subcategoryId, formatId, name, sortOrder: int }`.

#### `POST /api/v1/categories/{id}/subcategories`
- **Ciało:** `{ name }`. **Odpowiedź `201`:** `{ subcategoryId, formatId, name }`. **Błędy:** `400`, `404` (brak kategorii nadrzędnej).

#### `PUT /api/v1/categories/subcategories/{subcategoryId}`
- **Ciało:** `{ name }`. **Odpowiedź:** `{ subcategoryId, formatId, name }` — uwaga: `formatId` wraca tutaj jako pusty string zamiast prawdziwego ID kategorii nadrzędnej; nie polegaj na tym polu przy tym konkretnym wywołaniu.

#### `DELETE /api/v1/categories/subcategories/{subcategoryId}`
Takie samo miękkie odrzucenie jak przy usuwaniu kategorii, gdy assety wciąż się do niej odwołują. **Odpowiedź:** `{ subcategoryId, deleted: bool, reason? }`.

#### `GET /api/v1/categories/genres`
**Odpowiedź:** `{ items: GenreDto[] }`; `GenreDto { genreId, name, sortOrder: int }`.

#### `POST /api/v1/categories/genres`
- **Ciało:** `{ name }`. **Odpowiedź `201`:** `{ genreId, name }`.

#### `PUT /api/v1/categories/genres/{genreId}`
- **Ciało:** `{ name }`. **Odpowiedź:** `{ genreId, name }`.

#### `DELETE /api/v1/categories/genres/{genreId}`
**Odpowiedź:** `200` z **pustym ciałem** — w odróżnieniu od usuwania kategorii/podkategorii, tutaj nie ma DTO z potwierdzeniem.

### 6.14 Formaty

Ścieżka bazowa: `/api/v1/formats`.

#### `GET /api/v1/formats`
Widok tylko do odczytu tej samej tabeli, którą zarządzają [Kategorie](#613-kategorie-podkategorie-i-gatunki).
**Odpowiedź:** `{ items: AssetFormatDto[] }`; `AssetFormatDto { formatId, name, description? }`.

### 6.15 Now Playing

Ścieżka bazowa: `/api/v1/nowplaying`.

#### `GET /api/v1/nowplaying`
```
NowPlayingDto {
  nowPlaying?: CurrentTrackDto
  nextTrack?: NextTrackDto
  playlistMode, state, loopCurrent: bool
}
CurrentTrackDto {
  assetId, title, artist?, durationMs: uint
  positionMs: uint, remainingMs: uint, startedAt?: DateTime
  cueMarkers?: CueMarkersDto
}
NextTrackDto { assetId, title, artist?, durationMs: uint, scheduledAt?: DateTime }
```

#### `GET /api/v1/nowplaying/next`
**Odpowiedź `200`:** `NextTrackDto`. **Odpowiedź `204`:** jeśli nie ma możliwego do ustalenia kolejnego elementu.

#### `GET /api/v1/nowplaying/history`
- **Query:** `limit` (1–500, domyślnie 50), `offset` (domyślnie 0).
- **Odpowiedź:** `{ history: PlayoutLogDto[], total: int }`; `PlayoutLogDto { assetId?, title?, artist?, startedAt: DateTime, endedAt?: DateTime, sourceType }`.

### 6.16 Dziennik zdarzeń

Ścieżka bazowa: `/api/v1/events/log`.

#### `GET /api/v1/events/log`
- **Query:** `limit` (1–500, domyślnie 50).
- **Odpowiedź:** `{ items: EventLogEntryDto[] }`; `EventLogEntryDto { eventId, eventName, firedAt: DateTime, result: "SUCCESS" | "PARTIAL" | "FAILED" }`.
  > W obecnej implementacji `result` zawsze zgłasza `"SUCCESS"` — śledzenie wyniku dla poszczególnych wpisów wygląda na jeszcze niepodłączone w pełni. Nie polegaj na tym polu przy wykrywaniu niepowodzeń.

### 6.17 Zdarzenia zaplanowane

Ścieżka bazowa: `/api/v1/events` (inne podścieżki niż §6.16, bez kolizji). **Tworzenie/edycja/usuwanie tylko dla Admina.**

#### `GET /api/v1/events`
```
ScheduledEventResponseEnvelopeDto { items: ScheduledEventDto[] }
ScheduledEventDto {
  eventId, name, eventType, category, enabled: bool
  eventHour?: string ("hh:mm:ss"), days: string[], hours: int[]
  smartTiming: bool, actions: ScheduledEventActionDto[]
  lastFiredAt?: DateTime, skipNext: bool
  onlyOnDate?: string ("yyyy-MM-dd")   // ustawiane tylko dla zdarzeń ONE_TIME
}
ScheduledEventActionDto { type: string, payload: object }  // kształt zależy od `type`
```

#### `GET /api/v1/events/{id}`
**Błędy:** `404 EVENT_NOT_FOUND`.

#### `POST /api/v1/events` — **tylko Admin**
- **Ciało:** `{ name, eventType, category, enabled, eventHour?, days: string[], hours: int[], smartTiming, actions: ScheduledEventActionDto[], onlyOnDate? }` — `name` wymagane; `eventType` musi być znanym typem; `eventHour`, jeśli podane, musi mieć format `hh:mm:ss`; `onlyOnDate` jest **wymagane**, gdy `eventType` to `ONE_TIME`.
- **Odpowiedź `201`:** `{ eventId, name, createdAt }`.

#### `PUT /api/v1/events/{id}` — **tylko Admin**
To samo ciało/walidacja co `POST`, pełne zastąpienie. **Błędy:** `404`.

#### `PATCH /api/v1/events/{id}`
Częściowa aktualizacja — `{ enabled?: bool, skipNext?: bool }`. **Operator może ustawić wyłącznie `skipNext`**: żądanie Operatora zawierające `enabled` dostaje `403`. **Błędy:** `400` (puste ciało), `404`.

#### `DELETE /api/v1/events/{id}` — **tylko Admin**
**Odpowiedź:** `204`. **Błędy:** `404`.

#### `GET /api/v1/events/next`
**Odpowiedź `200`:** `{ eventId, name, firesAt: DateTime, remainingMs: uint, skipNext: bool }`. **Odpowiedź `204`:** nic nie zaplanowano.

---

## 7. Znane nieregularności i niespójności

API rosło organicznie razem z aplikacją desktopową, więc kilka miejsc nie jest idealnie jednolitych. Żadna z nich nie jest błędem, który zostanie po cichu „naprawiony" bez ostrzeżenia, ale warto wiedzieć o nich z góry:

1. **`POST /api/v1/assets/{id}/waveform/request`** zwraca `{ asset_id, message }` w snake_case, podczas gdy praktycznie wszystko inne w REST API jest camelCase.
2. **Wszystkie obiekty `payload` zdarzeń WebSocket są w snake_case** (`asset_id`, `duration_ms`, ...); tylko zewnętrzna koperta (`eventId`/`eventType`/`timestamp`) jest camelCase.
3. **Odpowiedzi błędów `MicController`** używają innego stylu `errorCode` (`InvalidFxType`, `FxSlotNotFound` — w stylu zbliżonym do PascalCase) i zawsze wysyłają puste `traceId`, w odróżnieniu od kodów `SCREAMING_SNAKE_CASE` i wypełnionych identyfikatorów śledzenia wszędzie indziej.
4. **`GET /api/v1/categories`** zwraca gołą tablicę JSON zamiast koperty `{ items: [...] }`, a **`DELETE /api/v1/categories/genres/{id}`** zwraca `200` bez żadnego ciała — obie różnią się od pokrewnych endpointów w tym samym kontrolerze.
5. **Kategorie i Formaty się nakładają:** `/api/v1/categories` (odczyt/zapis) i `/api/v1/formats` (tylko odczyt) operują na tej samej tabeli. Pisząc integrację, traktuj `/categories` jako źródło prawdy, a `/formats` jako wygodny alias tylko-do-odczytu.
6. **`PUT /api/v1/categories/subcategories/{id}`** obecnie zwraca w odpowiedzi pusty string dla `formatId` zamiast prawdziwego ID kategorii nadrzędnej — nie parsuj tego pola z tego konkretnego wywołania.
7. **Nigdzie w aplikacji nie ma endpointu OpenAPI/Swagger** — ten dokument jest źródłem prawdy; w razie wątpliwości kod kontrolerów w `src/RDM.API/Controllers/` jest ostatecznym źródłem.

## 8. Ściągawka: przykłady curl

Zamień `operator:twojehaslo` na prawdziwe dane logowania (albo pomiń `-u ...` całkowicie, jeśli włączyłeś anonimowy dostęp lokalny i wywołujesz z tej samej maszyny).

```bash
# Co teraz gra?
curl -u operator:twojehaslo http://localhost:9300/api/v1/nowplaying

# Uruchom główny odtwarzacz
curl -u operator:twojehaslo -X POST http://localhost:9300/api/v1/playlist/play

# Wyszukaj w bibliotece utwory danego artysty
curl -u operator:twojehaslo "http://localhost:9300/api/v1/assets?q=Coldplay&limit=20"

# Wyzwól slot cartwalla o ID "abc123"
curl -u operator:twojehaslo -X POST http://localhost:9300/api/v1/cartwall/abc123/trigger

# Włącz mikrofon (ducking podkładu muzycznego)
curl -u operator:twojehaslo -X POST http://localhost:9300/api/v1/mic/start

# Dodaj stream internetowy jako asset biblioteki
curl -u operator:twojehaslo -X POST http://localhost:9300/api/v1/streams \
  -H "Content-Type: application/json" \
  -d '{"name":"BBC Radio 1","streamUrl":"https://stream.example.com/radio1"}'

# Dodaj utwór do kolejki na żywo
curl -u operator:twojehaslo -X POST http://localhost:9300/api/v1/playlist/items \
  -H "Content-Type: application/json" \
  -d '{"assetId":"<guid-assetu>","position":0,"itemType":"ASSET"}'

# Sprawdzaj status importu aż do zakończenia
curl -u operator:twojehaslo -X POST http://localhost:9300/api/v1/assets/import \
  -H "Content-Type: application/json" \
  -d '{"filePath":"C:\\Music\\nowy_utwor.mp3","assetType":"Track","readId3":true}'
# → { "importId": "...", "status": "QUEUED", ... }
curl -u operator:twojehaslo http://localhost:9300/api/v1/assets/import/<importId>
```

---

**Ostatnia aktualizacja**: 2026-08-18
