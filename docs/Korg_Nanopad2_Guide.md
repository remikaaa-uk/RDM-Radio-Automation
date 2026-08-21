# Guide: Setting Up the Korg Nanopad2 in RDM

## Table of Contents
1. [Hardware Preparation](#hardware-preparation)
2. [Connecting the Nanopad2](#connecting-the-nanopad2)
3. [Checking Whether the System Sees the Device](#checking-whether-the-system-sees-the-device)
4. [Configuring RDM](#configuring-rdm)
5. [Teaching the Pads (Learn Mode)](#teaching-the-pads-learn-mode)
6. [Setting Actions for the Pads](#setting-actions-for-the-pads)
7. [Common Mappings](#common-mappings)
8. [Testing](#testing)
9. [Troubleshooting](#troubleshooting)

---

## Hardware Preparation

### Requirements
- ✅ Korg Nanopad2 (USB)
- ✅ USB power/data cable
- ✅ RDM installed and ready

### What is the Nanopad2?
The **Korg Nanopad2** is a compact MIDI controller with 16 pads for triggering beats/samples. It sends:
- **Note ON/OFF** - when you press a pad (e.g. Note 36-51)
- **Velocity** - how hard the pad is pressed (0-127)
- **Polyphonic** - multiple pads can be played at the same time

---

## Connecting the Nanopad2

### Step 1: Physical connection

1. Take the **USB cable** (used for both power and data)
2. Plug it into the **USB port** on the Nanopad2
3. Plug the other end into the **computer**
4. Wait **~3 seconds** for the system to recognise it

### Step 2: Turn on the Nanopad2 (if it has a power switch)

- Some versions have a **power toggle** - switch it on
- You should see an **LED** light up on the device

### Step 3: Initialise MIDI (if needed)

- The Nanopad2 may have a predefined **MIDI Channel** (usually Channel 1)
- Note down the channel (you'll need it for configuration)

---

## Checking Whether the System Sees the Device

### Method 1: Device Manager (Windows)

1. Press **`Win + X`**
2. Select **"Device Manager"**
3. Expand **"Sound, video and game controllers"** (or "Other devices")
4. Look for an entry containing **"Korg"** or **"nanopad"**
5. If it's there - the device is visible ✅

It should look something like this:
```
├─ Sound, video and game controllers
│  ├─ Korg nanoPAD2
│  └─ [other devices]
```

### Method 2: DAW (easiest)

1. Open any DAW (Reaper, Studio One, Cubase, FL Studio, etc.)
2. Go to **MIDI Input devices**
3. Look for **"Korg"** or **"Nanopad"**
4. If it's there - great! ✅

### Method 3: PowerShell

```powershell
# Open PowerShell and type:
Get-PnpDevice -Class "Multimedia" | Select-Object Name, Status
```

You should see an entry containing **"Korg"** with status **"OK"**

---

## Configuring RDM

### Step 1: Launch RDM

1. Open the **RDM** application
2. Wait for it to fully load (~10 seconds)
3. Go to **Hardware Manager** (in the main menu)

### Step 2: Check whether MIDI is visible

1. In **Hardware Manager** - the **"Trigger Mappings"** tab
2. Check the status/logs
3. You should see:
   ```
   MidiInputDriver: found 1 MIDI device(s)
   MidiInputDriver: opened 'Korg nanoPAD2'
   ```

**If nothing appears:**
- ⚠️ Go to the [Troubleshooting](#troubleshooting) section

---

## Teaching the Pads (Learn Mode)

**Learn Mode** is the fastest way to teach the program what each pad does.

### Step 1: Activate Learn Mode

1. Open **Hardware Manager** in RDM
2. Click the **"Start Learn"** button (bottom left)
3. The status should change to:
   ```
   "Press a MIDI/D&R key or button..."
   ```

### Step 2: Press a PAD on the Nanopad2

1. With Learn Mode active - **press PAD 1** (top left)
2. The program should **detect it immediately**
3. The status will change to:
   ```
   "Detected: MIDI / MidiNote_Ch1_N36"
   ```

### Step 3: Note down the signature

The program will display something like:
```
DeviceType:    MIDI
Signature:     MidiNote_Ch1_N36
```

**Remember the signature** - you'll need it!

Example for all pads (Nanopad2 default layout):
```
Pad 1  (top left)      → MidiNote_Ch1_N36
Pad 2  (top 2nd)       → MidiNote_Ch1_N37
Pad 3  (top 3rd)       → MidiNote_Ch1_N38
Pad 4  (top right)     → MidiNote_Ch1_N39
Pad 5  (row 2, left)   → MidiNote_Ch1_N48
Pad 6  (row 2, 2nd)    → MidiNote_Ch1_N49
...
Pad 16 (bottom right)  → MidiNote_Ch1_N51
```

### Step 4: Repeat for all pads

1. Click **"Start Learn"** again
2. Press **PAD 2**
3. Note down the signature
4. Repeat up to **PAD 16**

**Pro tip:** Save all signatures in a notepad - it'll make building mappings much easier!

---

## Setting Actions for the Pads

### Step 1: Add a new Trigger Mapping

1. In **Hardware Manager** → **"Trigger Mappings"**
2. Click the **"+"** button (add new mapping)
3. The **"Edit Trigger Mapping"** dialog opens

### Step 2: Fill in the form

**Example - Pad 1 to Play/Pause**:

```
Name:               Nanopad Pad 1 - Play
Device type:        MIDI
Device ID:          (empty)
Signature:          MidiNote_Ch1_N36        ← From Learn Mode!
Action:              PlayerPlay
Parameter:           (empty)
Active:              ✅ (checked)
```

### Step 3: Pick the right action

Click the **"Action"** field and choose from the list:

#### Player Actions (main player)
```
PlayerPlay              - Start playback
PlayerStop              - Stop playback
PlayerPause             - Pause (resume with the same key)
PlayerNext               - Next track
PlayerPlayStopToggle    - Toggle play/stop (one key for start/stop)
PlayerLoopToggle        - Toggle loop (repeat the track)
PlayerRemoveFromPlayer  - Remove from the playlist
```

#### Microphone Actions
```
MicOn                   - Turn the microphone on
MicOff                  - Turn the microphone off
MicToggle               - Toggle (one key for on/off)
MicTalkback             - Talkback (talk to another station)
```

#### Cartwall Actions (pad player)
```
CartwallPlaySlot        - Play a slot (requires the slot number as a parameter: "1", "2", etc.)
CartwallStopSlot        - Stop a slot
CartwallStopAll         - Stop all slots
CartwallToggleLoop      - Toggle loop for a slot
CartwallToggleMode      - Toggle mode (legato/non-legato)
CartwallTab1 to Tab6    - Switch to another tab
CartwallTriggerSlot1-16 - Trigger a specific slot
```

#### Aux Player Actions (aux players 1-4)
```
AuxPlay1-4              - Play on the aux player
AuxStop1-4              - Stop the aux player
AuxToggleOn1-4          - Toggle on/off
AuxToggleLoop1-4        - Toggle loop
AuxTogglePfl1-4         - Toggle PFL (pre-fader listen)
AuxEject1-4             - Eject the track
```

#### Automation Actions
```
AutomationTriggerMacro  - Run a macro (requires the macro ID as a parameter)
AutomationRunScript     - Run a JavaScript script
AutomationSendHttp      - Send an HTTP request (requires a URL)
AutomationEmergencyPanic - Emergency stop everything
```

#### Other Actions
```
RecorderStart           - Start recording
RecorderStop            - Stop recording
VisualTriggerTimer      - Trigger a visual timer
WindowTracksManager     - Open the Track Manager window
WindowPlaylistBuilder   - Open the Playlist Builder
WindowHardwareManager   - Open the Hardware Manager
Save, Undo, Redo        - Editing
```

### Step 4: Finish the configuration

1. Click **"Save"**
2. The mapping will appear in the list

---

## Common Mappings

### Scenario 1: Playback control (most popular)

| Pad      | Action              |
|----------|---------------------|
| Pad 1    | PlayerPlay          |
| Pad 2    | PlayerStop          |
| Pad 3    | PlayerNext          |
| Pad 4    | PlayerLoopToggle    |
| Pad 5    | MicToggle           |
| Pad 6-16 | (unassigned)        |

### Scenario 2: Cartwall hotkeys (for playing pads)

| Pad     | Action              | Parameter |
|---------|---------------------|-----------|
| Pad 1   | CartwallPlaySlot    | 1         |
| Pad 2   | CartwallPlaySlot    | 2         |
| Pad 3   | CartwallPlaySlot    | 3         |
| Pad 4   | CartwallPlaySlot    | 4         |
| Pad 5   | CartwallPlaySlot    | 5         |
| Pad 6   | CartwallPlaySlot    | 6         |
| Pad 7   | CartwallPlaySlot    | 7         |
| Pad 8   | CartwallPlaySlot    | 8         |
| ...     | ... and so on to 16 | ...       |

**How to set this up for Cartwall:**
1. Action: `CartwallPlaySlot`
2. Parameter: `1` (for Pad 1), `2` (for Pad 2), etc.

### Scenario 3: Mixed (playback + microphone + cartwall)

| Pad       | Action              |
|-----------|---------------------|
| Pad 1     | PlayerPlay          |
| Pad 2     | PlayerStop          |
| Pad 3     | PlayerNext          |
| Pad 4     | MicToggle           |
| Pad 5-12  | CartwallPlaySlot    |
| Pad 13    | PlayerLoopToggle    |
| Pad 14-16 | (unassigned)        |

---

## Testing

### Test 1: Are signals reaching RDM?

1. Open **Hardware Manager**
2. Click **"Start Learn"**
3. **Press a PAD** on the Nanopad2
4. You should see: `"Detected: MIDI / MidiNote_Ch1_Nxx"`

**If nothing appears:**
- Check [Troubleshooting](#troubleshooting)

### Test 2: Do actions actually run?

1. Create a mapping: Pad 1 → PlayerPlay
2. Open the **Playlist** in RDM
3. Load a track
4. **Press Pad 1** on the Nanopad2
5. The track should start playing!

### Test 3: Check every pad

1. Press each pad in turn
2. Check whether its action runs
3. If not - is the mapping active? (`Active: ✓`)

### Test 4: Review the logs

```
C:\ProgramData\RDM\rdm.log
```

Look for lines like:
```
MidiInputDriver: opened 'Korg nanoPAD2'
ActionRouter: executing action PlayerPlay
HardwareManager: SaveTriggerMapping
```

If you see these lines - everything is working! ✅

---

## Troubleshooting

### Problem 1: "The Nanopad2 doesn't appear in RDM"

#### Cause A: The device isn't connected
- ✅ Plug in the **USB cable**
- ✅ Wait **3 seconds**
- ✅ Restart RDM

#### Cause B: Missing driver
- ✅ Windows 10/11 should install it automatically
- ✅ If not - download the driver from the **Korg** website (korg.com)
- ✅ Install it and restart the computer

#### Cause C: The MIDI port is in use
- ✅ Close your **DAW** (Reaper, Studio One, etc.)
- ✅ Close any **other MIDI applications**
- ✅ Restart RDM

#### Cause D: Wrong configuration in RDM
- ✅ Check the file: `C:\ProgramData\RDM\rdm.log`
- ✅ Look for errors containing "MIDI" or "Korg"

### Problem 2: "The Nanopad2 is visible but doesn't respond to presses"

#### Cause A: Learn Mode isn't working
- ✅ Open **Learn Mode**
- ✅ Press **PAD 1**
- ✅ If nothing appears - restart RDM

#### Cause B: Wrong signature in the mapping
- ✅ Start Learn Mode
- ✅ Press the pad and note down the signature exactly
- ✅ Edit the mapping and **paste the signature** (don't type it by hand!)

#### Cause C: The mapping is inactive
- ✅ In Hardware Manager - check whether the **"Active"** checkbox is ticked
- ✅ If not - click the mapping, edit it and tick it

#### Cause D: The action doesn't exist
- ✅ Open the mapping edit form
- ✅ Click the **"Action"** field
- ✅ Choose from the dropdown list (don't type it by hand!)

### Problem 3: "The pad works sometimes, but not always"

#### Cause: Conflict with Learn Mode
- ✅ Check whether Learn Mode is still active
- ✅ If so - click **"Cancel Learn"**
- ✅ Restart RDM

#### Cause: The cache wasn't reloaded
- ✅ Restart the RDM application

### Problem 4: "I see MIDI errors in the logs"

```
MidiInputDriver: MIDI Input error
Exception: ...
```

- ✅ The MIDI port may be in use by another application
- ✅ Close **all DAWs** and MIDI applications
- ✅ Restart RDM

### Problem 5: "It's sending Note messages instead of CC"

By default, the Nanopad2 sends **Note On/Off** (not CC). If you need CC:
- ✅ Check the Nanopad2 documentation
- ✅ There may be a mode switch (hold a button to change it)
- ✅ Or use a mapper (as with the DR_Mixer)

---

## Useful Files and Paths

### Configuration location

```
Main configuration file:
C:\ProgramData\RDM\rdm.config.json

Application logs:
C:\ProgramData\RDM\rdm.log
```

### Mappings database

Mappings are stored in the **database** (MySQL):
```
Host:     <database server address>

Port:     3306
Database: rdm
Tables:   TriggerActionMappings, FeedbackRules
```

---

## Tips & Tricks

### Tip 1: Copy signatures from Learn Mode
```
1. Start Learn Mode
2. Press a pad
3. The signature is displayed (e.g. "MidiNote_Ch1_N36")
4. Copy it (Ctrl+C) from the status window
5. Paste it into the field (Ctrl+V) - avoids typos!
```

### Tip 2: Use readable mapping names
```
❌ "Trigger 1"
❌ "Pad test"
✅ "Nanopad Pad 1 - PlayerPlay"
✅ "Nanopad Cart Slot 1-8"
```

### Tip 3: Test one mapping at a time
```
1. Add a mapping
2. Save it
3. Test whether it works
4. If OK - add the next one
= Much easier to debug!
```

### Tip 4: Use PlayerPlayStopToggle to save space
```
Instead of two mappings:
  Pad 1 → PlayerPlay
  Pad 2 → PlayerStop

Use one:
  Pad 1 → PlayerPlayStopToggle (play or stop with the same pad)
```

### Tip 5: Combine with other devices
```
If you have both a Nanopad2 and a DR_Mixer:
- Nanopad2 → Pads (triggers)
- DR_Mixer → Faders (volume)
= A perfect combination!
```

### Tip 6: Save a mapping template
```
If you want to be able to quickly restore your configuration:
1. Export the trigger mappings (from the database)
2. Keep a note of every signature
3. On reinstall - restore it quickly
```

---

## Quick Start (3 minutes)

If you want to get going right away:

1. Plug in the Nanopad2 via USB
2. Open RDM → Hardware Manager
3. Click "Start Learn"
4. Press PAD 1 on the Nanopad2
   You'll see: "MidiNote_Ch1_N36"
5. Add a Trigger Mapping:
   - Name: "Pad 1 Play"
   - Type: MIDI
   - Signature: MidiNote_Ch1_N36
   - Action: PlayerPlay
6. Click "Save"
7. Test it - press PAD 1

✅ The track should start playing! 🎵

---

## Frequently Asked Questions

**Q: Can I use the Nanopad2 alongside the DR_Mixer at the same time?**
A: Yes! Nanopad2 = pads (triggers), DR_Mixer = faders (volume). A great combination.

**Q: How many mappings can I have?**
A: No limit! Each pad can have one mapping (plus feedback).

**Q: Can I map one pad to multiple actions?**
A: Not directly, but you can create a **Macro** (a set of actions) and trigger that instead.

**Q: Does the Nanopad2 send CC or Note messages?**
A: By default, **Note On/Off**. If you need CC, change it in the device's own settings.

**Q: What should I do if a pad responds sometimes but not other times?**
A: Check whether Learn Mode is still active. Restart RDM.

---

## Related Guides

- [Polish version of this guide](Poradnik_Korg_Nanopad2.md)
- [Guide: Setting Up the DR_Mixer (Airlite2)](Poradnik_DR_Mixer_Airlite2.md)
- [JavaScript Scripting Guide](JavaScript_Scripting_Guide.md)
- [RDM API Documentation](RDM_API_Documentation.md)
- [RDM Local Configuration File](RDM_LocalConfig.md)
- [RDM Installation Guide](RDM_Installation_Guide.md)

---

**Last updated**: 2026-07-01
**Tested with**: Korg Nanopad2 + Windows 11
