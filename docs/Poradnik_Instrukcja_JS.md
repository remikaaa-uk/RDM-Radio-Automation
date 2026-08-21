# Instrukcja JavaScript w RDM (Radio Digital Manager)

## 1. Czym jest JavaScript w RDM?

JavaScript w RDM to **zaawansowany system automatyzacji** umożliwiający pisanie skryptów bez konieczności rekompilacji aplikacji. Pozwala na tworzenie logiki biznesowej, warunkowych przepływów pracy i integracji z systemami zewnętrznymi.

### Charakterystyka:

- **Silnik:** Jint (ECMAScript 2020+)
- **Izolacja:** Każdy skrypt wykonuje się w izolowanym środowisku (sandbox)
- **Bezpieczeństwo:** Brak dostępu do System.*, brak require/import, ochrona przed atakami
- **Ograniczenia Zasobów:**
  - **Timeout:** Maksymalnie 5 sekund na wykonanie
  - **Pamięć:** Maksymalnie 4 MB RAM
  - **Instrukcje:** Zabezpieczenie przed nieskończonymi pętlami
- **Integracja:** Pełna integracja z systemem akcji RDM i urządzeniami sprzętowymi
- **Persistencja:** Skrypty przechowywane są w bazie danych (tabela `scripts`)
- **Zarządzanie:** Można włączać/wyłączać skrypty bez restartowania aplikacji

---

## 2. Struktura Danych

### Skrypt (`Script.cs`)

```csharp
public class Script
{
    public Guid   Id          { get; set; }       // Unikalny identyfikator
    public string Name        { get; set; }       // Nazwa skryptu
    public string ScriptBody  { get; set; }       // Kod JavaScript
    public bool   IsEnabled   { get; set; }       // Włączony/Wyłączony
    public string Language    { get; set; }       // "js" (w przyszłości możliwe inne)
}
```

### Wynik Wykonania (`ScriptResult.cs`)

```csharp
public record ScriptResult(bool Success, string? Error = null, long ElapsedMs = 0)
{
    public static ScriptResult Ok(long elapsedMs)  => new(true,  null,  elapsedMs);
    public static ScriptResult Fail(string error)  => new(false, error, 0);
}
```

---

## 3. Dostępne API

Skrypty mogą uzyskać dostęp do następujących API zorganizowanych w namespace'ach:

### 3.1. API Odtwarzacza (`player`)

```javascript
player.play()      // Uruchomienie odtwarzania
player.stop()      // Zatrzymanie odtwarzania
player.pause()     // Pauza
player.next()      // Następny utwór
```

**Przykład:**
```javascript
player.play();
rdm.delay(1000);
player.next();
```

---

### 3.2. API Mikrofonu (`mic`)

```javascript
mic.on()      // Włączenie mikrofonu
mic.off()     // Wyłączenie mikrofonu
mic.toggle()  // Przełączenie mikrofonu (on/off)
```

**Przykład:**
```javascript
mic.on();
rdm.log("Mikrofon włączony");
```

---

### 3.3. API Cartwall (`cart`)

```javascript
cart.selectTab(tabIndex)    // Wybór karty (0–6 → karty 1–7)
cart.triggerSlot(slotIndex) // Wyzwolenie slotu (0–15 → sloty 1–16)
```

**Przykład:**
```javascript
cart.selectTab(0);      // Przejdź na kartę 1
rdm.delay(200);
cart.triggerSlot(3);    // Wyzwól slot 4
```

---

### 3.4. API Aux Playerów (`aux`)

```javascript
aux.load(index, filePath)      // Wczytanie pliku (index: 0–3)
aux.play(index)                // Odtwarzanie aux (index: 0–3)
aux.stop(index, fadeMs)        // Zatrzymanie aux (index: 0–3); fadeMs opcjonalny, 0 = od razu
aux.setLoop(index, enabled)    // Włączenie/wyłączenie pętli
aux.setVolume(index, gain)     // Ustawienie głośności (0.0–1.0)
```

**Parametry:**
- `index`: 0–3 (Aux 1–4)
- `filePath`: Ścieżka do pliku na dysku (np. `/music/jingle.mp3`)
- `gain`: Wartość od 0.0 (bezdźwięk) do 1.0 (100%)
- `fadeMs`: czas fade-outu w ms przed zatrzymaniem (domyślnie 0 = zatrzymanie natychmiastowe).
  Ręczny Stop w interfejsie i wyzwalacze sprzętowe/MIDI używają zamiast tego globalnego
  ustawienia `AUX fade out on Stop` (zakładka Auto DJ w Ustawieniach).

**Przykład:**
```javascript
aux.load(0, "/audio/jingle.wav");
aux.play(0);
aux.setVolume(0, 0.8);  // 80% głośności
rdm.delay(3000);        // Poczekaj 3 sekundy
aux.stop(0, 1500);      // Fade out 1.5 s, potem stop
```

---

### 3.5. API RDM (Narzędzia i Integracje)

```javascript
rdm.log(message)              // Logowanie wiadomości
rdm.delay(milliseconds)       // Czekanie (max 4000ms)
rdm.sendHttp(url)             // Wysłanie żądania HTTP GET
rdm.sendSerial(deviceId, cmd) // Wysłanie komendy do urządzenia szeregowego
```

**Parametry:**
- `message`: Tekst do zalogu (pojawi się w application.log)
- `milliseconds`: Opóźnienie w millisekundach (max 4000ms)
- `url`: URL do zasobu HTTP (np. `http://api.local/webhook`)
- `deviceId`: ID urządzenia (np. `matrix_main`)
- `cmd`: Komenda (np. `ROUTE 1 2`)

**Przykład:**
```javascript
rdm.log("Skrypt START_SHOW uruchomiony");
rdm.delay(500);
rdm.sendHttp("http://dashboard.local/api/onair?status=true");
rdm.log("Notyfikacja wysłana");
```

---

## 4. Interfejs API Fasady (IScriptingFacade)

Poniżej pełna lista dostępnych metod (mapa między JS API a C#):

| JavaScript API | Metoda C# | Opis |
|---|---|---|
| `player.play()` | `PlayerPlay()` | Uruchomienie odtwarzacza |
| `player.stop()` | `PlayerStop()` | Zatrzymanie odtwarzacza |
| `player.next()` | `PlayerNext()` | Następny utwór |
| `player.pause()` | `PlayerPause()` | Pauza |
| `mic.on()` | `MicOn()` | Włączenie mikrofonu |
| `mic.off()` | `MicOff()` | Wyłączenie mikrofonu |
| `mic.toggle()` | `MicToggle()` | Przełączenie mikrofonu |
| `cart.selectTab(int)` | `CartSelectTab(int)` | Wybór karty cartwall |
| `cart.triggerSlot(int)` | `CartTriggerSlot(int)` | Wyzwolenie slotu |
| `aux.load(int, str)` | `AuxLoad(int, str)` | Wczytanie pliku |
| `aux.play(int)` | `AuxPlay(int)` | Odtwarzanie |
| `aux.stop(int, int?)` | `AuxStop(int, int)` | Zatrzymanie (opcjonalny fade w ms) |
| `aux.setLoop(int, bool)` | `AuxSetLoop(int, bool)` | Pętla |
| `aux.setVolume(int, float)` | `AuxSetVolume(int, float)` | Głośność |
| `rdm.log(str)` | `Log(str)` | Logowanie |
| `rdm.delay(int)` | `Delay(int)` | Opóźnienie |
| `rdm.sendHttp(str)` | `SendHttp(str)` | HTTP GET |
| `rdm.sendSerial(str, str)` | `SendSerial(str, str)` | Komenda szeregowa |

---

## 5. Architektura i Sandboxing

### Izolacja Środowiska

```
┌─────────────────────────────────────────────────┐
│        Skrypt JavaScript (IZOLOWANY)            │
│                                                  │
│  ✓ Dostęp do API RDM                            │
│  ✓ Timeout 5s                                   │
│  ✓ Limit 4MB RAM                                │
│  ✗ Brak dostępu do System.*                     │
│  ✗ Brak require/import                          │
│  ✗ Brak dostępu do FS                           │
│  ✗ Brak dostępu do sieciowych socketek          │
│                                                  │
└─────────────────────────────────────────────────┘
                       ↑
                       │ (Jint Engine)
                       ↓
┌─────────────────────────────────────────────────┐
│    IScriptingFacade (Bezpieczna Fasada)         │
└─────────────────────────────────────────────────┘
         ↑             ↑             ↑
         │             │             │
    Player      Cartwall          Mikser
    Service     Service           Service
```

### Bezpiecznik Zasobów

| Zasób | Limit | Działanie |
|-------|-------|----------|
| Czas wykonania | 5 sekund | TimeoutInterval (CancellationToken) |
| Pamięć | 4 MB | LimitMemory - wyjątek przy przekroczeniu |
| Pętla nieskończona | — | StatementsCountOverflow (detektuje `while(true)`) |

---

## 6. Przykłady Użytecznych Skryptów

### Przykład 1: "START AUDYCJI" (Broadcasting Script)

**Scenariusz:** Skrypt przygotowujący studio do emisji.

```javascript
// Skrypt: START AUDYCJI
// ID: a1b2c3d4-e5f6-7890-abcd-ef1234567890

rdm.log("=== PRZYGOTOWANIE AUDYCJI ===");

// Krok 1: Włączenie mikrofonu
mic.on();
rdm.log("✓ Mikrofon włączony");

// Krok 2: Uruchomienie playera
player.play();
rdm.log("✓ Odtwarzacz uruchomiony");

// Krok 3: Czekaj na inicjalizację
rdm.delay(1000);

// Krok 4: Załaduj jingle do AUX 1 (ID: 0)
aux.load(0, "/audio/jingles/station_id.wav");
rdm.log("✓ Jingle załadowany do AUX 1");

// Krok 5: Powiadom system zewnętrzny
rdm.sendHttp("http://dashboard.local/api/audycja/start");
rdm.log("✓ Dashboard powiadomiony");

rdm.log("=== AUDYCJA GOTOWA ===");
```

**Wyzwolenie:**
```
Mapowanie wyzwalacza:
- Przycisk: MIDI Note 40 (Korg nanoPAD slot 0)
- Akcja: AutomationRunScript
- Parameter: a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

---

### Przykład 2: "LOGIKA WARUNKOWA" (Conditional Logic)

**Scenariusz:** Skrypt podejmujący decyzje na podstawie godziny.

```javascript
// Skrypt: AUTOMACJA CZASOWA
// Odtwarzanie różnych formatów w zależności od pory dnia

rdm.log("Sprawdzam godzinę...");

const now = new Date();
const hour = now.getHours();

if (hour >= 6 && hour < 12) {
    // PORANEK (6:00 - 12:00)
    rdm.log("Poranek: playlist muzyki energicznej");
    cart.selectTab(0);  // Karta 1 - Poranek
    cart.triggerSlot(0); // Slot 1 - Otwarcie audycji
    
} else if (hour >= 12 && hour < 18) {
    // POŁUDNIE (12:00 - 18:00)
    rdm.log("Południe: playlist muzyki pop");
    cart.selectTab(1);  // Karta 2 - Południe
    cart.triggerSlot(0);
    
} else if (hour >= 18 && hour < 22) {
    // WIECZÓR (18:00 - 22:00)
    rdm.log("Wieczór: playlist muzyki relaksacyjnej");
    cart.selectTab(2);  // Karta 3 - Wieczór
    cart.triggerSlot(0);
    
} else {
    // NOC (22:00 - 6:00)
    rdm.log("Noc: automaty muzyczne");
    cart.selectTab(3);  // Karta 4 - Noc
    cart.triggerSlot(0);
}

rdm.log("✓ Playlist załadowany");
```

---

### Przykład 3: "PĘTLA I INTERAKCJE" (Loop & Interactions)

**Scenariusz:** Skrypt odtwarzający serię spotów reklamowych.

```javascript
// Skrypt: BLOK REKLAMOWY (10 spotów)
// Odtwarzanie każdego spotu z przerwami

const spotIds = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
const spotDurationMs = 30000; // 30 sekund na spot

rdm.log("=== BLOK REKLAMOWY ===");
rdm.sendHttp("http://dashboard.local/api/ads/start");

for (let i = 0; i < spotIds.length; i++) {
    const spotId = spotIds[i];
    
    rdm.log("Spot " + (i + 1) + "/" + spotIds.length + " (ID: " + spotId + ")");
    
    // Wyzwól slot cartwall (0–9 = sloty 1–10)
    cart.triggerSlot(spotId - 1);
    
    // Czekaj na koniec spotu
    rdm.delay(spotDurationMs);
    
    // Pokaż postęp
    if (i < spotIds.length - 1) {
        rdm.log("→ Przechodzę do następnego spotu");
    }
}

rdm.log("=== BLOK REKLAMOWY ZAKOŃCZONY ===");
rdm.sendHttp("http://dashboard.local/api/ads/end");
```

---

### Przykład 4: "INTEGRACJA SERWERA" (Server Integration)

**Scenariusz:** Skrypt pobierający dane z API i reagujący na nie.

```javascript
// Skrypt: HARMONOGRAM Z SERWERA
// Pobiera plan emisji z centralnego API

rdm.log("Pobieranie harmonogramu z serwera...");

// Symulacja pobierania - w rzeczywistości to byłby fetch (ale nie jest dostępny)
// Zamiast tego wysyłamy webhook i czekamy na callback
rdm.sendHttp("http://api.server.local/schedule/get?station_id=1");

// Alternatywa: bezpośrednia komenda do systemu
rdm.log("Wysyłam komendę do matrycy audio");
rdm.sendSerial("matrix_01", "ROUTE 1 2");  // Przesyłanie audio ze źródła 1 na wyjście 2

rdm.delay(500);

// Włącz mikrofon i uruchom player
mic.on();
player.play();

rdm.log("✓ System gotowy do emisji");
rdm.sendHttp("http://api.server.local/status/update?status=ready");
```

---

### Przykład 5: "OBSŁUGA BŁĘDÓW" (Error Handling)

**Scenariusz:** Skrypt z obsługą błędów i warunkową logiką.

```javascript
// Skrypt: BEZPIECZNE URUCHAMIANIE
// Sprawdzenie warunków przed Start

rdm.log("=== BEZPIECZNY START ===");

try {
    // Sprawdzenie 1: Czy mikrofon nie jest już włączony?
    // (w rzeczywistości byśmy mieli stan, ale dla przykładu...)
    
    rdm.log("Krok 1: Sprawdzam parametry...");
    
    const maxDelay = 4000;  // Max opóźnienie
    const safeDelay = Math.min(1500, maxDelay);
    
    rdm.log("Krok 2: Włączam mikrofon");
    mic.on();
    
    rdm.log("Krok 3: Czekam " + safeDelay + "ms");
    rdm.delay(safeDelay);
    
    rdm.log("Krok 4: Uruchamiam player");
    player.play();
    
    rdm.log("✓ START POWODZENIE");
    rdm.sendHttp("http://dashboard/log?event=start_success");
    
} catch (error) {
    rdm.log("✗ BŁĄD: " + error);
    rdm.sendHttp("http://dashboard/log?event=start_failed&error=" + error);
    
    // Natychmiast wyłącz wszystko w razie błędu
    player.stop();
    mic.off();
}
```

---

## 7. Możliwości Zaawansowane

### Wielokrotne Operacje na Aux

```javascript
// Załadowanie kilku plików do różnych aux playerów
aux.load(0, "/audio/intro.wav");
aux.load(1, "/audio/jingle.wav");
aux.load(2, "/audio/outro.wav");

// Sekwencyjna gra
aux.play(0);  // Intro
rdm.delay(5000);
aux.play(1);  // Jingle
rdm.delay(3000);
aux.play(2);  // Outro
```

### Sterowanie Głośnością

```javascript
// Fade in (stopniowy wzrost)
for (let i = 0; i <= 10; i++) {
    const volume = i / 10.0;
    aux.setVolume(0, volume);
    rdm.delay(100);
}

// Fade out (stopniowy spadek)
for (let i = 10; i >= 0; i--) {
    const volume = i / 10.0;
    aux.setVolume(0, volume);
    rdm.delay(100);
}
```

### Kombinacje Akcji

```javascript
// Kompleksowa sekwencja
mic.off();
player.stop();
rdm.delay(500);

cart.selectTab(2);
rdm.delay(200);
cart.triggerSlot(0);

aux.load(0, "/audio/transition.wav");
aux.play(0);
aux.setVolume(0, 0.7);

mic.on();
player.play();

rdm.log("Przełączenie audycji zakończone");
```

---

## 8. Limity i Ograniczenia

| Parametr | Limit | Uwagi |
|----------|-------|--------|
| Timeout wykonania | 5 sekund | TimeoutInterval (CancellationToken anuluje) |
| Maksymalna pamięć | 4 MB | LimitMemory (wyjątek OutOfMemoryException) |
| Maksymalne opóźnienie (Delay) | 4000 ms | Zabezpieczenie przed blokowaniem |
| Długość nazwy skryptu | 200 znaków | VARCHAR(200) w BD |
| Rozmiar kodu skryptu | MEDIUMTEXT (~16 MB) | Teoretycznie duży limit |
| Pętle nieskończone | Detektuje | StatementsCountOverflow |
| Dostęp do System.* | BRAK | Sandbox |
| require/import | BRAK | Sandbox |
| Dostęp do FileSystem | BRAK | Sandbox |
| Network sockets | BRAK | Tylko sendHttp |

### Błędy Wykonania

```javascript
// Timeout (5 sekund)
while (true) {
    // ERROR: "Skrypt anulowany (timeout lub przerwanie)"
}

// Przepełnienie pamięci (4 MB)
let array = [];
for (let i = 0; i < 1000000; i++) {
    array.push(new Array(100000).fill(0));
    // ERROR: Przepełnienie pamięci
}

// Opóźnienie ponad limit
rdm.delay(5000);
// OK - zostanie zwiniete do 4000ms
```

---

## 9. Wyzwolenie Skryptów

### Przez Mapowanie Wyzwalacza

```
Ustawienia Hardware Manager:
┌─────────────────────────────────────┐
│ Trigger: MIDI Note 45               │
│ Action: AutomationRunScript          │
│ Parameter: [GUID skryptu]            │
└─────────────────────────────────────┘
```

### Przez Makro

```
Makro "KOMPLEKSOWA AUDYCJA"
├─ Krok 1: ActionId = AutomationRunScript
│          Parameter = a1b2c3d4-e5f6-7890-abcd-ef1234567890
└─ Krok 2: (inne akcje)
```

### Przez HTTP API

```
POST /api/automation/script/run?id=a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

---

## 10. Monitorowanie i Debugowanie

### Logowanie

Wszystkie wiadomości z `rdm.log()` trafiają do:
- Pliku: `C:\ProgramData\RDM\rdm.log`
- Konsoli: Jeśli uruchomiona w debug mode

```javascript
rdm.log("[DEBUG] Zmienna x = " + x);
rdm.log("[ERROR] Coś poszło nie tak!");
rdm.log("[INFO] Skrypt zakończony pomyślnie");
```

### Monitorowanie Wydajności

ScriptRunner loguje czas wykonania:

```
[Information] ScriptRunner: skrypt 'AUDYCJA' zakończony w 1234ms
[Warning] ScriptRunner: skrypt 'LOGIKA' zakończony błędem: Timeout
```

---

## 11. Praktyczne Wskazówki

### Dobra Praktyka ✓

```javascript
// Jasne nazwy zmiennych
const slotIndex = 3;
cart.triggerSlot(slotIndex);

// Logowanie kluczowych punktów
rdm.log("Krok 1: Inicjalizacja");
rdm.log("Krok 2: Wykonanie");

// Obsługa limitów
if (totalDelay > 4000) {
    rdm.delay(4000);  // Maksymalnie 4000ms
} else {
    rdm.delay(totalDelay);
}
```

### Zła Praktyka ✗

```javascript
// Nieskończona pętla
while (true) {
    player.play();  // TIMEOUT!
}

// Nieznane typy
var x = "123";
var y = x + 100;  // JavaScript coercion!

// Brak logowania
mic.on();
player.play();
// Jak wiemy czy się udało?
```

---

## 12. Podsumowanie

JavaScript w RDM umożliwia:

✅ **Zaawansowaną Automatyzację** — Warunkowa logika i pętle  
✅ **Integracje Zewnętrzne** — HTTP i urządzenia szeregowe  
✅ **Bezpieczny Sandbox** — Ochrona przed wyciekami danych  
✅ **Dynamiczny Kod** — Brak potrzeby rekompilacji aplikacji  
✅ **Szybkie Testowanie** — Zmiana kodu w edytorze i natychmiastowe wykonanie  
✅ **Pełna Kontrola** — Dostęp do całej infrastruktury RDM  

Skrypty JavaScript to idealne narzędzie dla operatorów radiowych do automatyzacji złożonych scenariuszy emisyjnych bez wiedzy programistycznej na poziomie C#.
