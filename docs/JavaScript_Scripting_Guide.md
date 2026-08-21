# JavaScript Guide for RDM (Radio Digital Manager)

## 1. What is JavaScript in RDM?

JavaScript in RDM is an **advanced automation system** that lets you write scripts without recompiling the application. It allows you to build business logic, conditional workflows, and integrations with external systems.

### Characteristics:

- **Engine:** Jint (ECMAScript 2020+)
- **Isolation:** Each script runs in an isolated environment (sandbox)
- **Security:** No access to System.*, no require/import, protection against attacks
- **Resource Limits:**
  - **Timeout:** Maximum 5 seconds of execution
  - **Memory:** Maximum 4 MB RAM
  - **Statements:** Protection against infinite loops
- **Integration:** Full integration with the RDM action system and hardware devices
- **Persistence:** Scripts are stored in the database (`scripts` table)
- **Management:** Scripts can be enabled/disabled without restarting the application

---

## 2. Data Structure

### Script (`Script.cs`)

```csharp
public class Script
{
    public Guid   Id          { get; set; }       // Unique identifier
    public string Name        { get; set; }       // Script name
    public string ScriptBody  { get; set; }       // JavaScript code
    public bool   IsEnabled   { get; set; }       // Enabled/Disabled
    public string Language    { get; set; }       // "js" (other languages may be added later)
}
```

### Execution Result (`ScriptResult.cs`)

```csharp
public record ScriptResult(bool Success, string? Error = null, long ElapsedMs = 0)
{
    public static ScriptResult Ok(long elapsedMs)  => new(true,  null,  elapsedMs);
    public static ScriptResult Fail(string error)  => new(false, error, 0);
}
```

---

## 3. Available APIs

Scripts can access the following APIs, organised into namespaces:

### 3.1. Player API (`player`)

```javascript
player.play()      // Start playback
player.stop()      // Stop playback
player.pause()     // Pause
player.next()      // Next track
```

**Example:**
```javascript
player.play();
rdm.delay(1000);
player.next();
```

---

### 3.2. Microphone API (`mic`)

```javascript
mic.on()      // Turn the microphone on
mic.off()     // Turn the microphone off
mic.toggle()  // Toggle the microphone (on/off)
```

**Example:**
```javascript
mic.on();
rdm.log("Microphone on");
```

---

### 3.3. Cartwall API (`cart`)

```javascript
cart.selectTab(tabIndex)    // Select a tab (0-6 → tabs 1-7)
cart.triggerSlot(slotIndex) // Trigger a slot (0-15 → slots 1-16)
```

**Example:**
```javascript
cart.selectTab(0);      // Switch to tab 1
rdm.delay(200);
cart.triggerSlot(3);    // Trigger slot 4
```

---

### 3.4. Aux Player API (`aux`)

```javascript
aux.load(index, filePath)      // Load a file (index: 0-3)
aux.play(index)                // Play the aux player (index: 0-3)
aux.stop(index, fadeMs)        // Stop the aux player (index: 0-3); fadeMs optional, 0 = immediately
aux.setLoop(index, enabled)    // Enable/disable loop
aux.setVolume(index, gain)     // Set volume (0.0-1.0)
```

**Parameters:**
- `index`: 0-3 (Aux 1-4)
- `filePath`: path to a file on disk (e.g. `/music/jingle.mp3`)
- `gain`: value from 0.0 (silent) to 1.0 (100%)
- `fadeMs`: fade-out time in ms before stopping (defaults to 0 = stop immediately).
  Manual Stop from the UI and hardware/MIDI triggers use the global
  `AUX fade out on Stop` setting instead (Auto DJ tab in Settings).

**Example:**
```javascript
aux.load(0, "/audio/jingle.wav");
aux.play(0);
aux.setVolume(0, 0.8);  // 80% volume
rdm.delay(3000);        // Wait 3 seconds
aux.stop(0, 1500);      // Fade out over 1.5 s, then stop
```

---

### 3.5. RDM API (Tools and Integrations)

```javascript
rdm.log(message)              // Log a message
rdm.delay(milliseconds)       // Wait (max 4000ms)
rdm.sendHttp(url)             // Send an HTTP GET request
rdm.sendSerial(deviceId, cmd) // Send a command to a serial device
```

**Parameters:**
- `message`: text to log (appears in the application log)
- `milliseconds`: delay in milliseconds (max 4000ms)
- `url`: URL of an HTTP resource (e.g. `http://api.local/webhook`)
- `deviceId`: device ID (e.g. `matrix_main`)
- `cmd`: command (e.g. `ROUTE 1 2`)

**Example:**
```javascript
rdm.log("Script START_SHOW running");
rdm.delay(500);
rdm.sendHttp("http://dashboard.local/api/onair?status=true");
rdm.log("Notification sent");
```

---

## 4. Facade API Interface (IScriptingFacade)

Below is the full list of available methods (mapping between the JS API and C#):

| JavaScript API | C# Method | Description |
|---|---|---|
| `player.play()` | `PlayerPlay()` | Start the player |
| `player.stop()` | `PlayerStop()` | Stop the player |
| `player.next()` | `PlayerNext()` | Next track |
| `player.pause()` | `PlayerPause()` | Pause |
| `mic.on()` | `MicOn()` | Turn the microphone on |
| `mic.off()` | `MicOff()` | Turn the microphone off |
| `mic.toggle()` | `MicToggle()` | Toggle the microphone |
| `cart.selectTab(int)` | `CartSelectTab(int)` | Select a cartwall tab |
| `cart.triggerSlot(int)` | `CartTriggerSlot(int)` | Trigger a slot |
| `aux.load(int, str)` | `AuxLoad(int, str)` | Load a file |
| `aux.play(int)` | `AuxPlay(int)` | Play |
| `aux.stop(int, int?)` | `AuxStop(int, int)` | Stop (optional fade in ms) |
| `aux.setLoop(int, bool)` | `AuxSetLoop(int, bool)` | Loop |
| `aux.setVolume(int, float)` | `AuxSetVolume(int, float)` | Volume |
| `rdm.log(str)` | `Log(str)` | Logging |
| `rdm.delay(int)` | `Delay(int)` | Delay |
| `rdm.sendHttp(str)` | `SendHttp(str)` | HTTP GET |
| `rdm.sendSerial(str, str)` | `SendSerial(str, str)` | Serial command |

---

## 5. Architecture and Sandboxing

### Environment Isolation

```
┌─────────────────────────────────────────────────┐
│        JavaScript Script (ISOLATED)             │
│                                                  │
│  ✓ Access to RDM API                            │
│  ✓ 5s timeout                                   │
│  ✓ 4MB RAM limit                                │
│  ✗ No access to System.*                        │
│  ✗ No require/import                            │
│  ✗ No access to the file system                 │
│  ✗ No access to network sockets                 │
│                                                  │
└─────────────────────────────────────────────────┘
                       ↑
                       │ (Jint Engine)
                       ↓
┌─────────────────────────────────────────────────┐
│    IScriptingFacade (Safe Facade)               │
└─────────────────────────────────────────────────┘
         ↑             ↑             ↑
         │             │             │
    Player      Cartwall           Mixer
    Service     Service            Service
```

### Resource Guard

| Resource | Limit | Mechanism |
|-------|-------|----------|
| Execution time | 5 seconds | TimeoutInterval (CancellationToken) |
| Memory | 4 MB | LimitMemory - exception on excess |
| Infinite loop | — | StatementsCountOverflow (detects `while(true)`) |

---

## 6. Useful Script Examples

### Example 1: "SHOW START" (Broadcasting Script)

**Scenario:** A script that prepares the studio for broadcast.

```javascript
// Script: SHOW START
// ID: a1b2c3d4-e5f6-7890-abcd-ef1234567890

rdm.log("=== PREPARING SHOW ===");

// Step 1: Turn on the microphone
mic.on();
rdm.log("✓ Microphone on");

// Step 2: Start the player
player.play();
rdm.log("✓ Player started");

// Step 3: Wait for initialisation
rdm.delay(1000);

// Step 4: Load a jingle into AUX 1 (ID: 0)
aux.load(0, "/audio/jingles/station_id.wav");
rdm.log("✓ Jingle loaded into AUX 1");

// Step 5: Notify the external system
rdm.sendHttp("http://dashboard.local/api/show/start");
rdm.log("✓ Dashboard notified");

rdm.log("=== SHOW READY ===");
```

**Trigger:**
```
Trigger mapping:
- Button: MIDI Note 40 (Korg nanoPAD slot 0)
- Action: AutomationRunScript
- Parameter: a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

---

### Example 2: "CONDITIONAL LOGIC"

**Scenario:** A script that makes decisions based on the time of day.

```javascript
// Script: TIME-BASED AUTOMATION
// Plays a different format depending on the time of day

rdm.log("Checking the time...");

const now = new Date();
const hour = now.getHours();

if (hour >= 6 && hour < 12) {
    // MORNING (6:00 - 12:00)
    rdm.log("Morning: upbeat music playlist");
    cart.selectTab(0);  // Tab 1 - Morning
    cart.triggerSlot(0); // Slot 1 - Show opener
    
} else if (hour >= 12 && hour < 18) {
    // AFTERNOON (12:00 - 18:00)
    rdm.log("Afternoon: pop music playlist");
    cart.selectTab(1);  // Tab 2 - Afternoon
    cart.triggerSlot(0);
    
} else if (hour >= 18 && hour < 22) {
    // EVENING (18:00 - 22:00)
    rdm.log("Evening: relaxing music playlist");
    cart.selectTab(2);  // Tab 3 - Evening
    cart.triggerSlot(0);
    
} else {
    // NIGHT (22:00 - 6:00)
    rdm.log("Night: automated music rotation");
    cart.selectTab(3);  // Tab 4 - Night
    cart.triggerSlot(0);
}

rdm.log("✓ Playlist loaded");
```

---

### Example 3: "LOOPS AND INTERACTIONS"

**Scenario:** A script that plays a series of advertising spots.

```javascript
// Script: AD BREAK (10 spots)
// Plays each spot with pauses in between

const spotIds = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
const spotDurationMs = 30000; // 30 seconds per spot

rdm.log("=== AD BREAK ===");
rdm.sendHttp("http://dashboard.local/api/ads/start");

for (let i = 0; i < spotIds.length; i++) {
    const spotId = spotIds[i];
    
    rdm.log("Spot " + (i + 1) + "/" + spotIds.length + " (ID: " + spotId + ")");
    
    // Trigger the cartwall slot (0-9 = slots 1-10)
    cart.triggerSlot(spotId - 1);
    
    // Wait for the spot to finish
    rdm.delay(spotDurationMs);
    
    // Show progress
    if (i < spotIds.length - 1) {
        rdm.log("→ Moving to the next spot");
    }
}

rdm.log("=== AD BREAK FINISHED ===");
rdm.sendHttp("http://dashboard.local/api/ads/end");
```

---

### Example 4: "SERVER INTEGRATION"

**Scenario:** A script that fetches data from an API and reacts to it.

```javascript
// Script: SCHEDULE FROM SERVER
// Fetches the broadcast schedule from a central API

rdm.log("Fetching the schedule from the server...");

// Simulated fetch - in reality this would use fetch (which isn't available here)
// Instead, we send a webhook and wait for a callback
rdm.sendHttp("http://api.server.local/schedule/get?station_id=1");

// Alternative: a direct command to the system
rdm.log("Sending a command to the audio matrix");
rdm.sendSerial("matrix_01", "ROUTE 1 2");  // Route audio from source 1 to output 2

rdm.delay(500);

// Turn on the microphone and start the player
mic.on();
player.play();

rdm.log("✓ System ready for broadcast");
rdm.sendHttp("http://api.server.local/status/update?status=ready");
```

---

### Example 5: "ERROR HANDLING"

**Scenario:** A script with error handling and conditional logic.

```javascript
// Script: SAFE START
// Checks conditions before starting

rdm.log("=== SAFE START ===");

try {
    // Check 1: Is the microphone already on?
    // (in a real scenario we'd have state to check, but for this example...)
    
    rdm.log("Step 1: Checking parameters...");
    
    const maxDelay = 4000;  // Max delay
    const safeDelay = Math.min(1500, maxDelay);
    
    rdm.log("Step 2: Turning on the microphone");
    mic.on();
    
    rdm.log("Step 3: Waiting " + safeDelay + "ms");
    rdm.delay(safeDelay);
    
    rdm.log("Step 4: Starting the player");
    player.play();
    
    rdm.log("✓ START SUCCESSFUL");
    rdm.sendHttp("http://dashboard/log?event=start_success");
    
} catch (error) {
    rdm.log("✗ ERROR: " + error);
    rdm.sendHttp("http://dashboard/log?event=start_failed&error=" + error);
    
    // Immediately shut everything down on error
    player.stop();
    mic.off();
}
```

---

## 7. Advanced Capabilities

### Multiple Aux Operations

```javascript
// Load several files into different aux players
aux.load(0, "/audio/intro.wav");
aux.load(1, "/audio/jingle.wav");
aux.load(2, "/audio/outro.wav");

// Play them in sequence
aux.play(0);  // Intro
rdm.delay(5000);
aux.play(1);  // Jingle
rdm.delay(3000);
aux.play(2);  // Outro
```

### Volume Control

```javascript
// Fade in (gradual increase)
for (let i = 0; i <= 10; i++) {
    const volume = i / 10.0;
    aux.setVolume(0, volume);
    rdm.delay(100);
}

// Fade out (gradual decrease)
for (let i = 10; i >= 0; i--) {
    const volume = i / 10.0;
    aux.setVolume(0, volume);
    rdm.delay(100);
}
```

### Combined Actions

```javascript
// A complex sequence
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

rdm.log("Show transition complete");
```

---

## 8. Limits and Constraints

| Parameter | Limit | Notes |
|----------|-------|--------|
| Execution timeout | 5 seconds | TimeoutInterval (cancelled via CancellationToken) |
| Maximum memory | 4 MB | LimitMemory (throws OutOfMemoryException) |
| Maximum delay | 4000 ms | Protection against blocking |
| Script name length | 200 characters | VARCHAR(200) in the database |
| Script code size | MEDIUMTEXT (~16 MB) | Theoretically a large limit |
| Infinite loops | Detected | StatementsCountOverflow |
| Access to System.* | NONE | Sandbox |
| require/import | NONE | Sandbox |
| File system access | NONE | Sandbox |
| Network sockets | NONE | Only sendHttp |

### Execution Errors

```javascript
// Timeout (5 seconds)
while (true) {
    // ERROR: "Script cancelled (timeout or interruption)"
}

// Memory overflow (4 MB)
let array = [];
for (let i = 0; i < 1000000; i++) {
    array.push(new Array(100000).fill(0));
    // ERROR: Memory overflow
}

// Delay above the limit
rdm.delay(5000);
// OK - will be clamped down to 4000ms
```

---

## 9. Triggering Scripts

### Via a Trigger Mapping

```
Hardware Manager settings:
┌─────────────────────────────────────┐
│ Trigger: MIDI Note 45               │
│ Action: AutomationRunScript          │
│ Parameter: [script GUID]             │
└─────────────────────────────────────┘
```

### Via a Macro

```
Macro "FULL SHOW SEQUENCE"
├─ Step 1: ActionId = AutomationRunScript
│          Parameter = a1b2c3d4-e5f6-7890-abcd-ef1234567890
└─ Step 2: (other actions)
```

### Via the HTTP API

```
POST /api/automation/script/run?id=a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

---

## 10. Monitoring and Debugging

### Logging

Every message from `rdm.log()` goes to:
- The installed-build log file: `C:\ProgramData\RDM\rdm.log`
- The console: if running in debug mode

```javascript
rdm.log("[DEBUG] Variable x = " + x);
rdm.log("[ERROR] Something went wrong!");
rdm.log("[INFO] Script finished successfully");
```

### Performance Monitoring

ScriptRunner logs the execution time:

```
[Information] ScriptRunner: script 'SHOW' finished in 1234ms
[Warning] ScriptRunner: script 'LOGIC' finished with an error: Timeout
```

---

## 11. Practical Tips

### Good Practice ✓

```javascript
// Clear variable names
const slotIndex = 3;
cart.triggerSlot(slotIndex);

// Log key points
rdm.log("Step 1: Initialisation");
rdm.log("Step 2: Execution");

// Respect the limits
if (totalDelay > 4000) {
    rdm.delay(4000);  // Maximum 4000ms
} else {
    rdm.delay(totalDelay);
}
```

### Bad Practice ✗

```javascript
// Infinite loop
while (true) {
    player.play();  // TIMEOUT!
}

// Unclear types
var x = "123";
var y = x + 100;  // JavaScript coercion!

// No logging
mic.on();
player.play();
// How do we know if it worked?
```

---

## 12. Summary

JavaScript in RDM enables:

✅ **Advanced Automation** — conditional logic and loops
✅ **External Integrations** — HTTP and serial devices
✅ **A Safe Sandbox** — protection against data leaks
✅ **Dynamic Code** — no need to recompile the application
✅ **Fast Testing** — change the code in the editor and run it immediately
✅ **Full Control** — access to the entire RDM infrastructure

JavaScript scripts are the ideal tool for radio operators to automate complex broadcast scenarios without needing C#-level programming knowledge.
