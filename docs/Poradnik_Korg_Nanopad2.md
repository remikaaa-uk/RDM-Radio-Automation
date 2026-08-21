# Poradnik: Konfiguracja Korg Nanopad2 w RDM

## Spis treści
1. [Przygotowanie sprzętu](#przygotowanie-sprzętu)
2. [Podłączenie Nanopad2](#podłączenie-nanopad2)
3. [Sprawdzenie czy system widzi urządzenie](#sprawdzenie-czy-system-widzi-urządzenie)
4. [Konfiguracja RDM](#konfiguracja-rdm)
5. [Nauczanie padów (Learn Mode)](#nauczanie-padów-learn-mode)
6. [Ustawianie akcji dla padów](#ustawianie-akcji-dla-padów)
7. [Typowe mapowania](#typowe-mapowania)
8. [Testowanie](#testowanie)
9. [Rozwiązywanie problemów](#rozwiązywanie-problemów)

---

## Przygotowanie sprzętu

### Wymagania
- ✅ Korg Nanopad2 (USB)
- ✅ Kabel USB zasilający/komunikacyjny
- ✅ RDM zainstalowany i gotowy

### Co to jest Nanopad2?
**Korg Nanopad2** to kompaktowy kontroler MIDI z 16 padami do wybijania bitów/triggerów. Wysyła:
- **Note ON/OFF** - gdy naciśniesz pad (np. Note 36-51)
- **Velocity** - siła naciśnięcia (0-127)
- **Polyphonic** - możliwość grania wielu padów jednocześnie

---

## Podłączenie Nanopad2

### Krok 1: Fizyczne podłączenie

1. Weź **kabel USB** (do ładowania i komunikacji)
2. Podłącz do **portu USB** na Nanopad2
3. Podłącz drugi koniec do **komputera**
4. Czekaj **~3 sekundy** aż system go rozpozna

### Krok 2: Włącz Nanopad2 (jeśli ma włącznik)

- Niektóre wersje mają **toggle zasilania** - włącz go
- Powinieneś widzieć **LED** na urządzeniu

### Krok 3: Zainicjuj MIDI (jeśli trzeba)

- Nanopad2 może mieć predefiniowane **MIDI Channel** (zazwyczaj Channel 1)
- Zanotuj kanał (będzie potrzebny do konfiguracji)

---

## Sprawdzenie czy system widzi urządzenie

### Metoda 1: Device Manager (Windows)

1. Kliknij **`Win + X`**
2. Wybierz **"Menedżer urządzeń"**
3. Rozwiń **"Kontrolery USB"** (lub "Other devices")
4. Szukaj pozycji zawierającej **"Korg"** lub **"nanopad"**
5. Jeśli jest - urządzenie jest widoczne ✅

Powinno wyglądać tak:
```
├─ Kontrolery USB
│  ├─ Korg nanoPAD2
│  └─ [inne urządzenia]
```

### Metoda 2: DAW (najłatwiej)

1. Otwórz dowolny DAW (Reaper, Studio One, Cubase, FL Studio itp)
2. Przejdź do **MIDI Input devices**
3. Szukaj **"Korg"** lub **"Nanopad"**
4. Jeśli jest - świetnie! ✅

### Metoda 3: PowerShell

```powershell
# Otwórz PowerShell i wpisz:
Get-PnpDevice -Class "Multimedia" | Select-Object Name, Status
```

Powinno pojawić się coś zawierającego **"Korg"** ze statusem **"OK"**

---

## Konfiguracja RDM

### Krok 1: Uruchom RDM

1. Otwórz aplikację **RDM**
2. Czekaj aż się całkowicie załaduje (~10 sekund)
3. Przejdź do **Hardware Manager** (w menu głównym)

### Krok 2: Sprawdź czy MIDI jest widoczne

1. W **Hardware Manager** - zakładka **"Trigger Mappings"**
2. Spójrz w status/logi
3. Powinno być:
   ```
   MidiInputDriver: znaleziono 1 urządzeń MIDI
   MidiInputDriver: otwarto 'Korg nanoPAD2'
   ```

**Jeśli nic się nie pojawia:**
- ⚠️ Przejdź do sekcji [Rozwiązywanie problemów](#rozwiązywanie-problemów)

---

## Nauczanie padów (Learn Mode)

**Learn Mode** to najszybszy sposób na nauczenie programu co robi każdy pad.

### Krok 1: Aktywuj Learn Mode

1. Otwórz **Hardware Manager** w RDM
2. Kliknij przycisk **"Start Learn"** (u dołu, po lewej)
3. Status powinien zmienić się na:
   ```
   "Naciśnij klawisz lub przycisk MIDI/D&R..."
   ```

### Krok 2: Naciśnij PAD na Nanopad2

1. Gdy Learn Mode jest aktywny - **naciśnij PAD 1** (lewy górny)
2. Program powinien natychmiast to **wykryć**
3. Status zmieni się na:
   ```
   "Wykryto: MIDI / MidiNote_Ch1_N36"
   ```

### Krok 3: Zapamiętaj sygnaturę

Program wyświetli coś takiego:
```
DeviceType:    MIDI
Signature:     MidiNote_Ch1_N36
```

**Zapamiętaj sygnaturę** - będziesz jej potrzebować!

Przykład dla wszystkich padów (Nanopad2 domyślnie):
```
Pad 1  (górny lewy)    → MidiNote_Ch1_N36
Pad 2  (górny 2)       → MidiNote_Ch1_N37
Pad 3  (górny 3)       → MidiNote_Ch1_N38
Pad 4  (górny prawy)   → MidiNote_Ch1_N39
Pad 5  (2 wiersz lewy) → MidiNote_Ch1_N48
Pad 6  (2 wiersz 2)    → MidiNote_Ch1_N49
...
Pad 16 (dolny prawy)   → MidiNote_Ch1_N51
```

### Krok 4: Powtórz dla wszystkich padów

1. Kliknij **"Start Learn"** ponownie
2. Naciśnij **PAD 2**
3. Zapamiętaj sygnaturę
4. Powtarzaj aż do **PAD 16**

**💡 Pro tip:** Zapisz wszystkie sygnatury w notatniku - ułatwi ci tworzenie mapowań!

---

## Ustawianie akcji dla padów

### Krok 1: Dodaj nowe Trigger Mapping

1. W **Hardware Manager** → **"Trigger Mappings"**
2. Kliknij przycisk **"+"** (dodaj nowe mapowanie)
3. Otwórzy się dialog **"Edytuj Trigger Mapping"**

### Krok 2: Wypełnij formularz

**Przykład - Pad 1 do Play/Pauzy**:

```
Nazwa:              Nanopad Pad 1 - Play
Typ urządzenia:     MIDI
ID urządzenia:      (puste)
Sygnatura:          MidiNote_Ch1_N36        ← Z Learn Mode!
Akcja:              PlayerPlay
Parametr:           (puste)
Aktywny:            ✅ (zaznaczony)
```

### Krok 3: Wybierz odpowiednią akcję

Kliknij pole **"Akcja"** i wybierz z listy:

#### Akcje dla Player (główny gracz)
```
PlayerPlay              - Start odtwarzania
PlayerStop              - Stop odtwarzania
PlayerPause             - Pauza (wznów tym samym klawiszem)
PlayerNext              - Następny utwór
PlayerPlayStopToggle    - Toggle play/stop (jeden klawisz do start/stop)
PlayerLoopToggle        - Toggle pętli (powtarzanie utworu)
PlayerRemoveFromPlayer  - Usuń z playlisty
```

#### Akcje dla Mikrofonu
```
MicOn                   - Włącz mikrofon
MicOff                  - Wyłącz mikrofon
MicToggle               - Toggle (jeden klawisz do on/off)
MicTalkback             - Talkback (rozmowa z inną stacją)
```

#### Akcje dla Cartwall (pad player)
```
CartwallPlaySlot        - Zagraj slot (wymaga nr slota w parametrze: "1", "2" itp)
CartwallStopSlot        - Stop slota
CartwallStopAll         - Stop wszystkich slotów
CartwallToggleLoop      - Toggle pętli dla slota
CartwallToggleMode      - Toggle mode (legato/non-legato)
CartwallTab1 do Tab6    - Przejście na inny tab
CartwallTriggerSlot1-16 - Wyzwól konkretny slot
```

#### Akcje dla Aux Players (aux playery 1-4)
```
AuxPlay1-4              - Play na aux playerze
AuxStop1-4              - Stop aux playera
AuxToggleOn1-4          - Toggle on/off
AuxToggleLoop1-4        - Toggle pętli
AuxTogglePfl1-4         - Toggle PFL (pre-fader listen)
AuxEject1-4             - Wysuń utwór
```

#### Akcje dla Automatyzacji
```
AutomationTriggerMacro  - Uruchom macro (wymaga ID macro w parametrze)
AutomationRunScript     - Uruchom skrypt JavaScript
AutomationSendHttp      - Wyślij HTTP request (wymaga URL)
AutomationEmergencyPanic - Emergency stop wszystkiego
```

#### Inne akcje
```
RecorderStart           - Start nagrywania
RecorderStop            - Stop nagrywania
VisualTriggerTimer      - Wyzwól timer wizualny
WindowTracksManager     - Otwórz okno Track Manager
WindowPlaylistBuilder   - Otwórz Playlist Builder
WindowHardwareManager   - Otwórz Hardware Manager
Save, Undo, Redo        - Edycja
```

### Krok 4: Ukończ konfigurację

1. Kliknij **"Zapisz"**
2. Mapowanie pojawi się na liście

---

## Typowe mapowania

### Scenario 1: Kontrola playbacku (najpopularniejszy)

| Pad     | Akcja              |
|---------|---------------------|
| Pad 1   | PlayerPlay          |
| Pad 2   | PlayerStop          |
| Pad 3   | PlayerNext          |
| Pad 4   | PlayerLoopToggle    |
| Pad 5   | MicToggle           |
| Pad 6-16 | (nieprzypisane)    |

### Scenario 2: Cartwall hotkeys (do gry padami)

| Pad     | Akcja              | Parametr |
|---------|---------------------|----------|
| Pad 1   | CartwallPlaySlot    | 1        |
| Pad 2   | CartwallPlaySlot    | 2        |
| Pad 3   | CartwallPlaySlot    | 3        |
| Pad 4   | CartwallPlaySlot    | 4        |
| Pad 5   | CartwallPlaySlot    | 5        |
| Pad 6   | CartwallPlaySlot    | 6        |
| Pad 7   | CartwallPlaySlot    | 7        |
| Pad 8   | CartwallPlaySlot    | 8        |
| ...     | ... itd. do 16      | ...      |

**Jak ustawić dla Cartwall:**
1. Akcja: `CartwallPlaySlot`
2. Parametr: `1` (dla Padu 1), `2` (dla Padu 2) itd

### Scenario 3: Mieszany (playback + mikrofon + cartwall)

| Pad      | Akcja              |
|----------|---------------------|
| Pad 1    | PlayerPlay          |
| Pad 2    | PlayerStop          |
| Pad 3    | PlayerNext          |
| Pad 4    | MicToggle           |
| Pad 5-12 | CartwallPlaySlot    |
| Pad 13   | PlayerLoopToggle    |
| Pad 14-16 | (nieprzypisane)    |

---

## Testowanie

### Test 1: Czy sygnały docierają do RDM?

1. Otwórz **Hardware Manager**
2. Kliknij **"Start Learn"**
3. **Naciśnij PAD** na Nanopad2
4. Powinno pojawić się: `"Wykryto: MIDI / MidiNote_Ch1_Nxx"`

**Jeśli nic się nie pojawia:**
- Sprawdzaj [Rozwiązywanie problemów](#rozwiązywanie-problemów)

### Test 2: Czy akcje się wykonują?

1. Utwórz mapowanie: Pad 1 → PlayerPlay
2. Otwórz **Playlist** w RDM
3. Załaduj utwór
4. **Naciśnij Pad 1** na Nanopad2
5. Piosenka powinna się zagrać! 🎵

### Test 3: Sprawdzaj wszystkie pady

1. Dla każdego padu - naciśnij go
2. Sprawdź czy akcja się wykonuje
3. Jeśli nie - czy mapowanie jest aktywne? (`Aktywny: ✓`)

### Test 4: Przejrzyj logi

```
C:\ProgramData\RDM\rdm.log
```

Szukaj linii:
```
MidiInputDriver: otwarto 'Korg nanoPAD2'
ActionRouter: executing action PlayerPlay
HardwareManager: SaveTriggerMapping
```

Jeśli widzisz te linie - wszystko działa! ✅

---

## Rozwiązywanie problemów

### Problem 1: "Nanopad2 nie pojawia się w RDM"

#### Przyczyna A: Urządzenie nie jest podłączone
- ✅ Podłącz **kabel USB**
- ✅ Czekaj **3 sekundy**
- ✅ Zrestartuj RDM

#### Przyczyna B: Brak sterownika
- ✅ Windows 10/11 powinien zainstalować automatycznie
- ✅ Jeśli nie - pobierz sterownik ze strony **Korg** (korg.com)
- ✅ Zainstaluj i zrestartuj komputer

#### Przyczyna C: Port MIDI jest zajęty
- ✅ Zamknij **DAW** (Reaper, Studio One itp)
- ✅ Zamknij **inne aplikacje MIDI**
- ✅ Zrestartuj RDM

#### Przyczyna D: Zła konfiguracja w RDM
- ✅ Sprawdź plik: `C:\ProgramData\RDM\rdm.log`
- ✅ Szukaj błędów zawierających "MIDI" lub "Korg"

### Problem 2: "Nanopad2 jest widoczny ale nie reaguje na naciśnięcia"

#### Przyczyna A: Learn Mode nie działa
- ✅ Otwórz **Learn Mode**
- ✅ Naciśnij **PAD 1**
- ✅ Jeśli nic się nie pojawia - zrestartuj RDM

#### Przyczyna B: Zła sygnatura w mapowaniu
- ✅ Uruchom Learn Mode
- ✅ Naciśnij pad i zapamiętaj dokładnie sygnaturę
- ✅ Edytuj mapowanie i **wklej sygnaturę** (nie pisz ręcznie!)

#### Przyczyna C: Mapowanie jest nieaktywne
- ✅ W Hardware Manager - sprawdzaj czy checkbox **"Aktywny"** jest zaznaczony
- ✅ Jeśli nie - kliknij na mapowanie, edytuj i zaznacz

#### Przyczyna D: Akcja nie istnieje
- ✅ Otwórz formularz edycji mapowania
- ✅ Kliknij w pole **"Akcja"**
- ✅ Wybierz z wyświetlanej listy (nie pisz ręcznie!)

### Problem 3: "Pad czasami działa, czasami nie"

#### Przyczyna: Konflikt z Learn Mode
- ✅ Sprawdzaj czy Learn Mode nie jest aktywny
- ✅ Jeśli tak - kliknij **"Cancel Learn"**
- ✅ Zrestartuj RDM

#### Przyczyna: Cache nie został przeładowany
- ✅ Zrestartuj aplikację RDM

### Problem 4: "W logach widzę błędy MIDI"

```
MidiInputDriver: Błąd MIDI Input
Exception: ...
```

- ✅ Port MIDI może być używany przez inną aplikację
- ✅ Zamknij **wszystkie DAW** i aplikacje MIDI
- ✅ Zrestartuj RDM

### Problem 5: "Przesyła Note zamiast CC"

Nanopad2 domyślnie wysyła **Note On/Off** (nie CC). Jeśli potrzebujesz CC:
- ✅ Sprawdzaj dokumentację Nanopad2
- ✅ Może być mód do zmiany (hold przycisk, zmiana)
- ✅ Lub użyj mappera (jak dla DR_Mixer)

---

## Przydatne pliki i ścieżki

### Lokalizacja konfiguracji

```
Główny plik konfiguracji:
C:\ProgramData\RDM\rdm.config.json

Logi aplikacji:
C:\ProgramData\RDM\rdm.log
```

### Baza danych mapowań

Mapowania są przechowywane w **bazie danych** (MySQL):
```
Host:     <adres serwera bazy>

Port:     3306
Database: rdm
Tabele:   TriggerActionMappings, FeedbackRules
```

---

## Tips & Tricks

### Tip 1: Kopia sygnatur z Learn Mode
```
1. Uruchom Learn Mode
2. Naciśnij pad
3. Wyświetli się sygnatura (np. "MidiNote_Ch1_N36")
4. Skopiuj (Ctrl+C) z okna statusu
5. Wklej w polu (Ctrl+V) - unika błędów!
```

### Tip 2: Używaj czytelnych nazw mapowań
```
❌ "Trigger 1"
❌ "Pad test"
✅ "Nanopad Pad 1 - PlayerPlay"
✅ "Nanopad Cart Slot 1-8"
```

### Tip 3: Testuj jedno mapowanie na raz
```
1. Dodaj mapowanie
2. Zapisz
3. Testuj czy działa
4. Jeśli OK - dodaj następne
= Łatwiej debugować!
```

### Tip 4: Używaj PlayerPlayStopToggle dla kompaktowości
```
Zamiast dwóch mapowań:
  Pad 1 → PlayerPlay
  Pad 2 → PlayerStop

Użyj jednego:
  Pad 1 → PlayerPlayStopToggle (play lub stop tym samym padzie)
```

### Tip 5: Synchronizuj z innymi urządzeniami
```
Jeśli masz zarówno Nanopad2 jak i DR_Mixer:
- Nanopad2 → Pady (triggery)
- DR_Mixer → Faders (volume)
= Idealne połączenie!
```

### Tip 6: Zapisz template mapowań
```
Jeśli chcesz szybko przywrócić konfigurację:
1. Wyeksportuj trigger mappings (z bazy danych)
2. Zapamiętaj wszystkie sygnatury
3. Przy reinstalacji - szybko przywrócisz
```

---

## Szybki Start (3 minuty)

Jeśli chcesz od razu zacząć:

1. Podłącz Nanopad2 USB
2. Otwórz RDM → Hardware Manager
3. Kliknij "Start Learn"
4. Naciśnij PAD 1 na Nanopad2
   Pojawi się: "MidiNote_Ch1_N36"
5. Dodaj Trigger Mapping:
   - Nazwa: "Pad 1 Play"
   - Typ: MIDI
   - Sygnatura: MidiNote_Ch1_N36
   - Akcja: PlayerPlay
6. Kliknij "Zapisz"
7. Testuj - naciśnij PAD 1

✅ Piosenka się powinna zagrać! 🎵

---

## Często zadawane pytania

**P: Czy mogę używać Nanopad2 z DR_Mixer jednocześnie?**
O: Tak! Nanopad2 = pady (triggery), DR_Mixer = faders (volume). Świetna kombinacja.

**P: Ile mapowań mogę mieć?**
O: Bez limitu! Każdy pad może mieć jedno mapowanie (+ feedback).

**P: Czy mogę zmapować jeden pad na wiele akcji?**
O: Nie bezpośrednio, ale możesz stworzyć **Macro** (zestaw akcji) i triggerować to.

**P: Czy Nanopad2 wysyła CC czy Note?**
O: Domyślnie **Note On/Off**. Jeśli potrzebujesz CC - zmień w ustawieniach urządzenia.

**P: Co zrobić jeśli pad czasami reaguje, czasami nie?**
O: Sprawdź czy Learn Mode nie jest aktywny. Zrestartuj RDM.

---

## Powiązane poradniki

- [Wersja angielska tego poradnika](Korg_Nanopad2_Guide.md)
- [Poradnik: Konfiguracja DR_Mixer (Airlite2)](Poradnik_DR_Mixer_Airlite2.md)
- [Poradnik: Instrukcja JS](Poradnik_Instrukcja_JS.md)
- [Dokumentacja API RDM](RDM_Dokumentacja_API.md)
- [Plik konfiguracji lokalnej RDM](RDM_plik_konfiguracji.md)
- [Instrukcja instalacji RDM](RDM_Instrukcja_Instalacji.md)

---

**Ostatnia aktualizacja**: 2026-07-01
**Testowane z**: Korg Nanopad2 + Windows 11
